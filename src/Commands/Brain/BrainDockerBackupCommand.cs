using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainDockerBackupSettings : BrainSettings
{
    [CommandOption("-o|--out <DIR>")]
    [Description("Where to copy the data. Defaults to ~/.pks-cli/brain/rescued/.")]
    public string? Out { get; set; }

    [CommandOption("--volume <NAME>")]
    [Description("Only these volumes. Repeatable. Substring match against the volume name.")]
    public string[] Volume { get; set; } = [];

    [CommandOption("--all")]
    [Description("Copy the whole volume, not just its session data. Credentials are excluded either way.")]
    public bool All { get; set; }

    [CommandOption("--dry-run")]
    [Description("List the volumes that would be copied and what is in them, without copying.")]
    public bool DryRun { get; set; }

    [CommandOption("--no-register")]
    [Description("Copy the volumes but leave them out of ingest. The copy is then a cold archive only.")]
    public bool NoRegister { get; set; }
}

/// `pks brain docker backup` — get the data out of the volumes before something
/// takes them.
///
/// The volumes this finds are almost always dangling: their containers are gone,
/// `docker volume prune` would delete the lot, and no host-side scan can see
/// them. Copying them onto a real filesystem is the part that cannot be undone
/// later, so it comes before wiring them into ingest.
///
/// A backup is a copy onto somewhere more durable and usually more shared, so
/// `.credentials.json` — a live OAuth refresh token, sitting in every Claude
/// config volume — is excluded in both modes. Nothing in a transcript needs it.
public sealed class BrainDockerBackupCommand : AsyncCommand<BrainDockerBackupSettings>
{
    private readonly IDockerSessionScanner _docker;
    private readonly IBrainPathResolver _paths;
    private readonly IBrainRootRegistry _roots;

    public BrainDockerBackupCommand(
        IDockerSessionScanner docker,
        IBrainPathResolver paths,
        IBrainRootRegistry roots)
    {
        _docker = docker;
        _paths = paths;
        _roots = roots;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainDockerBackupSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain docker backup[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        if (!await _docker.IsDockerAvailableAsync())
        {
            AnsiConsole.MarkupLine("[yellow]No docker daemon reachable.[/] Nothing to back up.");

            return 1;
        }

        var inventory = await _docker.InventoryAsync();

        var candidates = inventory.Candidates
            .Where(name => settings.Volume.Length == 0 ||
                           settings.Volume.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{inventory.AllVolumes.Count:N0} volume(s) on this daemon, none matching.[/]");

            return 0;
        }

        var destination = Path.GetFullPath(
            settings.Out is { Length: > 0 } o ? o : Path.Combine(_paths.GlobalRoot, "rescued"));

        var dangling = new HashSet<string>(inventory.Dangling, StringComparer.Ordinal);

        IReadOnlyList<DockerSessionFile> files = [];
        await AnsiConsole.Status()
            .StartAsync(
                $"Mounting {candidates.Count:N0} volume(s) read-only…",
                async _ => files = await _docker.ScanAsync(candidates));

        var groups = DockerScan.ByProject(files);
        if (groups.Count > 0)
        {
            var table = new Table().Border(TableBorder.MinimalHeavyHead);
            table.AddColumn("Tool");
            table.AddColumn("Project (path inside container)");
            table.AddColumn(new TableColumn("Vols").RightAligned());
            table.AddColumn(new TableColumn("Sessions").RightAligned());
            table.AddColumn(new TableColumn("Size").RightAligned());

            foreach (var group in groups)
            {
                table.AddRow(
                    group.Tool,
                    $"[bold]{Markup.Escape(group.ProjectDir)}[/]",
                    group.Volumes.ToString("N0"),
                    group.Sessions.ToString("N0"),
                    FormatBytes(group.Bytes));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        // The scan only recognises session files. `--all` copies whole volumes, so
        // a volume with nothing recognisable is still worth taking then.
        var withData = new HashSet<string>(DockerScan.Volumes(files), StringComparer.Ordinal);
        var toCopy = settings.All ? candidates : candidates.Where(withData.Contains).ToList();

        AnsiConsole.MarkupLine($"[grey]Source     [/] {toCopy.Count:N0} volume(s), " +
                               $"{toCopy.Count(dangling.Contains):N0} of them dangling");
        AnsiConsole.MarkupLine($"[grey]Destination[/] {Markup.Escape(destination)}");
        AnsiConsole.MarkupLine($"[grey]Scope      [/] " +
                               (settings.All
                                   ? "[cyan]whole volume[/] [grey](minus credentials)[/]"
                                   : "[cyan]session data only[/] [grey](projects, sessions, tool-output, opencode.db)[/]"));
        AnsiConsole.WriteLine();

        if (toCopy.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Nothing to copy.[/]");

            return 0;
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Dry run:[/] would copy [cyan]{toCopy.Count:N0}[/] volume(s) into " +
                $"{Markup.Escape(destination)}.");

            return 0;
        }

        var result = DockerRescueResult.Empty;
        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async progressContext =>
            {
                var task = progressContext.AddTask("[cyan]Copying volumes[/]", maxValue: toCopy.Count);
                var progress = new Progress<string>(_ => task.Increment(1));

                result = await _docker.RescueAsync(toCopy, destination, settings.All, progress);
                task.Value = task.MaxValue;
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]Copied[/] {result.Files:N0} file(s), {FormatBytes(result.Bytes)}, " +
            $"from {result.Volumes:N0} volume(s) into {Markup.Escape(destination)}.");

        if (result.Skipped > 0)
            AnsiConsole.MarkupLine($"[grey]{result.Skipped:N0} volume(s) held nothing worth copying.[/]");

        foreach (var (volume, error) in result.Failed)
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(volume)}[/] [grey]{Markup.Escape(error)}[/]");

        AnsiConsole.WriteLine();

        if (settings.NoRegister)
        {
            AnsiConsole.MarkupLine(
                "[grey]--no-register: the copy is a cold mirror. Nothing will read it until you register " +
                "it. Re-running is safe and only overwrites what changed.[/]");

            return result.Failed.Count > 0 ? 1 : 0;
        }

        // Registering is the step that turns a mirror into data. Without it the
        // 28 rescued volumes sit on disk exactly as invisible as they were inside
        // the daemon — which was the original complaint.
        var added = _roots.AddRange(result.Rescued.Select(kv => new BrainSessionRoot(
            kv.Value,
            $"docker:{kv.Key}",
            DateTimeOffset.UtcNow,
            $"rescued by pks brain docker backup{(settings.All ? " --all" : "")}")));

        AnsiConsole.MarkupLine(
            $"[green]Registered[/] {result.Rescued.Count:N0} root(s) [grey]({added:N0} new)[/] in " +
            $"{Markup.Escape(_roots.RegistryPath)}.");
        AnsiConsole.MarkupLine(
            "[grey]Their sessions are now discoverable — each event carries [/]" +
            "[cyan]origin: docker:<volume>[/][grey], and a session already known from the host is read " +
            "once, not twice. Run [/][bold]pks brain ingest[/][grey] to pick them up.[/]");

        return result.Failed.Count > 0 ? 1 : 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):N1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        >= 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes} B",
    };
}
