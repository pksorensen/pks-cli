using System.Diagnostics;
using PKS.Attributes;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

[ToolRegistryExport(
    "pks/claude/backup",
    Title = "pks claude backup",
    Description = "Backs up the ~/.claude/ directory (sessions, projects, settings) to registered rsync targets over SSH.",
    Tags = ["claude", "backup", "rsync", "ssh"],
    Status = "stable",
    Icon = "cloud-upload",
    Usage = "pks claude backup",
    Examples = ["pks claude backup"]
)]
public class ClaudeBackupCommand : AsyncCommand<CommandSettings>
{
    private readonly IRsyncTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public ClaudeBackupCommand(IRsyncTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, CommandSettings settings)
    {
        var targets = await _configService.ListTargetsAsync();

        if (targets.Count == 0)
        {
            _console.MarkupLine("[yellow]No rsync targets registered.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks rsync init[/] to add your NAS or backup host.[/]");
            return 1;
        }

        if (!RsyncAvailable())
        {
            _console.MarkupLine("[red]rsync not found on PATH.[/]");
            _console.MarkupLine("[dim]Install it with: [cyan]sudo apt install rsync[/] or [cyan]brew install rsync[/][/]");
            return 1;
        }

        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude") + Path.DirectorySeparatorChar;

        if (!Directory.Exists(source.TrimEnd(Path.DirectorySeparatorChar)))
        {
            _console.MarkupLine("[red]~/.claude/ directory not found.[/]");
            return 1;
        }

        _console.Write(new Rule("[bold cyan]Claude Backup[/]").RuleStyle("cyan dim"));
        _console.WriteLine();
        _console.MarkupLine($"[dim]Source:[/] [cyan]{source.EscapeMarkup()}[/]");
        _console.MarkupLine($"[dim]Targets:[/] {targets.Count}");
        _console.WriteLine();

        var results = new List<(string Label, bool Success, string? Stats, TimeSpan Duration)>();

        foreach (var target in targets)
        {
            var label = target.Label ?? $"{target.Username}@{target.Host}";
            var dest = $"{target.Username}@{target.Host}:{target.RemotePath}";

            var sshArgs = BuildSshArgs(target);
            var rsyncArgs = $"-avz --delete -e \"ssh {sshArgs}\" \"{source}\" \"{dest}\"";

            var sw = Stopwatch.StartNew();
            string? statsLine = null;
            bool success = false;
            var outputLines = new List<string>();

            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Syncing to [cyan]{label.EscapeMarkup()}[/]...", async _ =>
                {
                    var psi = new ProcessStartInfo("rsync", rsyncArgs)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    var p = Process.Start(psi);
                    if (p == null) return;

                    var stdout = await p.StandardOutput.ReadToEndAsync();
                    await p.WaitForExitAsync();

                    outputLines.AddRange(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                    success = p.ExitCode == 0;

                    // rsync stats are at the end: "sent X bytes  received Y bytes"
                    statsLine = outputLines.LastOrDefault(l =>
                        l.StartsWith("sent ") || l.StartsWith("total size"));
                });

            sw.Stop();
            results.Add((label, success, statsLine, sw.Elapsed));

            if (success)
                _console.MarkupLine($"[green]✓[/] [bold]{label.EscapeMarkup()}[/] — {sw.Elapsed.TotalSeconds:F1}s");
            else
            {
                _console.MarkupLine($"[red]✗[/] [bold]{label.EscapeMarkup()}[/] — failed");
                var stderr = outputLines.Where(l => l.Contains("error") || l.Contains("failed")).Take(3);
                foreach (var l in stderr)
                    _console.MarkupLine($"  [red dim]{l.EscapeMarkup()}[/]");
            }
        }

        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Target[/]")
            .AddColumn(new TableColumn("[dim]Status[/]").Centered())
            .AddColumn("[dim]Duration[/]")
            .AddColumn("[dim]Stats[/]");

        foreach (var (lbl, ok, stats, dur) in results)
        {
            table.AddRow(
                lbl.EscapeMarkup(),
                ok ? "[green]OK[/]" : "[red]FAILED[/]",
                $"{dur.TotalSeconds:F1}s",
                stats?.EscapeMarkup() ?? "[dim]-[/]");
        }

        _console.Write(table);

        return results.All(r => r.Success) ? 0 : 1;
    }

    private static string BuildSshArgs(RsyncTarget target)
    {
        var parts = new List<string> { $"-p {target.Port}", "-o BatchMode=yes", "-o StrictHostKeyChecking=accept-new" };
        if (!string.IsNullOrEmpty(target.KeyPath))
            parts.Insert(0, $"-i \"{target.KeyPath}\"");
        return string.Join(" ", parts);
    }

    private static bool RsyncAvailable()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("rsync", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit();
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
