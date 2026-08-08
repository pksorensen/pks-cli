using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Discovery;

namespace PKS.Commands.Brain;

public sealed class BrainRestoreSettings : BrainSettings
{
    [CommandOption("--from-remote")]
    [Description("Take the catalog from the server instead of this machine's export manifest.")]
    public bool FromRemote { get; set; }

    [CommandOption("-e|--endpoint|--server <URL>")]
    [Description("Where to restore from. Defaults to the last endpoint pushed to, then https://agentics.dk.")]
    public string? Endpoint { get; set; }

    [CommandOption("--no-login")]
    [Description("Never stop to sign in. For cron and CI, where a device-code prompt would hang forever.")]
    public bool NoLogin { get; set; }

    [CommandOption("--client-id <ID>")]
    [Description("OAuth client_id for a receiver that will not accept our client-id URL.")]
    public string? ClientId { get; set; }

    [CommandOption("-t|--token <TOKEN>")]
    [Description("Upload token. Otherwise PKS_BRAIN_TOKEN, then the credentials from `pks agentics init`.")]
    public string? Token { get; set; }

    [CommandOption("-k|--kind <KIND>")]
    [Description("Only this blob kind: claude-transcript, codex-rollout, codex-archived, opencode-session, opencode-tool-output.")]
    public string? Kind { get; set; }

    [CommandOption("-s|--since <WINDOW>")]
    [Description("Only blobs captured since 30d, 12h, or an ISO date.")]
    public string? Since { get; set; }

    [CommandOption("-o|--target <DIR>")]
    [Description("Where to write. Defaults to ./brain-restore/.")]
    public string? Target { get; set; }

    [CommandOption("--in-place")]
    [Description("Write back into the live tool directories. Prompts first, and only for kinds whose location is reconstructible.")]
    public bool InPlace { get; set; }

    [CommandOption("--overwrite")]
    [Description("Replace files that already exist at the destination.")]
    public bool Overwrite { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show what would be written without touching a file.")]
    public bool DryRun { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Skip the --in-place confirmation. For scripts.")]
    public bool Yes { get; set; }

    [CommandOption("--quiet")]
    [Description("Suppress per-file output — just print the summary.")]
    public bool Quiet { get; set; }
}

/// `pks brain restore` — brings the raw sources back.
///
/// This is the command the rest of the design leans on. Everything else asks a
/// developer to send data somewhere; this one proves it comes back, in its
/// original bytes, verified against the hash it was stored under. It runs
/// against the local blob store by default, so it also works with no account
/// and no network — the backup is useful before any of the server exists.
public sealed class BrainRestoreCommand(
    IBrainRestoreService restore,
    IBrainTokenResolver tokens,
    IBrainExportService export,
    IBrainPathResolver paths,
    IAgenticPlatformDiscovery discovery) : AsyncCommand<BrainRestoreSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrainRestoreSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain restore[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        DateTimeOffset? since = null;
        if (settings.Since is { Length: > 0 } sinceText && !TryParseSince(sinceText, out since))
        {
            AnsiConsole.MarkupLine($"[red]Unknown --since:[/] {Markup.Escape(sinceText)}. Expected 30d, 12h or an ISO date.");

            return 1;
        }

        var manifest = await export.LoadManifestAsync();
        var server = settings.Endpoint ?? manifest.Endpoint ?? PushOptions.DefaultEndpoint;

        // Local restores never touch the network, so they must not pay for
        // discovery either — the whole point of the local store is that it works
        // with no account and no connection.
        BrainEndpoint? endpoint = null;
        BrainToken? token = null;
        if (settings.FromRemote)
        {
            (endpoint, _) = await BrainConnection.ResolveAsync(discovery, server);

            var request = new BrainTokenRequest(settings.Token, endpoint,
                AllowInteractiveLogin: !settings.NoLogin && AnsiConsole.Profile.Capabilities.Interactive,
                settings.ClientId,
                BrainConnection.ShowDeviceCode,
                BrainConnection.ShowAuthorizeUrl);

            token = await tokens.ResolveAsync(request);
            if (token is null)
            {
                BrainConnection.PrintNoCredential(endpoint, request.AllowInteractiveLogin);
                AnsiConsole.MarkupLine("  Without [bold]--from-remote[/] the local blob store is used and no credential is needed.");

                return 1;
            }
            if (token.Warning is { Length: > 0 } warning)
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");
        }
        var origin = endpoint?.Origin ?? BrainPushService.NormalizeEndpoint(server);

        var target = Path.GetFullPath(settings.Target ?? Path.Combine(Directory.GetCurrentDirectory(), "brain-restore"));

        AnsiConsole.MarkupLine($"[grey]Catalog   [/] [cyan]{Markup.Escape(settings.FromRemote ? origin : paths.ExportManifestPath)}[/]");
        AnsiConsole.MarkupLine($"[grey]Kind      [/] [cyan]{settings.Kind ?? "every kind"}[/]");
        AnsiConsole.MarkupLine($"[grey]Since     [/] [cyan]{(since is { } s ? s.ToString("yyyy-MM-dd HH:mm") + " UTC" : "everything")}[/]");
        AnsiConsole.MarkupLine(settings.InPlace
            ? $"[grey]Writing to[/] [yellow]the live tool directories[/]"
            : $"[grey]Writing to[/] [cyan]{Markup.Escape(target)}[/]");
        AnsiConsole.WriteLine();

        if (settings.InPlace && !settings.DryRun && !settings.Yes && !ConfirmInPlace())
        {
            AnsiConsole.MarkupLine("[grey]Nothing was written.[/]");

            return 0;
        }

        var options = new RestoreOptions
        {
            Endpoint = origin,
            ApiBase = endpoint?.ApiBase,
            Token = token?.Value ?? "",
            FromRemote = settings.FromRemote,
            Kind = settings.Kind?.Trim().ToLowerInvariant(),
            Since = since,
            TargetDir = target,
            InPlace = settings.InPlace,
            DryRun = settings.DryRun,
            Overwrite = settings.Overwrite,
        };

        RestoreRun run;
        try
        {
            run = await restore.RunAsync(options, settings.Quiet ? NullRestoreProgress.Instance : new SpectreRestoreProgress());
        }
        catch (BrainPushException ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Code)}[/] — {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"  {Advice(ex)}");

            return 1;
        }

