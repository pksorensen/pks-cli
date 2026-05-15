using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainRefreshSettings : BrainSettings
{
    [CommandOption("--model")]
    [Description("Model name passed to claude (default: haiku).")]
    public string? Model { get; set; }

    [CommandOption("--parallel")]
    [Description("Max parallel claude invocations (default 10).")]
    public int? Parallel { get; set; }

    [CommandOption("--since")]
    [Description("Only ingest/extract sessions newer than this (7d, 24h, 30m, or ISO date).")]
    public string? Since { get; set; }

    [CommandOption("--max-budget-usd")]
    [Description("Hard dollar cap per invocation (forwarded to each claude call).")]
    public double? MaxBudgetUsd { get; set; }

    [CommandOption("--no-ai")]
    [Description("Skip claude entirely. Ingest still runs; synth/wiki/adr emit skeletons only.")]
    public bool NoAi { get; set; }

    [CommandOption("--dry-run")]
    [Description("Plan every phase and show the combined estimate; no claude calls.")]
    public bool DryRun { get; set; }

    [CommandOption("--force")]
    [Description("Re-run synth/wiki/adr even when no new extracts were produced.")]
    public bool Force { get; set; }

    [CommandOption("--skip-ingest")]
    [Description("Skip the deterministic ingest pass.")]
    public bool SkipIngest { get; set; }

    [CommandOption("--skip-extract")]
    [Description("Skip the per-session AI extract pass.")]
    public bool SkipExtract { get; set; }

    [CommandOption("--skip-synth")]
    [Description("Skip the cross-session synthesis pass.")]
    public bool SkipSynth { get; set; }

    [CommandOption("--skip-wiki")]
    [Description("Skip the wiki render pass.")]
    public bool SkipWiki { get; set; }

    [CommandOption("--skip-adr")]
    [Description("Skip the ADR render pass.")]
    public bool SkipAdr { get; set; }

    [CommandOption("-y|--yes")]
    [Description("Skip the combined cost-confirmation prompt.")]
    public bool Yes { get; set; }
}

/// `pks brain refresh` — one command that brings the whole brain up to date.
/// Plans every phase first so the user sees one combined cost estimate, then
/// runs ingest → extract → synth → wiki → adr in order. Downstream AI phases
/// skip themselves when extract produced nothing new, unless --force is passed.
public class BrainRefreshCommand : AsyncCommand<BrainRefreshSettings>
{
    private readonly IBrainIngestPipeline _ingest;
    private readonly IBrainExtractPipeline _extract;
    private readonly IBrainSynthesisPipeline _synth;
    private readonly IBrainWikiPipeline _wiki;
    private readonly IBrainAdrPipeline _adr;
    private readonly IBrainPathResolver _paths;

