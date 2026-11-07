using System.ComponentModel;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Agent.Setup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

public sealed class CodexConnectSettings : CodexBranchSettings
{
    [CommandArgument(0, "[url]")]
    [Description("The platform to connect to, e.g. agentics.dk or http://localhost:3000")]
    public string? Url { get; set; }

    [CommandOption("--use <ID>")]
    [Description("Pick this profile without prompting (for scripts and re-runs)")]
    public string? Use { get; set; }

    [CommandOption("--all")]
    [Description("Re-run every source already connected on this machine")]
    public bool All { get; set; }

    [CommandOption("--prune")]
    [Description("Also remove profiles the platform no longer publishes")]
    public bool Prune { get; set; }

    [CommandOption("--login")]
    [Description("Force a fresh browser sign-in instead of reusing a cached token")]
    public bool ForceLogin { get; set; }

    [CommandOption("--activate")]
    [Description("Also make the chosen profile what a bare `codex` gets (see the note it prints)")]
    public bool Activate { get; set; }
}

/// <summary>
/// <c>pks codex connect &lt;url&gt;</c> — discover a platform, sign in, pick a profile, write it.
///
/// <para>
/// <b>This is the update path too.</b> There is no separate refresh command, because a second
/// command is a second thing to remember and the work is identical: fetch, diff by name, rewrite.
/// Re-running is how a reader picks up a project that was added or a marketplace that was enabled.
/// </para>
///
/// <para>
/// Protocol: ASP v1, <c>docs/specs/agent-setup/</c> in the agentic-live-www workspace.
/// </para>
/// </summary>
[Description("Configure Codex from a platform that speaks the Agent Setup Protocol")]
public sealed class CodexConnectCommand(IAgentSetupClient client, IAnsiConsole console)
    : AsyncCommand<CodexConnectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CodexConnectSettings settings)
    {
        var ledger = CodexProfileLedger.Load();

        var origins = settings.All
            ? ledger.Sources.Keys.ToList()
            : settings.Url is { Length: > 0 } ? [settings.Url] : [];

        if (origins.Count == 0)
        {
            if (settings.All)
            {
                console.MarkupLine("[yellow]No platforms connected yet.[/]");
                console.MarkupLine("[dim]Connect one with [bold]pks codex connect agentics.dk[/].[/]");

                return 0;
            }

            console.MarkupLine("[red]Which platform?[/] e.g. [bold]pks codex connect agentics.dk[/]");

            return 1;
        }

        var failures = 0;
        var changed = false;
        foreach (var origin in origins)
        {
            // One platform failing must not cost the others. Name it, carry on.
            var (code, wrote) = await ConnectOneAsync(origin, settings, ledger);
            if (code != 0) failures++;
            changed |= wrote;
        }

        // Only persist when something was actually applied. A run that failed at discovery —
        // offline, wrong URL, platform down — has nothing to record, and saving anyway is how
        // an unparseable ledger becomes an empty one: Load() returns empty, the run fails, and
        // Save() writes that emptiness back over the real thing.
        if (changed) ledger.Save();

        return failures == 0 ? 0 : 1;
    }

    private async Task<(int Code, bool Wrote)> ConnectOneAsync(
        string url, CodexConnectSettings settings, CodexProfileLedger ledger)
    {
        console.MarkupLine($"[dim]Looking for agent-setup on[/] [bold]{Markup.Escape(url)}[/][dim]…[/]");

        var disco = await client.DiscoverAsync(url);
        if (!disco.Ok)
        {
            console.MarkupLine($"[red]{Markup.Escape(disco.Error ?? "Discovery failed.")}[/]");

            return (1, false);
        }

        var doc = disco.Document!;
        if (doc.PublishesProvider == false)
            console.MarkupLine(
                "[dim]This platform configures context (MCP servers, marketplaces, model policy) " +
                "and not the model — your own Codex model login is left alone.[/]");

        var result = await client.ProfilesAsync(
            disco, "codex", settings.ForceLogin,
            authorizeUrl =>
            {
                console.MarkupLine("[dim]Opening your browser to sign in. If it does not open:[/]");
                console.WriteLine(authorizeUrl);
            });

        if (!result.Ok)
        {
            console.MarkupLine($"[red]{Markup.Escape(result.Error ?? "Could not read profiles.")}[/]");

            return (1, false);
        }

        var list = result.List!;
        var sourceId = list.Source!.Id;

        if (list.Profiles.Count == 0)
        {
            console.MarkupLine(
                $"[yellow]{Markup.Escape(disco.Origin)} published no profiles for you.[/]");
            console.MarkupLine("[dim]Nothing was changed.[/]");

            return (0, false);
        }

        var who = list.Subject?.Handle ?? list.Subject?.DisplayName;
        console.MarkupLine(
            $"[green]Signed in[/]{(who is null ? "" : $" as [bold]{Markup.Escape(who)}[/]")}" +
            $" [dim]({list.Profiles.Count} profile(s) available)[/]");

        // Already-connected profiles are REFRESHED, not re-chosen. A re-run is an update, and a
        // reader who connected three projects expects all three to pick up a new marketplace —
        // not to be asked again and have the other two go stale.
        var known = ledger.Sources.TryGetValue(disco.Origin, out var ledgered)
            ? ledgered.Profiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        var refreshing = settings.Use is null or ""
            ? list.Profiles.Where(p => known.Contains(p.Id)).ToList()
            : [];

        List<AspProfile> toApply;
        if (refreshing.Count > 0)
        {
            toApply = refreshing;
            console.MarkupLine(
                $"[dim]Refreshing {refreshing.Count} already-connected profile(s). " +
                "Add [bold]--use <id>[/] to connect another.[/]");
        }
        else
        {
            var chosen = ChooseProfile(list, settings.Use);
            if (chosen is null) return (1, false);
            toApply = [chosen];
        }

        CodexApplyResult? applied = null;
        foreach (var profile in toApply)
        {
            applied = CodexProfileWriter.Apply(
                new CodexApplyRequest(
                    Origin: disco.Origin,
                    SourceId: sourceId,
                    SourceDisplayName: list.Source.DisplayName,
                    Issuer: result.Issuer,
                    ClientId: result.ClientId,
                    Profile: profile,
                    MakeActive: settings.Activate),
                ledger);

            foreach (var note in applied.Notes) console.MarkupLine($"[dim]{Markup.Escape(note)}[/]");
        }

        var chosenLast = toApply[^1];

        if (settings.Prune)
        {
            var publishedIds = list.Profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in CodexProfileWriter.Prune(disco.Origin, publishedIds, ledger))
                console.MarkupLine($"[dim]Removed [bold]{Markup.Escape(removed)}[/], no longer published.[/]");
        }

        var ns = applied!.Namespace;
        console.WriteLine();
        if (toApply.Count == 1)
            console.MarkupLine($"[green]Ready.[/] Use it with [bold]codex -p {Markup.Escape(ns)}[/]");
        else
            console.MarkupLine(
                $"[green]Ready.[/] {toApply.Count} profiles updated: " +
                Markup.Escape(string.Join(", ", toApply.Select(p =>
                    CodexProfileApplier.NamespaceFor(sourceId, p.Id)))));

        // Codex authenticates each HTTP MCP server itself, on first use, and the reader has to
        // complete that once per server. Saying so here is the difference between "it works" and
        // "the tools are missing and I cannot see why".
        var httpServers = (chosenLast.McpServers ?? []).Where(s => !string.IsNullOrEmpty(s.Url)).ToList();
        if (httpServers.Count > 0)
        {
            console.MarkupLine(
                $"[dim]Each server signs in on its own the first time. To do it now:[/] " +
                $"[bold]codex -p {Markup.Escape(ns)} mcp login {Markup.Escape(httpServers[0].Name)}[/]");
        }

        if (!settings.Activate)
            console.MarkupLine(
                "[dim]A bare [bold]codex[/] is untouched. [bold]pks codex use[/] changes that — " +
                "it also means this profile's servers appear under every other [bold]-p[/], " +
                "because Codex merges the base config into all of them.[/]");

        return (0, true);
    }

    /// <summary>
    /// The picker. Preselects the platform's <c>default</c>, and skips the prompt when
    /// <c>--use</c> names a profile or there is only one.
    /// </summary>
    internal AspProfile? ChooseProfile(AspProfileList list, string? requested)
    {
        if (requested is { Length: > 0 })
        {
            var match = list.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, requested, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                console.MarkupLine($"[red]No profile '{Markup.Escape(requested)}' here.[/]");
                console.MarkupLine($"[dim]Available: {Markup.Escape(string.Join(", ", list.Profiles.Select(p => p.Id)))}[/]");

                return null;
            }

            return match;
        }

        if (list.Profiles.Count == 1) return list.Profiles[0];

        // Non-interactive: a prompt with nobody to answer it hangs a script forever.
        if (!console.Profile.Capabilities.Interactive)
        {
            var fallback = list.Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, list.Default, StringComparison.OrdinalIgnoreCase)) ?? list.Profiles[0];

            console.MarkupLine(
                $"[yellow]Not a terminal, so taking the default:[/] [bold]{Markup.Escape(fallback.Id)}[/] " +
                "[dim](pass --use <id> to be explicit)[/]");

            return fallback;
        }

        var choices = BuildPickerChoices(list);
        var prompt = new SelectionPrompt<string> { Title = "Which profile?", PageSize = 15 };
        foreach (var (group, labels) in choices.Groups) prompt.AddChoiceGroup(group, labels);

        return choices.ByLabel[console.Prompt(prompt)];
    }

    /// <summary>
    /// Everything about the picker except the prompt itself: the grouping, the ordering, the
    /// "(default)" marker and the label-to-profile map.
    ///
    /// <para>
    /// Separate from <see cref="ChooseProfile"/> because a <c>SelectionPrompt</c> drives a live
    /// render loop that cannot be fed from a test console, and the decisions worth testing are
    /// all here rather than in Spectre.
    /// </para>
    /// </summary>
    internal static (List<(string Group, List<string> Labels)> Groups, Dictionary<string, AspProfile> ByLabel)
        BuildPickerChoices(AspProfileList list)
    {
        var byLabel = new Dictionary<string, AspProfile>(StringComparer.Ordinal);
        var groups = new List<(string, List<string>)>();

        foreach (var group in list.Profiles
                     .GroupBy(p => p.Group ?? "other")
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var labels = new List<string>();
            foreach (var profile in group.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                var isDefault = string.Equals(profile.Id, list.Default, StringComparison.OrdinalIgnoreCase);
                var label = $"{profile.DisplayName ?? profile.Id}{(isDefault ? " (default)" : "")}";

                // Two projects may legitimately share a display name across owners, and a
                // SelectionPrompt keyed by string would then map both rows to one profile.
                while (byLabel.ContainsKey(label)) label += " ";

                byLabel[label] = profile;
                labels.Add(label);
            }

            groups.Add((group.Key, labels));
        }

        return (groups, byLabel);
    }
}
