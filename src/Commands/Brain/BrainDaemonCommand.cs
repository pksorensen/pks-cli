using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainDaemonInstallSettings : BrainSettings
{
    [CommandOption("-l|--level <LEVEL>")]
    [Description("What the daily export should include: all, prompts or metrics. Default: metrics.")]
    public string Level { get; set; } = AsfLevel.Metrics;

    [CommandOption("-e|--endpoint <URL>")]
    [Description("Where the daily push should send. Default: https://agentics.dk.")]
    public string Endpoint { get; set; } = PushOptions.DefaultEndpoint;

    // The value name cannot be "HH:MM" — Spectre rejects ':' in a template.
    [CommandOption("--at <TIME>")]
    [Description("Local time of day to run, HH:MM. Default 03:30.")]
    public string? At { get; set; }

    [CommandOption("--exe <PATH>")]
    [Description("The pks executable the job should call. Defaults to the running one, which is wrong when pks is launched through a wrapper.")]
    public string? Exe { get; set; }

    [CommandOption("--no-ingest")]
    [Description("Back up only — skip the local `brain ingest` step.")]
    public bool NoIngest { get; set; }

    [CommandOption("--dry-run")]
    [Description("Print the script and unit files without writing or activating anything.")]
    public bool DryRun { get; set; }

    [CommandOption("--force")]
    [Description("Replace an existing scheduled job.")]
    public bool Force { get; set; }
}

/// `pks brain daemon install` — the daily job.
///
/// Daily, not hourly, and not weekly: opencode deletes spilled tool output after
/// a hardcoded 7 days, so a week is exactly the interval at which data starts
/// disappearing. Daily leaves six days of slack for a closed laptop.
public sealed class BrainDaemonInstallCommand(IBrainDaemonService daemon) : AsyncCommand<BrainDaemonInstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrainDaemonInstallSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain daemon install[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        string level;
        try
        {
            level = AsfLevel.Parse(settings.Level);
        }
        catch (ArgumentException)
        {
            AnsiConsole.MarkupLine($"[red]Unknown --level:[/] {Markup.Escape(settings.Level)}. Expected all, prompts or metrics.");

            return 1;
        }

        var at = new TimeOnly(3, 30);
        if (settings.At is { Length: > 0 } atText &&
            !TimeOnly.TryParseExact(atText.Trim(), "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
        {
            AnsiConsole.MarkupLine($"[red]Could not parse --at:[/] {Markup.Escape(settings.At)}. Expected HH:MM, e.g. 03:30.");

            return 1;
        }

        var options = new DaemonOptions
        {
            Level = level,
            Endpoint = BrainPushService.NormalizeEndpoint(settings.Endpoint),
            At = at,
            IncludeIngest = !settings.NoIngest,
            ExecutablePath = settings.Exe,
            Force = settings.Force,
        };

        var plan = daemon.Plan(options);

        AnsiConsole.MarkupLine($"[grey]Scheduler[/] [cyan]{plan.Scheduler}[/]");
        AnsiConsole.MarkupLine($"[grey]Runs at  [/] [cyan]{at:HH\\:mm}[/] local, daily");
        AnsiConsole.MarkupLine($"[grey]Level    [/] [cyan]{level}[/]");
        AnsiConsole.MarkupLine($"[grey]Endpoint [/] [cyan]{Markup.Escape(options.Endpoint)}[/]");
        AnsiConsole.MarkupLine($"[grey]Script   [/] {Markup.Escape(plan.ScriptPath)}");
        AnsiConsole.MarkupLine($"[grey]Log      [/] {Markup.Escape(plan.LogPath)}");
        AnsiConsole.WriteLine();

        if (settings.DryRun)
        {
            AnsiConsole.Write(new Panel(Markup.Escape(plan.ScriptBody.TrimEnd()))
                .Header(Markup.Escape(plan.ScriptPath)).BorderColor(Color.Grey));
            foreach (var (path, body) in plan.Units)
            {
                AnsiConsole.Write(new Panel(Markup.Escape(body.TrimEnd()))
                    .Header(Markup.Escape(path)).BorderColor(Color.Grey));
            }
            foreach (var command in plan.Activation)
                AnsiConsole.MarkupLine($"[grey]would run:[/] {Markup.Escape(command)}");

            return 0;
        }

        var result = await daemon.InstallAsync(options);

        // No scheduler is the devcontainer case: no user systemd session, no
        // crontab binary. The script is still written — it is the useful half —
        // and the caller is told exactly how to fire it from outside.
        if (plan.Scheduler == DaemonScheduler.None)
        {
            foreach (var path in result.Wrote)
                AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(path)}");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]No scheduler on this machine[/] (looked for systemd --user, launchd, cron, schtasks).");
            AnsiConsole.MarkupLine("The script is ready; schedule it from wherever this box is started. Either:");
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(BrainDaemonService.CronLine(plan.ScriptPath, at))}[/]   [grey](in a crontab that can see this filesystem)[/]");
            AnsiConsole.MarkupLine(
                $"  [bold]docker exec -u $(id -u) <container> {Markup.Escape(plan.ScriptPath)}[/]   [grey](from the host's scheduler, for a devcontainer)[/]");
            AnsiConsole.MarkupLine($"Or just run it yourself: [bold]{Markup.Escape(plan.ScriptPath)}[/]");

            return 1;
        }

        foreach (var path in result.Wrote)
            AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(path)}");
        foreach (var command in result.Ran)
            AnsiConsole.MarkupLine($"[grey]ran[/] {Markup.Escape(command)}");

        foreach (var problem in result.Problems)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(problem)}[/]");

        AnsiConsole.WriteLine();
        if (result.ManualStep is { Length: > 0 } manual)
        {
            AnsiConsole.MarkupLine("[yellow]The files are in place but the scheduler did not accept them.[/] Finish with:");
            AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(manual)}[/]");

            return 1;
        }

        AnsiConsole.MarkupLine("[green]Installed.[/] Check it with [bold]pks brain daemon status[/].");
        if (level != AsfLevel.Full)
        {
            AnsiConsole.MarkupLine(
                "[grey]Note: blobs — including opencode's spilled tool output — are only backed up at [/][bold]--level all[/][grey].[/]");
        }

        return 0;
    }
}

