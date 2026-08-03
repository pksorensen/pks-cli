using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainExportSettings : BrainSettings
{
    [CommandOption("-l|--level <LEVEL>")]
    [Description("How much to include: all (full), prompts, or metrics. Default: metrics.")]
    public string Level { get; set; } = AsfLevel.Metrics;

    [CommandOption("-s|--source <KIND>")]
    [Description("Only export one tool: claude, codex or opencode.")]
    public string? Source { get; set; }

    [CommandOption("-p|--project <FILTER>")]
    [Description("Only export sessions whose project slug contains this text.")]
    public string? Project { get; set; }

    [CommandOption("--since <WHEN>")]
    [Description("Only export sessions changed since then. Accepts 7d, 24h, 30m, or an ISO date.")]
    public string? Since { get; set; }

    [CommandOption("--limit <N>")]
    [Description("Cap the number of sessions exported (after filtering).")]
    public int? Limit { get; set; }

    [CommandOption("--force")]
    [Description("Ignore the export cursors and re-emit every event. Event ids are unchanged, so this costs bandwidth, never correctness.")]
    public bool Force { get; set; }

    [CommandOption("--no-blobs")]
    [Description("Skip the raw source backup. Not recommended: the blobs are the only copy of opencode's spilled tool output once its 7-day sweep runs.")]
    public bool NoBlobs { get; set; }

    [CommandOption("--seal-bytes <N>")]
    [Description("Seal a chunk once it reaches this many uncompressed bytes (default 8 MiB).")]
    public int? SealBytes { get; set; }

    [CommandOption("--quiet")]
    [Description("Suppress per-chunk output — just print the summary.")]
    public bool Quiet { get; set; }
}

/// `pks brain export` — turns the three sources into sealed, hash-addressed
/// chunks under ~/.pks-cli/brain/export/, plus a raw blob backup.
///
/// Nothing leaves the machine here; `pks brain push` does that. Splitting the two
/// means the export is worth running on its own — it is already a cross-tool
/// backup — and that a failed upload never costs a re-parse.
public sealed class BrainExportCommand : AsyncCommand<BrainExportSettings>
{
    private readonly IBrainExportService _export;
    private readonly IBrainPathResolver _paths;