    public BrainRefreshCommand(
        IBrainIngestPipeline ingest,
        IBrainExtractPipeline extract,
        IBrainSynthesisPipeline synth,
        IBrainWikiPipeline wiki,
        IBrainAdrPipeline adr,
        IBrainPathResolver paths)
    {
        _ingest = ingest;
        _extract = extract;
        _synth = synth;
        _wiki = wiki;
        _adr = adr;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainRefreshSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain refresh[/]").RuleStyle("magenta dim"));
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
        var slug = _paths.EncodeSlug(_paths.Normalize(cwd) ?? cwd);
        var projectRoot = _paths.ResolveProjectRoot(cwd);
        if (projectRoot is null)
        {
            AnsiConsole.MarkupLine("[red]Not inside a git repository.[/] `pks brain refresh` writes per-project artifacts.");
            return 1;
        }

        var model = settings.Model ?? BrainExtractDefaults.Model;
        var parallel = settings.Parallel ?? BrainExtractDefaults.Parallel;

        AnsiConsole.MarkupLine($"[grey]Project slug:[/] [cyan]{slug}[/]");
        AnsiConsole.MarkupLine($"[grey]Project root:[/] [cyan]{projectRoot}[/]");
        AnsiConsole.MarkupLine($"[grey]Model:[/] [cyan]{model}[/]  [grey]Parallel:[/] [cyan]{parallel}[/]");
        if (settings.NoAi) AnsiConsole.MarkupLine("[yellow]--no-ai[/]: claude will not be invoked in any phase.");
        if (settings.DryRun) AnsiConsole.MarkupLine("[yellow]--dry-run[/]: planning only.");
        if (settings.Force) AnsiConsole.MarkupLine("[yellow]--force[/]: synth/wiki/adr will run even with no new extracts.");
        AnsiConsole.WriteLine();

        var extractOpts = new BrainExtractOptions
        {
            ProjectSlug = slug,
            SinceUtc = since,
            Model = model,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            MaxParallelism = parallel,
        };
        var synthOpts = new BrainSynthOptions
        {
            Model = model,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            MaxParallelism = parallel,
            NoAi = settings.NoAi,
        };
        var wikiOpts = new BrainWikiOptions
        {
            Model = model,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            MaxParallelism = parallel,
            NoAi = settings.NoAi,
        };
        var adrOpts = new BrainAdrOptions
        {
            Model = model,
            MaxBudgetUsd = settings.MaxBudgetUsd,
            MaxParallelism = parallel,
            NoAi = settings.NoAi,
        };

        // ── Phase 1 (run first): Ingest ─────────────────────────────────────────
        // Ingest is deterministic + cheap, but it discovers new session files that
        // make extract eligible. If we plan extract BEFORE ingest, the eligible
        // count is stale-low and the cost gate silently fails to fire. Always
        // ingest first, then plan the AI phases against the post-ingest disk state.
        // (--dry-run still ingests because that's how planning gets accurate counts;
        //  ingest writes only to ~/.pks-cli/brain/, not the user's project tree.)
        Infrastructure.Services.Brain.Models.IngestRun? ingestRun = null;
        if (!settings.SkipIngest)
        {
            AnsiConsole.Write(new Rule("[grey]1. Ingest[/]").RuleStyle("grey dim").LeftJustified());
            var ingestOpts = new IngestOptions
            {
                SinceUtc = since,
                MaxParallelism = parallel,
            };
            ingestRun = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => await _ingest.RunAsync(ingestOpts, new MinimalIngestProgress(ctx)));
            AnsiConsole.MarkupLine($"  [green]✓[/] {ingestRun.FilesIngested:N0} ingested, {ingestRun.FilesSkippedUpToDate:N0} up-to-date, {ingestRun.PromptsAppended:N0} prompts");
            AnsiConsole.WriteLine();
        }

        // ── Build plans for the AI phases (after ingest, so counts are accurate) ─
        BrainExtractPlan? extractPlan = settings.SkipExtract ? null : await _extract.PlanAsync(extractOpts);
        BrainSynthPlan?   synthPlan   = settings.SkipSynth   ? null : await _synth.PlanAsync(synthOpts);
        BrainWikiPlan?    wikiPlan    = settings.SkipWiki    ? null : await _wiki.PlanAsync(wikiOpts);
        BrainAdrPlan?     adrPlan     = settings.SkipAdr     ? null : await _adr.PlanAsync(adrOpts);

        // Downstream skip rule: if extract has nothing new and --force wasn't passed,
        // synth/wiki/adr re-runs are pure waste (same extracts → same outputs).
        var newExtractsExpected = (extractPlan?.Eligible ?? 0) > 0;
        var runDownstream = settings.Force || newExtractsExpected || settings.SkipExtract;

        // ── Combined plan table ─────────────────────────────────────────────────
        var totalEstimate = 0.0;
        var planTable = new Table().Border(TableBorder.MinimalHeavyHead);
        planTable.AddColumn(new TableColumn("[grey]Phase[/]"));
        planTable.AddColumn(new TableColumn("[grey]Plan[/]"));
        planTable.AddColumn(new TableColumn("[grey]Est. cost[/]").RightAligned());

        planTable.AddRow(
            "[bold]1. Ingest[/]",
            settings.SkipIngest
                ? "[grey]skipped[/]"
                : ingestRun is null
                    ? "[cyan]walk Claude session JSONLs[/] [grey](deterministic)[/]"
                    : $"[green]done[/] · {ingestRun.FilesIngested:N0} new, {ingestRun.FilesSkippedUpToDate:N0} up-to-date",
            "[grey]$0[/]");

        if (settings.SkipExtract)
            planTable.AddRow("[bold]2. Extract[/]", "[grey]skipped[/]", "[grey]$0[/]");
        else if (extractPlan!.Eligible == 0)
            planTable.AddRow("[bold]2. Extract[/]", "[grey]nothing eligible (all current)[/]", "[grey]$0[/]");
        else
        {
            totalEstimate += extractPlan.EstimatedCostUsd;
            planTable.AddRow(
                "[bold]2. Extract[/]",
                $"{extractPlan.Eligible:N0} session(s) eligible · [grey]{extractPlan.SkippedByCursor:N0} up-to-date[/]",
                $"[green]${extractPlan.EstimatedCostUsd:0.##}[/]");
        }

