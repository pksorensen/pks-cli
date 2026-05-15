using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainIngestSettings : BrainSettings
{
    [CommandOption("-p|--project")]
    [Description("Match against the encoded project-slug substring (e.g. agentic-live).")]
    public string? Project { get; set; }

    [CommandOption("--since")]
    [Description("Only ingest sessions newer than this. Accepts 7d, 24h, 30m, or an ISO date.")]
    public string? Since { get; set; }

    [CommandOption("--limit")]
    [Description("Cap the number of session files processed (after filtering).")]
    public int? Limit { get; set; }

    [CommandOption("--force")]
    [Description("Ignore the per-session cursor and re-parse every matched file.")]
    public bool Force { get; set; }

    [CommandOption("--parallel")]
    [Description("Override the max degree of parallelism (default: CPU count).")]
    public int? Parallel { get; set; }

    [CommandOption("--quiet")]
    [Description("Suppress the progress bar — just print the final summary.")]
    public bool Quiet { get; set; }
}

public class BrainIngestCommand : AsyncCommand<BrainIngestSettings>
{
    private readonly IBrainIngestPipeline _pipeline;
    private readonly IBrainPathResolver _paths;

    public BrainIngestCommand(IBrainIngestPipeline pipeline, IBrainPathResolver paths)
    {
        _pipeline = pipeline;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainIngestSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain ingest[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        DateTime? since = null;
        if (settings.Since is { Length: > 0 } sinceStr)
        {
            if (!TryParseSince(sinceStr, out since))
            {
                AnsiConsole.MarkupLine($"[red]Could not parse --since value:[/] {sinceStr}");
                return 1;
            }
        }

        var options = new IngestOptions
        {
            ProjectFilter = settings.Project,
            SinceUtc = since,
            Limit = settings.Limit,
            Force = settings.Force,
            MaxParallelism = settings.Parallel ?? Environment.ProcessorCount,
        };

        AnsiConsole.MarkupLine($"[grey]Reading from[/] [cyan]{_paths.ClaudeProjectsRoot}[/]");
        AnsiConsole.MarkupLine($"[grey]Writing to  [/] [cyan]{_paths.GlobalRoot}[/]");
        if (options.ProjectFilter is not null)
            AnsiConsole.MarkupLine($"[grey]Project filter:[/] {options.ProjectFilter}");
        if (options.SinceUtc is { } sUtc)
            AnsiConsole.MarkupLine($"[grey]Since:[/] {sUtc:yyyy-MM-dd HH:mm} UTC");
        if (options.Limit is { } lim)
            AnsiConsole.MarkupLine($"[grey]Limit:[/] {lim} files");
        if (options.Force)
            AnsiConsole.MarkupLine($"[yellow]--force: ignoring per-session cursor.[/]");
        AnsiConsole.WriteLine();

        Infrastructure.Services.Brain.Models.IngestRun run;
        if (settings.Quiet)
        {
            run = await _pipeline.RunAsync(options, NullIngestProgress.Instance);
        }
        else
        {
            run = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx =>
                {
                    var progress = new SpectreIngestProgress(ctx);
                    return await _pipeline.RunAsync(options, progress);
                });
        }

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Run id[/]",             run.RunId);
        t.AddRow("[grey]Duration[/]",           run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]Files scanned[/]",      run.FilesScanned.ToString("N0"));
        t.AddRow("[grey]Files ingested[/]",     run.FilesIngested.ToString("N0"));
        t.AddRow("[grey]Skipped (up-to-date)[/]", run.FilesSkippedUpToDate.ToString("N0"));
        t.AddRow("[grey]Files failed[/]",       run.FilesFailed.ToString("N0"));
        t.AddRow("[grey]Prompts appended[/]",   run.PromptsAppended.ToString("N0"));
        t.AddRow("[grey]Tool calls appended[/]", run.ToolCallsAppended.ToString("N0"));
        t.AddRow("[grey]File ops appended[/]",  run.FileOpsAppended.ToString("N0"));
        t.AddRow("[grey]Errors appended[/]",    run.ErrorsAppended.ToString("N0"));
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: [bold]pks brain status[/] for totals.");
        return 0;
    }

    private static bool TryParseSince(string s, out DateTime? value)
    {
        value = null;
        s = s.Trim();
        if (s.Length == 0) return false;

        // 7d / 24h / 30m / 45s relative offsets
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
            value = DateTime.UtcNow - delta;
            return true;
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            value = dt;
            return true;
        }
        return false;
    }

    // ── Spectre Progress adapter ──────────────────────────────────────────────

    private sealed class SpectreIngestProgress : IIngestProgress
    {
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _completed;

        public SpectreIngestProgress(ProgressContext ctx) => _ctx = ctx;

        public void Discovered(int totalFiles)
        {
            _task = _ctx.AddTask("[cyan]Ingesting sessions[/]", maxValue: Math.Max(1, totalFiles));
        }

        public void Filtered(int eligibleFiles, int skippedByCursor)
        {
            if (_task is not null)
            {
                _task.MaxValue = Math.Max(1, eligibleFiles);
                _task.Description = $"[cyan]Ingesting {eligibleFiles} sessions[/] [grey](skipping {skippedByCursor} up-to-date)[/]";
            }
        }

        public void Started(string file) { }

        public void Finished(string file, bool ingested, bool error)
        {
            Interlocked.Increment(ref _completed);
            if (_task is not null) _task.Value = _completed;
        }
    }
}