    public BrainExportCommand(IBrainExportService export, IBrainPathResolver paths)
    {
        _export = export;
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainExportSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain export[/]").RuleStyle("magenta dim"));
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

        var source = settings.Source?.Trim().ToLowerInvariant();
        if (source is { Length: > 0 } && source is not (AsfSource.Claude or AsfSource.Codex or AsfSource.OpenCode))
        {
            AnsiConsole.MarkupLine($"[red]Unknown --source:[/] {source}. Expected claude, codex or opencode.");

            return 1;
        }

        DateTime? since = null;
        if (settings.Since is { Length: > 0 } sinceStr && !TryParseSince(sinceStr, out since))
        {
            AnsiConsole.MarkupLine($"[red]Could not parse --since value:[/] {sinceStr}");

            return 1;
        }

        var options = new ExportOptions
        {
            Level = level,
            SourceFilter = source,
            ProjectFilter = settings.Project,
            SinceUtc = since,
            Limit = settings.Limit,
            Force = settings.Force,
            IncludeBlobs = !settings.NoBlobs,
            SealBytes = settings.SealBytes ?? AsfChunkWriter.DefaultSealBytes,
        };

        AnsiConsole.MarkupLine($"[grey]Level      [/] [cyan]{level}[/] {LevelBlurb(level)}");
        AnsiConsole.MarkupLine($"[grey]Sources    [/] [cyan]{source ?? "every installed tool"}[/]");
        AnsiConsole.MarkupLine($"[grey]Writing to [/] [cyan]{_paths.ExportRoot}[/]");
        if (options.SinceUtc is { } sUtc)
            AnsiConsole.MarkupLine($"[grey]Since      [/] {sUtc:yyyy-MM-dd HH:mm} UTC");
        if (options.Force)
            AnsiConsole.MarkupLine("[yellow]--force: ignoring export cursors.[/]");
        if (!options.IncludeBlobs)
            AnsiConsole.MarkupLine("[yellow]--no-blobs: opencode spill files will not be rescued this run.[/]");
        AnsiConsole.WriteLine();

        ExportRun run;
        if (settings.Quiet)
        {
            run = await _export.RunAsync(options, NullExportProgress.Instance);
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
                .StartAsync(async ctx => await _export.RunAsync(options, new SpectreExportProgress(ctx)));
        }

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Run id[/]", run.RunId);
        t.AddRow("[grey]Duration[/]", run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]Sessions scanned[/]", run.SessionsScanned.ToString("N0"));
        t.AddRow("[grey]Sessions exported[/]", run.SessionsExported.ToString("N0"));
        t.AddRow("[grey]Skipped (up-to-date)[/]", run.SessionsSkipped.ToString("N0"));
        if (run.SessionsFailed > 0)
            t.AddRow("[grey]Sessions failed[/]", $"[red]{run.SessionsFailed:N0}[/]");
        t.AddRow("[grey]Events written[/]", run.EventsWritten.ToString("N0"));
        t.AddRow("[grey]Events already sent[/]", run.EventsSkipped.ToString("N0"));
        t.AddRow("[grey]Chunks sealed[/]",
            $"{run.ChunksSealed:N0}  [grey]({FormatBytes(run.ChunkBytes)} of {FormatBytes(run.ChunkUncompressedBytes)})[/]");
        t.AddRow("[grey]Blobs added[/]",
            $"{run.BlobsAdded:N0}  [grey]({FormatBytes(run.BlobBytes)}, {run.BlobsAlreadyPresent:N0} already stored)[/]");
        if (run.BlobsDeferred > 0)
            t.AddRow("[grey]Blobs deferred[/]",
                $"{run.BlobsDeferred:N0}  [grey](still being written — archived on the next run)[/]");
        if (run.BlobsSuperseded > 0)
            t.AddRow("[grey]Blobs superseded[/]", run.BlobsSuperseded.ToString("N0"));
        if (run.BlobsPruned > 0)
            t.AddRow("[grey]Blobs pruned[/]",
                $"{run.BlobsPruned:N0}  [grey]({FormatBytes(run.BlobBytesPruned)} reclaimed)[/]");
        if (run.SpillFilesArchived > 0)
            t.AddRow("[grey]opencode spills rescued[/]", $"[green]{run.SpillFilesArchived:N0}[/]");
        AnsiConsole.Write(t);

        if (run.Failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]{run.Failures.Count} problem(s):[/]");
            foreach (var failure in run.Failures.Take(10))
                AnsiConsole.MarkupLine($"  [grey]•[/] {Markup.Escape(failure)}");
            if (run.Failures.Count > 10)
                AnsiConsole.MarkupLine($"  [grey]… and {run.Failures.Count - 10} more[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Next: [bold]pks brain push[/] to send the sealed chunks to your profile.");

        return 0;
    }

    private static string LevelBlurb(string level) => level switch
    {
        AsfLevel.Full => "[grey]— prompts, tool arguments and file paths, secrets masked[/]",
        AsfLevel.Prompts => "[grey]— prompts and which tools ran, but no arguments or paths[/]",
        _ => "[grey]— counts, tokens and timings only, no text[/]",
    };

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

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private sealed class SpectreExportProgress : IExportProgress
    {
        private readonly ProgressContext _ctx;
        private ProgressTask? _task;
        private int _completed;

        public SpectreExportProgress(ProgressContext ctx) => _ctx = ctx;

        public void Discovered(int sessions) =>
            _task = _ctx.AddTask("[cyan]Exporting sessions[/]", maxValue: Math.Max(1, sessions));

        public void Filtered(int eligible, int skipped)
        {
            if (_task is null) return;
            _task.MaxValue = Math.Max(1, eligible);
            _task.Description = $"[cyan]Exporting {eligible} sessions[/] [grey](skipping {skipped} up-to-date)[/]";
        }

        public void Finished(string sessionKey, long eventsWritten, bool failed)
        {
            _completed++;
            if (_task is not null) _task.Value = _completed;
        }

        public void Sealing(ChunkManifest chunk)
        {
            if (_task is not null)
                _task.Description =
                    $"[cyan]Exporting[/] [grey]— sealed {chunk.Src}-{chunk.Level}-{chunk.Ordinal:D2} ({chunk.Events:N0} events)[/]";
        }
    }
}
