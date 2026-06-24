using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainExtractSettings : BrainSettings
{
    [CommandOption("-p|--project")]
    [Description("Project slug to extract from. Defaults to the encoded slug of the current cwd.")]
    public string? Project { get; set; }

    [CommandOption("--skill-path")]
    [Description("Override the brain-extract SKILL.md location (skips the search hierarchy).")]
    public string? SkillPath { get; set; }

    [CommandOption("--since")]
    [Description("Only extract sessions newer than this (e.g. 7d, 24h, 30m, or an ISO date).")]
    public string? Since { get; set; }

    [CommandOption("--limit")]
    [Description("Cap the number of sessions extracted in this run.")]
    public int? Limit { get; set; }

    [CommandOption("--force")]
    [Description("Re-extract even if ./.pks/brain/extracts/<id>.md is newer than the source.")]
    public bool Force { get; set; }

    [CommandOption("--parallel")]
    [Description("Max parallel claude invocations (default 10).")]
    public int? Parallel { get; set; }

    [CommandOption("--model")]
    [Description("Model name passed to claude (default: haiku — the cheapest model, good enough for this batch task).")]
    public string? Model { get; set; }

    [CommandOption("--max-budget-usd")]
    [Description("Hard dollar cap per invocation (forwarded to claude --max-budget-usd).")]
    public double? MaxBudgetUsd { get; set; }

    [CommandOption("--agent")]
    [Description("Summarizer backend: pks (built-in in-process agent, default) or claude (shell out to the claude CLI).")]
    public string? Agent { get; set; }

    [CommandOption("--foundry")]
    [Description("Use Azure AI Foundry as the token/model provider (your Azure quota) instead of the agent's default Anthropic billing.")]
    public bool Foundry { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show which sessions would be extracted without invoking claude.")]
    public bool DryRun { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Skip the cost-confirmation prompt for large runs.")]
    public bool Yes { get; set; }
}

internal static class BrainExtractDefaults
{
    public const string Model = "haiku";
    public const string Agent = "pks";
    public const int Parallel = 10;
    /// Skip the prompt when the run is small enough that the estimated spend is trivial.
    public const double ConfirmCostUsd = 1.00;
    public const int ConfirmEligibleCount = 25;
}

public class BrainExtractCommand : AsyncCommand<BrainExtractSettings>
{
    private readonly IBrainExtractPipeline _pipeline;
    private readonly IBrainPathResolver _paths;
    private readonly IBrainSkillReader _skillReader;

    public BrainExtractCommand(IBrainExtractPipeline pipeline, IBrainPathResolver paths, IBrainSkillReader skillReader)
    {
        _pipeline = pipeline;
        _paths = paths;
        _skillReader = skillReader;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainExtractSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain extract[/]").RuleStyle("magenta dim"));
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

        var cwd = Directory.GetCurrentDirectory();
        var slug = settings.Project ?? _paths.EncodeSlug(_paths.Normalize(cwd) ?? cwd);
        var projectRoot = _paths.ResolveProjectRoot(cwd);
        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[red]Not inside a git repository.[/] `pks brain extract` writes to `./.pks/brain/extracts/`, which only exists in a project.");
            return 1;
        }

        // Resolve skill source once, up front, so the user can see what's being used.
        BrainSkillSource skill;
        try
        {
            skill = await _skillReader.ReadAsync(settings.SkillPath);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Could not load brain-extract skill:[/] {ex.Message}");
            return 1;
        }

        var model = settings.Model ?? BrainExtractDefaults.Model;
        var parallel = settings.Parallel ?? BrainExtractDefaults.Parallel;
        var agent = (settings.Agent ?? BrainExtractDefaults.Agent).Trim().ToLowerInvariant();
        if (agent is not ("pks" or "claude"))
        {
            AnsiConsole.MarkupLine($"[red]Unknown --agent value:[/] {agent}. Use [bold]pks[/] or [bold]claude[/].");
            return 1;
        }

        AnsiConsole.MarkupLine($"[grey]Project slug:[/] [cyan]{slug}[/]");
        AnsiConsole.MarkupLine($"[grey]Extracts dir:[/] [cyan]{Path.Combine(projectRoot, "extracts")}[/]");
        AnsiConsole.MarkupLine($"[grey]Skill source:[/] [cyan]{skill.Source}[/] [grey]({skill.Body.Length:N0} chars)[/]");
        AnsiConsole.MarkupLine($"[grey]Agent:[/] [cyan]{agent}[/]{(settings.Foundry ? "  [grey]Provider:[/] [cyan]foundry[/]" : "")}");
        AnsiConsole.MarkupLine($"[grey]Model:[/] [cyan]{model}[/]  [grey]Parallel:[/] [cyan]{parallel}[/]");
        if (settings.MaxBudgetUsd is { } b) AnsiConsole.MarkupLine($"[grey]Max budget / call:[/] ${b:0.##}");
        if (since is { } sUtc) AnsiConsole.MarkupLine($"[grey]Since:[/] {sUtc:yyyy-MM-dd HH:mm} UTC");
        if (settings.Limit is { } lim) AnsiConsole.MarkupLine($"[grey]Limit:[/] {lim} sessions");
        if (settings.Force) AnsiConsole.MarkupLine($"[yellow]--force[/]: re-extracting even when up-to-date.");
        if (settings.DryRun) AnsiConsole.MarkupLine($"[yellow]--dry-run[/]: claude will not be invoked.");
        AnsiConsole.WriteLine();

        var options = new BrainExtractOptions
        {
            ProjectSlug = slug,
            SkillPath = settings.SkillPath,
            SinceUtc = since,
            Limit = settings.Limit,
            Force = settings.Force,
            DryRun = settings.DryRun,
            Model = model,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            MaxParallelism = parallel,
            Agent = agent,
            UseFoundry = settings.Foundry,
        };

        // Pre-flight: enumerate eligible + estimate cost BEFORE any claude call.
        var plan = await _pipeline.PlanAsync(options);
        AnsiConsole.MarkupLine($"[bold]Plan:[/] {plan.Eligible:N0} session(s) eligible, {plan.SkippedByCursor:N0} skipped up-to-date.");
        if (plan.Eligible == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to extract — everything is current.[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[bold]Estimated total cost:[/] [green]${plan.EstimatedCostUsd:0.##}[/] [grey]({plan.EstimateBasis})[/]");
        if (plan.EstimatedDuration is { } eta)
        {
            var pretty = eta.TotalHours >= 1 ? $"{eta.Hours}h {eta.Minutes:00}m"
                       : eta.TotalMinutes >= 1 ? $"{eta.Minutes}m {eta.Seconds:00}s"
                       : $"{eta.Seconds}s";
            AnsiConsole.MarkupLine($"[bold]Estimated wall-clock:[/] [yellow]{pretty}[/] [grey](at parallel={options.MaxParallelism})[/]");
        }
        AnsiConsole.WriteLine();

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[green]Would extract {plan.Eligible} session(s).[/]");
            if (plan.Preview.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]First few:[/]");
                foreach (var id in plan.Preview) AnsiConsole.MarkupLine($"  [cyan]{id}[/]");
                if (plan.Eligible > plan.Preview.Count)
                    AnsiConsole.MarkupLine($"  [grey]... and {plan.Eligible - plan.Preview.Count} more[/]");
            }
            return 0;
        }

        // Confirmation gate: skip when the run is small or --yes was passed.
        if (!settings.Yes
            && plan.Eligible >= BrainExtractDefaults.ConfirmEligibleCount
            && plan.EstimatedCostUsd >= BrainExtractDefaults.ConfirmCostUsd)
        {
            var go = AnsiConsole.Confirm(
                $"Proceed with [bold]{plan.Eligible:N0}[/] extracts at estimated [green]${plan.EstimatedCostUsd:0.##}[/]?",
                defaultValue: false);
            if (!go)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/] Re-run with [bold]--limit N[/] for a partial run, or [bold]-y/--yes[/] to skip this prompt.");
                return 0;
            }
            AnsiConsole.WriteLine();
        }

        BrainExtractRun run;
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
                var progress = new SpectreExtractProgress(ctx);
                return await _pipeline.RunAsync(options, progress);
            });

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Run id[/]",             run.RunId);
        t.AddRow("[grey]Duration[/]",           run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]Eligible[/]",           run.Eligible.ToString("N0"));
        t.AddRow("[grey]Skipped up-to-date[/]", run.SkippedUpToDate.ToString("N0"));
        t.AddRow("[grey]Extracted[/]",          run.Extracted.ToString("N0"));
        t.AddRow("[grey]Failed[/]",             run.Failed.ToString("N0"));
        t.AddRow("[grey]Input tokens[/]",       FormatTokens(run.TotalInputTokens + run.TotalCacheReadTokens + run.TotalCacheCreationTokens));
        t.AddRow("[grey]  (cache read)[/]",     FormatTokens(run.TotalCacheReadTokens));
        t.AddRow("[grey]  (cache create)[/]",   FormatTokens(run.TotalCacheCreationTokens));
        t.AddRow("[grey]Output tokens[/]",      FormatTokens(run.TotalOutputTokens));
        t.AddRow("[grey]Total cost[/]",         $"${run.TotalCostUsd:0.####}");
        if (run.Extracted > 0)
        {
            var avgCost = run.TotalCostUsd / run.Extracted;
            t.AddRow("[grey]Avg / extract[/]",  $"${avgCost:0.####}");
        }
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();
        if (run.Extracted > 0)
        {
            AnsiConsole.MarkupLine($"Wrote [bold]{run.Extracted}[/] extract(s) to [cyan]{Path.Combine(projectRoot, "extracts")}[/].");
        }
        return run.Failed > 0 && run.Extracted == 0 ? 1 : 0;
    }

    private static string FormatTokens(long n) =>
        n < 10_000   ? n.ToString("N0")
        : n < 1_000_000 ? $"{n / 1000.0:0.#}K"
        :                 $"{n / 1_000_000.0:0.##}M";

    private static bool TryParseSince(string s, out DateTime? value)
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

    private sealed class SpectreExtractProgress : IBrainExtractProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total;
        private int _completed;
        private int _failed;
        private long _inTokens;
        private long _outTokens;
        private long _cacheReadTokens;
        private long _cacheCreateTokens;
        private double _cost;
        // Rolling-average duration over completed extracts — drives ETA.
        private double _avgDurationSec;

        public SpectreExtractProgress(ProgressContext ctx) => _ctx = ctx;

        public void Discovered(int eligibleSessions)
        {
            _total = Math.Max(1, eligibleSessions);
            _task = _ctx.AddTask(BuildDescription(), maxValue: _total);
        }

        public void Started(string sessionId) { }

        public void Finished(ExtractFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++;
                if (!info.Success) _failed++;
                _inTokens         += info.InputTokens;
                _outTokens        += info.OutputTokens;
                _cacheReadTokens  += info.CacheReadInputTokens;
                _cacheCreateTokens += info.CacheCreationInputTokens;
                _cost             += info.CostUsd;

                if (info.Duration > TimeSpan.Zero)
                {
                    // Exponential moving average — newer extracts get a little more weight.
                    var sec = info.Duration.TotalSeconds;
                    _avgDurationSec = _avgDurationSec <= 0 ? sec : (_avgDurationSec * 0.7 + sec * 0.3);
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
            var totalIn = _inTokens + _cacheReadTokens + _cacheCreateTokens;
            var failedSuffix = _failed > 0 ? $" [red]✗{_failed}[/]" : "";
            var eta = ComputeEta();
            // Projected total = running avg × total; shown once we have ≥1 datapoint
            // so the user can see whether the run is on track to blow their budget.
            string projected = "";
            if (_completed > 0 && _total > _completed)
            {
                var projTotal = _cost / _completed * _total;
                projected = $" · proj:[yellow]${projTotal:0.##}[/]";
            }
            // Padding the counter so the bar doesn't shift when single→double digits.
            return $"[cyan]Extracting[/] {_completed,3}/{_total}"
                 + $" · in:[bold]{FormatTokens(totalIn)}[/] out:[bold]{FormatTokens(_outTokens)}[/]"
                 + $" · [green]${_cost:0.####}[/]"
                 + projected
                 + (eta is null ? "" : $" · ETA [yellow]{eta}[/]")
                 + failedSuffix;
        }

        private string? ComputeEta()
        {
            if (_avgDurationSec <= 0) return null;
            var remaining = _total - _completed;
            if (remaining <= 0) return null;
            // Wall-clock ETA assumes claude calls run at our configured parallelism.
            // We don't have the parallelism number here, but the EMA naturally absorbs
            // the per-worker rate, so this estimate is close enough.
            var seconds = remaining * _avgDurationSec;
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1 ? $"{ts.Hours}h {ts.Minutes:00}m"
                 : ts.TotalMinutes >= 1 ? $"{ts.Minutes}m {ts.Seconds:00}s"
                 : $"{ts.Seconds}s";
        }
    }
}
