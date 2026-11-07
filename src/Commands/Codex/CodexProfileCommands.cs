using System.ComponentModel;
using PKS.Infrastructure.Services.Agent.Codex;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

/// <summary>
/// <c>pks codex profiles</c> — what is connected on this machine. No network.
/// </summary>
[Description("List the Codex profiles connected from platforms")]
public sealed class CodexProfilesCommand(IAnsiConsole console) : Command<CodexProfilesCommand.Settings>
{
    public sealed class Settings : CodexBranchSettings
    {
        [CommandOption("--paths")]
        [Description("Also show the file each profile lives in")]
        public bool Paths { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var ledger = CodexProfileLedger.Load();
        var rows = ledger.AllProfiles().ToList();

        if (rows.Count == 0)
        {
            console.MarkupLine("[yellow]No profiles connected.[/]");
            console.MarkupLine("[dim]Connect one with [bold]pks codex connect agentics.dk[/].[/]");

            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Profile");
        table.AddColumn("Name");
        table.AddColumn("Platform");
        table.AddColumn("Active");
        if (settings.Paths) table.AddColumn("Layer file");

        foreach (var (origin, source, profile) in rows.OrderBy(r => r.Profile.Namespace, StringComparer.Ordinal))
        {
            var active = string.Equals(ledger.Active, profile.Namespace, StringComparison.OrdinalIgnoreCase);
            var cells = new List<string>
            {
                $"[bold]{Markup.Escape(profile.Namespace)}[/]",
                Markup.Escape(profile.DisplayName ?? profile.Id),
                Markup.Escape(source.DisplayName ?? origin),
                active ? "[green]yes[/]" : "[dim]—[/]",
            };

            if (settings.Paths)
                cells.Add($"[dim]{Markup.Escape(profile.LayerFile ?? "—")}[/]");

            table.AddRow(cells.ToArray());
        }

        console.Write(table);
        console.MarkupLine("[dim]Use one for a single run:[/] [bold]codex -p <profile>[/]");

        // "Active" has a consequence a reader cannot see from their config, so it is said here:
        // Codex merges the base config into EVERY -p invocation.
        if (ledger.Active is { Length: > 0 })
            console.MarkupLine(
                $"[dim]{Markup.Escape(ledger.Active)} is in config.toml, so its servers also appear " +
                "under every other [bold]-p[/]. [bold]pks codex use --none[/] clears it.[/]");

        return 0;
    }
}

/// <summary>
/// <c>pks codex use &lt;profile&gt;</c> — change what a bare <c>codex</c> gets. No network.
///
/// <para>
/// This is the command someone runs several times a day, which is why it touches nothing remote.
/// </para>
/// </summary>
[Description("Make a connected profile the one a bare `codex` uses")]
public sealed class CodexUseCommand(IAnsiConsole console) : Command<CodexUseCommand.Settings>
{
    public sealed class Settings : CodexBranchSettings
    {
        [CommandArgument(0, "[profile]")]
        [Description("The profile namespace, as `pks codex profiles` shows it")]
        public string? Profile { get; set; }

        [CommandOption("--none")]
        [Description("Clear the active profile, leaving config.toml to you and `-p` clean")]
        public bool None { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (!settings.None && string.IsNullOrWhiteSpace(settings.Profile))
        {
            console.MarkupLine("[red]Which profile?[/] [dim]`pks codex profiles` lists them, or pass --none.[/]");

            return 1;
        }

        var ledger = CodexProfileLedger.Load();
        var (ok, message) = CodexProfileWriter.Use(settings.None ? null : settings.Profile, ledger);

        if (!ok)
        {
            console.MarkupLine($"[red]{Markup.Escape(message)}[/]");

            return 1;
        }

        ledger.Save();
        console.MarkupLine($"[green]{Markup.Escape(message)}[/]");

        return 0;
    }
}

/// <summary>
/// <c>pks codex remove &lt;profile&gt;</c> — take a profile off this machine. No network.
/// </summary>
[Description("Remove a connected Codex profile from this machine")]
public sealed class CodexRemoveCommand(IAnsiConsole console) : Command<CodexRemoveCommand.Settings>
{
    public sealed class Settings : CodexBranchSettings
    {
        [CommandArgument(0, "<profile>")]
        [Description("The profile namespace, as `pks codex profiles` shows it")]
        public string Profile { get; set; } = string.Empty;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var ledger = CodexProfileLedger.Load();

        if (!CodexProfileWriter.Remove(settings.Profile, ledger))
        {
            console.MarkupLine($"[red]No connected profile called '{Markup.Escape(settings.Profile)}'.[/]");
            console.MarkupLine("[dim]`pks codex profiles` lists them.[/]");

            return 1;
        }

        ledger.Save();
        console.MarkupLine($"[green]Removed {Markup.Escape(settings.Profile)}.[/]");

        // The variable may have other writers, and a credential is cheap to re-mint but
        // impossible to recover, so it is named rather than deleted.
        console.MarkupLine("[dim]Any environment variable it used was left in place.[/]");

        return 0;
    }
}