        AnsiConsole.WriteLine();

        if (run.BlobsListed == 0)
        {
            AnsiConsole.MarkupLine("[yellow]The catalog is empty.[/]");
            AnsiConsole.MarkupLine(settings.FromRemote
                ? "  Nothing matching those filters has been pushed at [bold]--level all[/]. Blobs never travel below it."
                : "  Run [bold]pks brain export --level all[/] first, or add [bold]--from-remote[/] to ask the server.");

            return 0;
        }

        if (settings.DryRun)
        {
            var table = new Table().Border(TableBorder.MinimalHeavyHead);
            table.AddColumn("kind"); table.AddColumn("destination"); table.AddColumn(new TableColumn("size").RightAligned()); table.AddColumn("source");
            foreach (var item in run.Plan.Take(40))
            {
                table.AddRow(
                    Markup.Escape(item.Kind),
                    Markup.Escape(item.Destination),
                    FormatBytes(item.Bytes),
                    item.Local ? "[green]local[/]" : "[cyan]download[/]");
            }
            AnsiConsole.Write(table);
            if (run.Plan.Count > 40)
                AnsiConsole.MarkupLine($"[grey]… and {run.Plan.Count - 40:N0} more[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold]Dry run:[/] would write [cyan]{run.Plan.Count:N0}[/] file(s), {FormatBytes(run.Plan.Sum(p => p.Bytes))}.");

            return 0;
        }

