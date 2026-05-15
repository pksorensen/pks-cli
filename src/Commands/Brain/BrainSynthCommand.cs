using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainSynthSettings : BrainSettings
{
    [CommandOption("--model")]
    [Description("Model name passed to claude (default: haiku — cheap, fast, good enough).")]
    public string? Model { get; set; }

    [CommandOption("--parallel")]
    [Description("Max parallel claude invocations (default 10).")]
    public int? Parallel { get; set; }

    [CommandOption("--max-clusters")]
    [Description("Cap how many clusters get an AI narrative. Others still appear in clusters.json.")]
    public int? MaxClusters { get; set; }

    [CommandOption("--min-cluster-size")]
    [Description("Minimum sessions per cluster to surface (default 2 — singletons usually aren't a theme).")]
    public int? MinClusterSize { get; set; }

    [CommandOption("--no-ai")]
    [Description("Skip claude calls. Writes clusters.json + a deterministic themes.md skeleton only.")]
    public bool NoAi { get; set; }

    [CommandOption("--max-budget-usd")]
    [Description("Hard dollar cap per invocation (forwarded to claude --max-budget-usd).")]
    public double? MaxBudgetUsd { get; set; }

    [CommandOption("--dry-run")]
    [Description("Plan only — print eligible clusters + estimated cost; no claude calls.")]
    public bool DryRun { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Skip the cost-confirmation prompt for large runs.")]
    public bool Yes { get; set; }
}

public class BrainSynthCommand : AsyncCommand<BrainSynthSettings>
{
    private readonly IBrainSynthesisPipeline _pipeline;
    private readonly IBrainPathResolver _paths;

    public BrainSynthCommand(IBrainSynthesisPipeline pipeline, IBrainPathResolver paths)
    {
        _pipeline = pipeline;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSynthSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain synth[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd);
        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[red]Not inside a git repository.[/] `pks brain synth` writes to `./.pks/brain/synthesis/`, which only exists in a project.");
            return 1;
        }

        var options = new BrainSynthOptions
        {
            Model = settings.Model ?? BrainExtractDefaults.Model,
            MaxParallelism = settings.Parallel ?? BrainExtractDefaults.Parallel,
            MaxClusters = settings.MaxClusters,
            MinClusterSize = settings.MinClusterSize ?? 2,
            NoAi = settings.NoAi,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            DryRun = settings.DryRun,
        };

        AnsiConsole.MarkupLine($"[grey]Extracts dir:[/] [cyan]{Path.Combine(projectRoot, "extracts")}[/]");
        AnsiConsole.MarkupLine($"[grey]Synthesis dir:[/] [cyan]{Path.Combine(projectRoot, "synthesis")}[/]");
        AnsiConsole.MarkupLine($"[grey]Model:[/] [cyan]{options.Model}[/]  [grey]Parallel:[/] [cyan]{options.MaxParallelism}[/]  [grey]Min cluster size:[/] [cyan]{options.MinClusterSize}[/]");
        if (options.NoAi) AnsiConsole.MarkupLine("[yellow]--no-ai[/]: claude will not be invoked.");
        if (options.DryRun) AnsiConsole.MarkupLine("[yellow]--dry-run[/]: claude will not be invoked.");
        AnsiConsole.WriteLine();

        var plan = await _pipeline.PlanAsync(options);
        AnsiConsole.MarkupLine($"[bold]Plan:[/] {plan.ExtractsFound:N0} extract(s) → {plan.ClustersFound:N0} cluster(s), {plan.TotalAiCalls:N0} AI call(s).");
        if (plan.ExtractsFound == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No extracts found.[/] Run [bold]pks brain extract[/] first to produce per-session summaries.");
            return 0;
        }
        if (plan.ClustersFound == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No clusters meet the minimum-size threshold.[/] Lower --min-cluster-size to surface single-session themes.");
            return 0;
        }
        if (plan.TopClusters.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Top clusters:[/] " + string.Join(", ", plan.TopClusters));
        }
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
            AnsiConsole.MarkupLine($"[green]Would synthesise {plan.ClustersAiSummarized} cluster(s).[/]");
            return 0;
        }

        if (!settings.Yes && !options.NoAi
            && plan.TotalAiCalls >= 10
            && plan.EstimatedCostUsd >= 1.0)
        {
            var go = AnsiConsole.Confirm(
                $"Proceed with [bold]{plan.TotalAiCalls}[/] AI call(s) at estimated [green]${plan.EstimatedCostUsd:0.##}[/]?",
                defaultValue: false);
            if (!go)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/] Re-run with [bold]--max-clusters N[/], [bold]--no-ai[/], or [bold]-y/--yes[/].");
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
                var progress = new SpectreSynthProgress(ctx);
                return await _pipeline.RunAsync(options, progress);
            });

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Run id[/]",             run.RunId);
        t.AddRow("[grey]Duration[/]",           run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]Extracts read[/]",      run.ExtractsRead.ToString("N0"));
        t.AddRow("[grey]Clusters found[/]",     run.ClustersFound.ToString("N0"));
        t.AddRow("[grey]Clusters AI'd[/]",      run.ClustersWritten.ToString("N0"));
        t.AddRow("[grey]Failed AI calls[/]",    run.Failed.ToString("N0"));
        t.AddRow("[grey]themes.md[/]",          run.ThemesWritten ? "[green]✓ written[/]" : "[grey]skipped[/]");
        t.AddRow("[grey]bad-habits.md[/]",      run.HabitsWritten ? "[green]✓ written[/]" : "[grey]skipped[/]");
        t.AddRow("[grey]clusters.json[/]",      run.ClustersJsonWritten ? "[green]✓ written[/]" : "[grey]skipped[/]");
        t.AddRow("[grey]Input tokens[/]",       FormatTokens(run.TotalInputTokens + run.TotalCacheReadTokens + run.TotalCacheCreationTokens));
        t.AddRow("[grey]Output tokens[/]",      FormatTokens(run.TotalOutputTokens));
        t.AddRow("[grey]Total cost[/]",         $"${run.TotalCostUsd:0.####}");
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Wrote synthesis to [cyan]{Path.Combine(projectRoot, "synthesis")}[/].");
        return 0;
    }

    private static string FormatTokens(long n) =>
        n < 10_000   ? n.ToString("N0")
        : n < 1_000_000 ? $"{n / 1000.0:0.#}K"
        :                 $"{n / 1_000_000.0:0.##}M";

    private sealed class SpectreSynthProgress : IBrainSynthProgress
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

        public SpectreSynthProgress(ProgressContext ctx) => _ctx = ctx;

        public void Discovered(int totalCalls)
        {
            _total = Math.Max(1, totalCalls);
            _task = _ctx.AddTask(BuildDescription(), maxValue: _total);
        }
        public void Started(string stage) { }
        public void Finished(SynthFinishedInfo info)
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
                ? TimeSpan.FromSeconds((_total - _completed) * _avgSec)
                : (TimeSpan?)null;
            var etaStr = eta is null ? "" :
                  eta.Value.TotalHours >= 1 ? $" · ETA [yellow]{eta.Value.Hours}h {eta.Value.Minutes:00}m[/]"
                : eta.Value.TotalMinutes >= 1 ? $" · ETA [yellow]{eta.Value.Minutes}m {eta.Value.Seconds:00}s[/]"
                : $" · ETA [yellow]{eta.Value.Seconds}s[/]";
            var projected = _completed > 0 && _total > _completed
                ? $" · proj:[yellow]${_cost / _completed * _total:0.##}[/]"
                : "";
            return $"[cyan]Synthesising[/] {_completed,3}/{_total}"
                 + $" · in:[bold]{FormatTokens(totalIn)}[/] out:[bold]{FormatTokens(_outTok)}[/]"
                 + $" · [green]${_cost:0.####}[/]"
                 + projected
                 + etaStr
                 + failedSuffix;
        }
    }
}