/// `pks brain daemon status` — is the backup actually running?
public sealed class BrainDaemonStatusCommand(IBrainDaemonService daemon) : AsyncCommand<BrainSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrainSettings settings)
    {
        var status = await daemon.StatusAsync();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain daemon status[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Scheduler[/]", status.Scheduler.ToString());
        t.AddRow("[grey]Installed[/]", status.Installed ? "[green]yes[/]" : "[yellow]no[/]");
        t.AddRow("[grey]Enabled[/]", status.Enabled ? "[green]yes[/]" : "[yellow]no[/]");
        if (status.NextRun is { Length: > 0 })
            t.AddRow("[grey]Timer[/]", Markup.Escape(status.NextRun));
        if (status.LastRun is { Length: > 0 })
            t.AddRow("[grey]Last log write[/]", status.LastRun);
        t.AddRow("[grey]Script[/]", Markup.Escape(status.ScriptPath));
        t.AddRow("[grey]Log[/]", Markup.Escape(status.LogPath));
        AnsiConsole.Write(t);

        AnsiConsole.WriteLine();
        var p = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        p.AddColumn(""); p.AddColumn("");
        p.AddRow("[grey]Endpoint[/]", status.Endpoint is { Length: > 0 } e ? Markup.Escape(e) : "[grey]never pushed[/]");
        p.AddRow("[grey]Chunks pushed[/]", status.ChunksUploaded.ToString("N0"));
        p.AddRow("[grey]Chunks pending[/]",
            status.ChunksPending > 0 ? $"[yellow]{status.ChunksPending:N0}[/]" : "0");
        if (status.BlobsPending > 0)
            p.AddRow("[grey]Blobs pending[/]", $"[yellow]{status.BlobsPending:N0}[/]");
        p.AddRow("[grey]Last upload[/]",
            status.LastUpload is { } last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "[yellow]never[/]");
        AnsiConsole.Write(p);

        if (status.LogTail is { Length: > 0 } tail)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(Markup.Escape(tail)).Header("last run").BorderColor(Color.Grey));
        }

        if (!status.Installed)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Not scheduled yet: [bold]pks brain daemon install --level all[/]");
        }

        return 0;
    }
}

/// `pks brain daemon uninstall` — stop the schedule. Data stays.
public sealed class BrainDaemonUninstallCommand(IBrainDaemonService daemon) : AsyncCommand<BrainSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrainSettings settings)
    {
        var result = await daemon.UninstallAsync();

        AnsiConsole.WriteLine();
        foreach (var command in result.Ran)
            AnsiConsole.MarkupLine($"[grey]ran[/] {Markup.Escape(command)}");
        foreach (var path in result.Removed)
            AnsiConsole.MarkupLine($"[green]removed[/] {Markup.Escape(path)}");
        foreach (var problem in result.Problems)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(problem)}[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]The schedule is gone.[/] Exported chunks, blobs and anything already pushed are untouched.");

        return 0;
    }
}