        AddDownstreamRow(planTable, "3. Synth", settings.SkipSynth, runDownstream, synthPlan?.TotalAiCalls,
            synthPlan?.ClustersFound, synthPlan?.EstimatedCostUsd, settings.NoAi, ref totalEstimate,
            countLabel: "cluster", aiCallLabel: "AI call");

        AddDownstreamRow(planTable, "4. Wiki", settings.SkipWiki, runDownstream, wikiPlan?.ClustersToRender,
            wikiPlan?.ClustersEligible, wikiPlan?.EstimatedCostUsd, settings.NoAi, ref totalEstimate,
            countLabel: "page", aiCallLabel: "page");

        AddDownstreamRow(planTable, "5. ADR", settings.SkipAdr, runDownstream, adrPlan?.ClustersToRender,
            adrPlan?.ClustersEligible, adrPlan?.EstimatedCostUsd, settings.NoAi, ref totalEstimate,
            countLabel: "ADR", aiCallLabel: "ADR");

        planTable.AddRow("", "[bold]Total[/]", $"[bold green]${totalEstimate:0.##}[/]");
        AnsiConsole.Write(planTable);
        AnsiConsole.WriteLine();

        if (!runDownstream && !settings.SkipExtract && !settings.Force)
            AnsiConsole.MarkupLine("[grey]Synth/wiki/adr will be skipped — no new extracts. Use[/] [bold]--force[/] [grey]to re-run anyway.[/]");
        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine($"[green]Dry-run.[/] Would spend an estimated [green]${totalEstimate:0.##}[/].");
            return 0;
        }

        // ── One combined confirmation gate ──────────────────────────────────────
        if (!settings.Yes && !settings.NoAi && totalEstimate >= 1.0)
        {
            var go = AnsiConsole.Confirm(
                $"Run refresh at estimated [green]${totalEstimate:0.##}[/]?",
                defaultValue: false);
            if (!go)
            {
                AnsiConsole.MarkupLine("[yellow]Aborted.[/] Re-run with [bold]--no-ai[/], [bold]--dry-run[/], or [bold]-y[/].");
                return 0;
            }
            AnsiConsole.WriteLine();
        }

        // ── Phase 2: Extract ────────────────────────────────────────────────────
        BrainExtractRun? extractRun = null;
        if (!settings.SkipExtract && extractPlan!.Eligible > 0)
        {
            AnsiConsole.Write(new Rule("[grey]2. Extract[/]").RuleStyle("grey dim").LeftJustified());
            extractRun = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => await _extract.RunAsync(extractOpts, new MinimalExtractProgress(ctx)));
            AnsiConsole.MarkupLine($"  [green]✓[/] {extractRun.Extracted:N0} extracted, {extractRun.Failed:N0} failed · [green]${extractRun.TotalCostUsd:0.####}[/]");
            AnsiConsole.WriteLine();
            // Re-evaluate downstream: actual run may have produced fewer or zero outputs.
            runDownstream = settings.Force || extractRun.Extracted > 0;
        }

        // ── Phase 3: Synth ──────────────────────────────────────────────────────
        BrainSynthRun? synthRun = null;
        if (!settings.SkipSynth && runDownstream)
        {
            AnsiConsole.Write(new Rule("[grey]3. Synth[/]").RuleStyle("grey dim").LeftJustified());
            synthRun = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => await _synth.RunAsync(synthOpts, new MinimalSynthProgress(ctx)));
            AnsiConsole.MarkupLine($"  [green]✓[/] {synthRun.ClustersFound:N0} clusters, {synthRun.ClustersWritten:N0} written · [green]${synthRun.TotalCostUsd:0.####}[/]");
            AnsiConsole.WriteLine();
        }

        // ── Phase 4: Wiki ───────────────────────────────────────────────────────
        BrainWikiRun? wikiRun = null;
        if (!settings.SkipWiki && runDownstream)
        {
            AnsiConsole.Write(new Rule("[grey]4. Wiki[/]").RuleStyle("grey dim").LeftJustified());
            wikiRun = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => await _wiki.RunAsync(wikiOpts, new MinimalWikiProgress(ctx)));
            AnsiConsole.MarkupLine($"  [green]✓[/] {wikiRun.ClustersRendered:N0} pages, {wikiRun.Failed:N0} failed · [green]${wikiRun.TotalCostUsd:0.####}[/]");
            AnsiConsole.WriteLine();
        }

        // ── Phase 5: ADR ────────────────────────────────────────────────────────
        BrainAdrRun? adrRun = null;
        if (!settings.SkipAdr && runDownstream)
        {
            AnsiConsole.Write(new Rule("[grey]5. ADR[/]").RuleStyle("grey dim").LeftJustified());
            adrRun = await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx => await _adr.RunAsync(adrOpts, new MinimalAdrProgress(ctx)));
            AnsiConsole.MarkupLine($"  [green]✓[/] {adrRun.AdrsWritten:N0} ADRs, {adrRun.Failed:N0} failed · [green]${adrRun.TotalCostUsd:0.####}[/]");
            AnsiConsole.WriteLine();
        }

        // ── Final summary ───────────────────────────────────────────────────────
        AnsiConsole.Write(new Rule("[bold magenta]refresh complete[/]").RuleStyle("magenta dim"));
        var summary = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        summary.AddColumn(""); summary.AddColumn("");
        summary.AddRow("[grey]Ingest[/]",  ingestRun is null  ? "[grey]skipped[/]" : $"{ingestRun.FilesIngested:N0} files");
        summary.AddRow("[grey]Extract[/]", extractRun is null ? (settings.SkipExtract ? "[grey]skipped[/]" : "[grey]nothing eligible[/]") : $"{extractRun.Extracted:N0} extracts · ${extractRun.TotalCostUsd:0.####}");
        summary.AddRow("[grey]Synth[/]",   synthRun is null   ? (settings.SkipSynth   ? "[grey]skipped[/]" : "[grey]skipped (no new extracts)[/]") : $"{synthRun.ClustersFound:N0} clusters · ${synthRun.TotalCostUsd:0.####}");
        summary.AddRow("[grey]Wiki[/]",    wikiRun is null    ? (settings.SkipWiki    ? "[grey]skipped[/]" : "[grey]skipped (no new extracts)[/]") : $"{wikiRun.ClustersRendered:N0} pages · ${wikiRun.TotalCostUsd:0.####}");
        summary.AddRow("[grey]ADR[/]",     adrRun is null     ? (settings.SkipAdr     ? "[grey]skipped[/]" : "[grey]skipped (no new extracts)[/]") : $"{adrRun.AdrsWritten:N0} ADRs · ${adrRun.TotalCostUsd:0.####}");
        var totalSpend = (extractRun?.TotalCostUsd ?? 0) + (synthRun?.TotalCostUsd ?? 0) + (wikiRun?.TotalCostUsd ?? 0) + (adrRun?.TotalCostUsd ?? 0);
        summary.AddRow("[bold]Total spend[/]", $"[bold green]${totalSpend:0.####}[/]");
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();
        return 0;
    }

    private static void AddDownstreamRow(
        Table table, string phaseLabel, bool skip, bool willRun,
        int? aiCalls, int? eligible, double? estCost,
        bool noAi, ref double total,
        string countLabel, string aiCallLabel)
    {
        if (skip)
        {
            table.AddRow($"[bold]{phaseLabel}[/]", "[grey]skipped[/]", "[grey]$0[/]");
            return;
        }
        if (!willRun)
        {
            var note = eligible is null ? "[grey](no plan)[/]" : $"[grey]{eligible:N0} {countLabel}(s) — skipped, no new extracts[/]";
            table.AddRow($"[bold]{phaseLabel}[/]", note, "[grey]$0[/]");
            return;
        }
        if (noAi)
        {
            table.AddRow($"[bold]{phaseLabel}[/]", $"[grey]--no-ai · {eligible ?? 0:N0} {countLabel}(s) deterministic only[/]", "[grey]$0[/]");
            return;
        }
        var cost = estCost ?? 0.0;
        total += cost;
        table.AddRow(
            $"[bold]{phaseLabel}[/]",
            $"{aiCalls ?? 0:N0} {aiCallLabel}(s) [grey]· {eligible ?? 0:N0} {countLabel}(s) detected[/]",
            cost > 0 ? $"[green]${cost:0.##}[/]" : "[grey]$0[/]");
    }

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

    // ── Minimal progress adapters (one progress bar per phase, no projections) ──
    // The individual phase commands have richer Spectre progress widgets; refresh
    // keeps it terse so the eye can follow the phase sequence.

    private sealed class MinimalIngestProgress : IIngestProgress
    {
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _completed;
        public MinimalIngestProgress(ProgressContext ctx) => _ctx = ctx;
        public void Discovered(int totalFiles) => _task = _ctx.AddTask("[cyan]Ingesting[/]", maxValue: Math.Max(1, totalFiles));
        public void Filtered(int eligibleFiles, int skippedByCursor)
        {
            if (_task is not null) { _task.MaxValue = Math.Max(1, eligibleFiles); _task.Description = $"[cyan]Ingesting {eligibleFiles}[/] [grey](skip {skippedByCursor})[/]"; }
        }
        public void Started(string file) { }
        public void Finished(string file, bool ingested, bool error)
        {
            Interlocked.Increment(ref _completed);
            if (_task is not null) _task.Value = _completed;
        }
    }

    private sealed class MinimalExtractProgress : IBrainExtractProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total, _completed, _failed;
        private double _cost;
        public MinimalExtractProgress(ProgressContext ctx) => _ctx = ctx;
        public void Discovered(int eligibleSessions) { _total = Math.Max(1, eligibleSessions); _task = _ctx.AddTask(Build(), maxValue: _total); }
        public void Started(string sessionId) { }
        public void Finished(ExtractFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++; if (!info.Success) _failed++; _cost += info.CostUsd;
                if (_task is not null) { _task.Value = _completed; _task.Description = Build(); }
            }
        }
        private string Build() => $"[cyan]Extracting[/] {_completed}/{_total} · [green]${_cost:0.####}[/]" + (_failed > 0 ? $" [red]✗{_failed}[/]" : "");
    }

    private sealed class MinimalSynthProgress : IBrainSynthProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total, _completed, _failed;
        private double _cost;
        public MinimalSynthProgress(ProgressContext ctx) => _ctx = ctx;
        public void Discovered(int totalCalls) { _total = Math.Max(1, totalCalls); _task = _ctx.AddTask(Build(), maxValue: _total); }
        public void Started(string stage) { }
        public void Finished(SynthFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++; if (!info.Success) _failed++; _cost += info.CostUsd;
                if (_task is not null) { _task.Value = _completed; _task.Description = Build(); }
            }
        }
        private string Build() => $"[cyan]Synthesizing[/] {_completed}/{_total} · [green]${_cost:0.####}[/]" + (_failed > 0 ? $" [red]✗{_failed}[/]" : "");
    }

    private sealed class MinimalWikiProgress : IBrainWikiProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total, _completed, _failed;
        private double _cost;
        public MinimalWikiProgress(ProgressContext ctx) => _ctx = ctx;
        public void Discovered(int totalPages) { _total = Math.Max(1, totalPages); _task = _ctx.AddTask(Build(), maxValue: _total); }
        public void Started(string tag) { }
        public void Finished(WikiFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++; if (!info.Success) _failed++; _cost += info.CostUsd;
                if (_task is not null) { _task.Value = _completed; _task.Description = Build(); }
            }
        }
        private string Build() => $"[cyan]Wiki pages[/] {_completed}/{_total} · [green]${_cost:0.####}[/]" + (_failed > 0 ? $" [red]✗{_failed}[/]" : "");
    }

    private sealed class MinimalAdrProgress : IBrainAdrProgress
    {
        private readonly object _gate = new();
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _total, _completed, _failed;
        private double _cost;
        public MinimalAdrProgress(ProgressContext ctx) => _ctx = ctx;
        public void Discovered(int totalCalls) { _total = Math.Max(1, totalCalls); _task = _ctx.AddTask(Build(), maxValue: _total); }
        public void Started(string tag) { }
        public void Finished(AdrFinishedInfo info)
        {
            lock (_gate)
            {
                _completed++; if (!info.Success) _failed++; _cost += info.CostUsd;
                if (_task is not null) { _task.Value = _completed; _task.Description = Build(); }
            }
        }
        private string Build() => $"[cyan]ADRs[/] {_completed}/{_total} · [green]${_cost:0.####}[/]" + (_failed > 0 ? $" [red]✗{_failed}[/]" : "");
    }
}
