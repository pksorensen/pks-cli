using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainAdrSettings : BrainSettings
{
    [CommandOption("--model")]
    [Description("Model name passed to claude (default: haiku).")]
    public string? Model { get; set; }

    [CommandOption("--parallel")]
    [Description("Max parallel claude invocations (default 10).")]
    public int? Parallel { get; set; }

    [CommandOption("--max-adrs")]
    [Description("Cap how many ADRs are AI-rendered in this run.")]
    public int? MaxAdrs { get; set; }

    [CommandOption("--min-cluster-size")]
    [Description("Minimum sessions per cluster to consider for an ADR (default 5 — decisions need evidence).")]
    public int? MinClusterSize { get; set; }

    [CommandOption("--include-tag")]
    [Description("Extra tag to count as architectural (repeatable). Adds to the default allowlist.")]
    public string[]? IncludeTags { get; set; }

    [CommandOption("--tags")]
    [Description("Comma-separated tags that REPLACE the default architectural allowlist.")]
    public string? Tags { get; set; }

    [CommandOption("--no-ai")]
    [Description("Skip claude calls. Writes only the deterministic adr/index.md.")]
    public bool NoAi { get; set; }

    [CommandOption("--max-budget-usd")]
    [Description("Hard dollar cap per invocation (forwarded to claude --max-budget-usd).")]
    public double? MaxBudgetUsd { get; set; }

    [CommandOption("--dry-run")]
    [Description("Plan only — list candidates + estimate; no claude calls.")]
    public bool DryRun { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Skip the cost-confirmation prompt.")]
    public bool Yes { get; set; }
}

public class BrainAdrCommand : AsyncCommand<BrainAdrSettings>
{
    private readonly IBrainAdrPipeline _pipeline;
    private readonly IBrainPathResolver _paths;

    public BrainAdrCommand(IBrainAdrPipeline pipeline, IBrainPathResolver paths)
    {
        _pipeline = pipeline;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainAdrSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain adr[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd);
        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[red]Not inside a git repository.[/] `pks brain adr` writes to `./.pks/brain/adr/`.");
            return 1;
        }

        var options = new BrainAdrOptions
        {
            Model = settings.Model ?? BrainExtractDefaults.Model,
            MaxParallelism = settings.Parallel ?? BrainExtractDefaults.Parallel,
            MaxAdrs = settings.MaxAdrs,
            MinClusterSize = settings.MinClusterSize ?? 5,
            IncludeTags = (settings.IncludeTags ?? Array.Empty<string>()).ToList(),
            Tags = settings.Tags is { Length: > 0 }
                ? settings.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : null,
            NoAi = settings.NoAi,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            DryRun = settings.DryRun,
        };

        AnsiConsole.MarkupLine($"[grey]Synthesis dir:[/] [cyan]{Path.Combine(projectRoot, "synthesis")}[/]");
        AnsiConsole.MarkupLine($"[grey]ADR dir:[/] [cyan]{Path.Combine(projectRoot, "adr")}[/]");
        AnsiConsole.MarkupLine($"[grey]Model:[/] [cyan]{options.Model}[/]  [grey]Parallel:[/] [cyan]{options.MaxParallelism}[/]  [grey]Min cluster size:[/] [cyan]{options.MinClusterSize}[/]");
        if (options.NoAi) AnsiConsole.MarkupLine("[yellow]--no-ai[/]: claude will not be invoked.");
        if (options.DryRun) AnsiConsole.MarkupLine("[yellow]--dry-run[/]: claude will not be invoked.");
        AnsiConsole.WriteLine();

        var plan = await _pipeline.PlanAsync(options);
        if (plan.ClustersDetected == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No clusters.json found.[/] Run [bold]pks brain synth --no-ai[/] first to build the cluster index.");
            return 0;
        }
        AnsiConsole.MarkupLine($"[grey]Allowed tags ({plan.AllowedTags.Count}):[/] " +
            string.Join(", ", plan.AllowedTags.Take(15).Select(t => $"[cyan]{t}[/]")) +
            (plan.AllowedTags.Count > 15 ? $" [grey]…+{plan.AllowedTags.Count - 15} more[/]" : ""));
        AnsiConsole.MarkupLine($"[bold]Plan:[/] {plan.ClustersDetected:N0} cluster(s) detected, {plan.ClustersEligible:N0} match architectural tags + min-size, {plan.ClustersToRender:N0} to render.");
        if (plan.Candidates.Count > 0)
            AnsiConsole.MarkupLine("[grey]Candidates:[/] " + string.Join(", ", plan.Candidates));
        AnsiConsole.MarkupLine($"[bold]Estimated total cost:[/] [green]${plan.EstimatedCostUsd:0.##}[/] [grey]({plan.EstimateBasis})[/]");
        if (plan.EstimatedDuration is { } d)
        {
            var pretty = d.TotalHours >= 1 ? $"{d.Hours}h {d.Minutes:00}m"
                       : d.TotalMinutes >= 1 ? $"{d.Minutes}m {d.Seconds:00}s"
                       : $"{d.Seconds}s";
            AnsiConsole.MarkupLine($"[bold]Estimated wall-clock:[/] [yellow]{pretty}[/] [grey](at parallel={options.MaxParallelism})[/]");
        }
        AnsiConsole.WriteLine();

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[green]Would render {plan.ClustersToRender} ADR(s).[/]");
            return 0;
        }
        if (plan.ClustersToRender == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No architectural clusters at this threshold.[/]");
            AnsiConsole.MarkupLine($"Try [bold]--min-cluster-size 3[/] or [bold]--include-tag <tag>[/] to widen the net.");
            return 0;
        }

        if (!settings.Yes && !options.NoAi
            && plan.ClustersToRender >= 10
            && plan.EstimatedCostUsd >= 1.0)
        {
            var go = AnsiConsole.Confirm(
                $"Proceed with [bold]{plan.ClustersToRender}[/] ADR(s) at estimated [green]${plan.EstimatedCostUsd:0.##}[/]?",
                defaultValue: false);
            if (!go)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/] Re-run with [bold]--max-adrs N[/], [bold]--no-ai[/], or [bold]-y/--yes[/].");
                return 0;
            }
            AnsiConsole.WriteLine();
        }

        var run = await AnsiConsole.Progress()
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
                var progress = new SpectreAdrProgress(ctx);
                return await _pipeline.RunAsync(options, progress);
            });

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Run id[/]",         run.RunId);
        t.AddRow("[grey]Duration[/]",       run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]ADRs written[/]",   run.AdrsWritten.ToString("N0"));
        t.AddRow("[grey]Failed[/]",         run.Failed.ToString("N0"));
        t.AddRow("[grey]adr/index.md[/]",   run.IndexWritten ? "[green]✓ written[/]" : "[grey]skipped[/]");
        t.AddRow("[grey]Input tokens[/]",   FormatTokens(run.TotalInputTokens + run.TotalCacheReadTokens + run.TotalCacheCreationTokens));
        t.AddRow("[grey]Output tokens[/]",  FormatTokens(run.TotalOutputTokens));
        t.AddRow("[grey]Total cost[/]",     $"${run.TotalCostUsd:0.####}");
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
        if (run.AdrsWritten > 0)
            AnsiConsole.MarkupLine($"Wrote [bold]{run.AdrsWritten}[/] ADR(s) to [cyan]{Path.Combine(projectRoot, "adr")}[/].");
        AnsiConsole.MarkupLine($"Open [cyan]{Path.Combine(projectRoot, "adr", "index.md")}[/] for the index.");
        return 0;
    }

    private static string FormatTokens(long n) =>
        n < 10_000   ? n.ToString("N0")
        : n < 1_000_000 ? $"{n / 1000.0:0.#}K"
        :                 $"{n / 1_000_000.0:0.##}M";

    private sealed class SpectreAdrProgress : IBrainAdrProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total;
        private int _completed;
        private int _failed;
        private long _inTok, _outTok, _cacheRead, _cacheCreate;
        private double _cost;
        private double _avgSec;

        public SpectreAdrProgress(ProgressContext ctx) => _ctx = ctx;

        public void Discovered(int totalCalls)
        {
            _total = Math.Max(1, totalCalls);
            _task = _ctx.AddTask(BuildDescription(), maxValue: _total);
        }
        public void Started(string tag) { }
        public void Finished(AdrFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++;
                if (!info.Success) _failed++;
                _inTok += info.InputTokens;
                _outTok += info.OutputTokens;
                _cacheRead += info.CacheReadInputTokens;
                _cacheCreate += info.CacheCreationInputTokens;
                _cost += info.CostUsd;
                if (info.Duration > TimeSpan.Zero)
                {
                    var sec = info.Duration.TotalSeconds;
                    _avgSec = _avgSec <= 0 ? sec : (_avgSec * 0.7 + sec * 0.3);
                }
                if (_task is not null)
                {
                    _task.Value = _completed;
                    _task.Description = BuildDescription();
                }
            }
        }

        private string BuildDescription()
        {
            var totalIn = _inTok + _cacheRead + _cacheCreate;
            var failedSuffix = _failed > 0 ? $" [red]✗{_failed}[/]" : "";
            var eta = _avgSec > 0 && _total > _completed
                ? TimeSpan.FromSeconds((_total - _completed) * _avgSec) : (TimeSpan?)null;
            var etaStr = eta is null ? "" :
                  eta.Value.TotalMinutes >= 1 ? $" · ETA [yellow]{eta.Value.Minutes}m {eta.Value.Seconds:00}s[/]"
                : $" · ETA [yellow]{eta.Value.Seconds}s[/]";
            var projected = _completed > 0 && _total > _completed
                ? $" · proj:[yellow]${_cost / _completed * _total:0.##}[/]" : "";
            return $"[cyan]ADRs[/] {_completed,3}/{_total}"
                 + $" · in:[bold]{FormatTokens(totalIn)}[/] out:[bold]{FormatTokens(_outTok)}[/]"
                 + $" · [green]${_cost:0.####}[/]" + projected + etaStr + failedSuffix;
        }
    }
}