        var summary = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        summary.AddColumn(""); summary.AddColumn("");
        summary.AddRow("[grey]Duration[/]", run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        summary.AddRow("[grey]Catalog[/]", $"{run.BlobsListed:N0} blob(s)");
        summary.AddRow("[grey]Restored[/]", $"{run.Restored:N0}  [grey]({FormatBytes(run.BytesWritten)})[/]");
        if (run.FromLocalStore > 0)
            summary.AddRow("[grey]From local store[/]", $"{run.FromLocalStore:N0}  [grey](no download needed)[/]");
        if (run.Downloaded > 0)
            summary.AddRow("[grey]Downloaded[/]", $"{run.Downloaded:N0}  [grey]({FormatBytes(run.BytesDownloaded)} on the wire)[/]");
        if (run.SkippedExisting > 0)
            summary.AddRow("[grey]Already on disk[/]", $"{run.SkippedExisting:N0}  [grey](use --overwrite to replace)[/]");
        if (run.SkippedNoLocation > 0)
            summary.AddRow("[grey]No in-place home[/]", $"[yellow]{run.SkippedNoLocation:N0}[/]");
        if (run.HashMismatches > 0)
            summary.AddRow("[grey]Hash mismatches[/]", $"[red]{run.HashMismatches:N0}[/]");
        AnsiConsole.Write(summary);

        if (run.SkippedNoLocation > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]{run.SkippedNoLocation:N0} blob(s) have no reconstructible in-place location.[/]");
            AnsiConsole.MarkupLine(
                "  Only the basename is stored — a path names projects and people — which is enough for opencode's flat");
            AnsiConsole.MarkupLine(
                "  spill directory and not for a Claude transcript's per-project folder. Re-run without [bold]--in-place[/].");
        }

        if (run.Truncated)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[yellow]The server returned a full page — there is more behind these filters.[/] Narrow with --kind or --since.");
        }

        if (run.HashMismatches > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[red]Some blobs did not hash to their content address and were not written.[/] Nothing on disk was changed by them.");
        }

        if (run.Failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]{run.Failures.Count} problem(s):[/]");
            foreach (var failure in run.Failures.Take(10))
                AnsiConsole.MarkupLine($"  [grey]•[/] {Markup.Escape(failure)}");
            if (run.Failures.Count > 10)
                AnsiConsole.MarkupLine($"  [grey]… and {run.Failures.Count - 10} more[/]");
        }

        if (run.Restored > 0 && !settings.InPlace)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"Restored to [bold]{Markup.Escape(target)}[/]");
            AnsiConsole.MarkupLine("Rebuild the normalized layer from them with [bold]pks brain export --force[/].");
        }

        return run.Failures.Count > 0 || run.HashMismatches > 0 ? 1 : 0;
    }

    /// The one destructive thing this command can do, spelled out before it
    /// happens rather than in the help text.
    private static bool ConfirmInPlace()
    {
        AnsiConsole.MarkupLine("[yellow]--in-place writes into the directories your tools are reading right now.[/]");
        AnsiConsole.MarkupLine("[grey]Existing files are left alone unless --overwrite is also given.[/]");
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm("Continue?", defaultValue: false);
    }

    private static string Advice(BrainPushException ex) => ex.Code switch
    {
        "unauthorized" => "Sign in again with [bold]pks agentics init[/], or mint a fresh upload token.",
        "level_exceeded" => "Raw blobs need a token whose maximum level is [bold]all[/]. Mint one on the settings page.",
        "unreachable" => "Check the endpoint and your network. Without [bold]--from-remote[/] this works offline.",
        _ => "Nothing was written; re-running is safe.",
    };

    private static bool TryParseSince(string s, out DateTimeOffset? value)
    {
        value = null;
        s = s.Trim();
        if (s.Length == 0) return false;

        if (s.Length >= 2 && char.IsLetter(s[^1]) && double.TryParse(
                s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan delta = s[^1] switch
            {
                'd' or 'D' => TimeSpan.FromDays(n),
                'h' or 'H' => TimeSpan.FromHours(n),
                'm' or 'M' => TimeSpan.FromMinutes(n),
                's' or 'S' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
            if (delta == TimeSpan.Zero) return false;
            value = DateTimeOffset.UtcNow - delta;

            return true;
        }

        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            value = parsed;

            return true;
        }

        return false;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
    };
}

internal sealed class SpectreRestoreProgress : IRestoreProgress
{
    public void Cataloged(int blobs, long bytes, string source) =>
        AnsiConsole.MarkupLine($"[grey]Catalog[/] {blobs:N0} blob(s)");

    public void Restoring(string sha, string destination, bool local) { }

    public void Restored(string sha, long bytes) =>
        AnsiConsole.MarkupLine($"  [green]✓[/] [grey]{Markup.Escape(Short(sha))}[/] {bytes:N0} B");

    public void Skipped(string sha, string reason) =>
        AnsiConsole.MarkupLine($"  [grey]·[/] [grey]{Markup.Escape(Short(sha))} — {Markup.Escape(reason)}[/]");

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
