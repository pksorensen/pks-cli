using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using PKS.Infrastructure.Services.Discovery;

namespace PKS.Commands.Brain;

public sealed class BrainPushSettings : BrainSettings
{
    // `--server` is the name the rest of the CLI uses for "which host"; the
    // original `--endpoint` stays as an alias so existing scripts keep working.
    [CommandOption("-e|--endpoint|--server <URL>")]
    [Description("Which receiver to push to. Defaults to the last one used, then https://agentics.dk. "
               + "Any host that implements the ASF receiver API works — see docs/specs/asf/06-receiver-auth.md.")]
    public string? Endpoint { get; set; }

    [CommandOption("-t|--token <TOKEN>")]
    [Description("Upload token. Otherwise PKS_BRAIN_TOKEN, then the credentials from `pks agentics init`.")]
    public string? Token { get; set; }

    [CommandOption("--no-login")]
    [Description("Never open an interactive login. For cron and CI, where a device-code prompt would hang.")]
    public bool NoLogin { get; set; }

    [CommandOption("--client-id <ID>")]
    [Description("OAuth client_id to log in with. Only needed for providers that reject our "
               + "client-id metadata document (CIMD) URL and hand out IDs instead.")]
    public string? ClientId { get; set; }

    [CommandOption("-l|--level <LEVEL>")]
    [Description("Only push chunks sealed at this level: all, prompts or metrics.")]
    public string? Level { get; set; }

    [CommandOption("-s|--source <KIND>")]
    [Description("Only push chunks from one tool: claude, codex or opencode.")]
    public string? Source { get; set; }

    [CommandOption("--dry-run")]
    [Description("Show what would be sent without opening a sync session.")]
    public bool DryRun { get; set; }

    [CommandOption("--no-blobs")]
    [Description("Skip the raw source backup even at --level all.")]
    public bool NoBlobs { get; set; }

    [CommandOption("--force")]
    [Description("Re-send chunks already marked uploaded. Deduped on arrival, so this costs bandwidth only.")]
    public bool Force { get; set; }

    [CommandOption("--quiet")]
    [Description("No progress at all — just print the summary.")]
    public bool Quiet { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Print a line per chunk and blob instead of a progress bar. For diagnosing a "
               + "push that stalls on one artifact.")]
    public bool Verbose { get; set; }

    [CommandOption("--wait [SECONDS]")]
    [Description("Wait for the server to finish folding what was sent, so the summary reports "
               + "what the profile actually shows. Defaults to 120 seconds.")]
    public FlagValue<int>? Wait { get; set; }
}

/// `pks brain push` — uploads what `brain export` sealed.
///
/// Everything it sends already exists on disk in its final, masked, hashed form;
/// this command adds no data and makes no decisions about content. That split is
/// what makes the export worth running without an account, and makes a failed
/// push a bandwidth problem rather than a data problem.
public sealed class BrainPushCommand(
    IBrainPushService push,
    IBrainTokenResolver tokens,
    IBrainExportService export,
    IAgenticPlatformDiscovery discovery) : AsyncCommand<BrainPushSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BrainPushSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain push[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        string? level = null;
        if (settings.Level is { Length: > 0 })
        {
            try
            {
                level = AsfLevel.Parse(settings.Level);
            }
            catch (ArgumentException)
            {
                AnsiConsole.MarkupLine($"[red]Unknown --level:[/] {Markup.Escape(settings.Level)}. Expected all, prompts or metrics.");

                return 1;
            }
        }

        var manifest = await export.LoadManifestAsync();
        var server = settings.Endpoint ?? manifest.Endpoint ?? PushOptions.DefaultEndpoint;

        if (manifest.Chunks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing has been exported yet.[/]");
            AnsiConsole.MarkupLine("Run [bold]pks brain export --level all[/] first.");

            return 0;
        }

        var (endpoint, caps) = await BrainConnection.ResolveAsync(discovery, server);

        // Blobs are the raw source archive. A receiver may take events and
        // refuse the archive, and pushing them anyway would waste the bandwidth
        // to earn a 4xx per blob.
        var includeBlobs = !settings.NoBlobs && caps.BlobsAccepted;

        var request = new BrainTokenRequest(
            settings.Token,
            endpoint,
            AllowInteractiveLogin: !settings.NoLogin && !settings.DryRun && AnsiConsole.Profile.Capabilities.Interactive,
            settings.ClientId,
            BrainConnection.ShowDeviceCode,
            BrainConnection.ShowAuthorizeUrl);

        var token = await tokens.ResolveAsync(request);
        if (token is null && !settings.DryRun)
        {
            BrainConnection.PrintNoCredential(endpoint, request.AllowInteractiveLogin);

            return 1;
        }
        if (token?.Warning is { Length: > 0 } warning)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");

        // A push of 13k blobs outlives a 5-minute access token several times
        // over, so renewal has to be a loop, not a one-off — but a loop that a
        // permanently-rejecting server can spin. One renewal per 30 seconds is
        // far more than a real expiry needs and far less than a refusal can
        // exploit; past that the push fails with "could not be renewed", which
        // is the honest description of a refresh that keeps producing tokens the
        // server rejects.
        var lastRenewal = DateTimeOffset.MinValue;
        var renewalDebounce = TimeSpan.FromSeconds(30);

        var options = new PushOptions
        {
            Endpoint = endpoint.Origin,
            ApiBase = endpoint.ApiBase,
            Token = token?.Value ?? "",
            LevelFilter = level,
            SourceFilter = settings.Source?.Trim().ToLowerInvariant(),
            DryRun = settings.DryRun,
            IncludeBlobs = includeBlobs,
            Force = settings.Force,
            // The receiver's own ceilings, not ours. Batching to a limit the
            // other side does not have is how a client "works on agentics.dk
            // and nowhere else".
            // Commit only stores; the receiver folds afterwards. Waiting is opt-in
            // because the data is safe either way — but without it a first push
            // ends on an account that still reads as empty, which looks like a
            // failure and is not.
            WaitForProjection = WaitFor(settings),
            BatchSize = caps.BatchSize(PushOptions.DefaultBatchSize),
            BlobBatchSize = caps.BlobBatchSize(PushOptions.DefaultBlobBatchSize),
            MaxChunkBytes = caps.MaxChunkBytes,
            MaxBlobBytes = caps.MaxBlobBytes,
            // The resolver refreshes and saves, so re-asking it is the whole
            // renewal — but only with `ForceRefresh`. Without it the resolver
            // answers from its own expiry arithmetic and hands back the token
            // the server just rejected, which is exactly how a long push died at
            // 12,000 of 13,249 blobs. A 401 is the server's vote, and it wins.
            //
            // For a `bkt_` token there is nothing to refresh, so the same value
            // comes back and the 401 stands, which is correct: that token really
            // was revoked. No interactive prompt here — a device code halfway
            // through an upload would be a surprise, not a feature.
            Reauthenticate = async ct =>
            {
                if (DateTimeOffset.UtcNow - lastRenewal < renewalDebounce) return null;
                lastRenewal = DateTimeOffset.UtcNow;

                return (await tokens.ResolveAsync(
                    request with { AllowInteractiveLogin = false, ForceRefresh = true }, ct))?.Value;
            },
        };

        BrainConnection.PrintTarget(endpoint, caps);
        AnsiConsole.MarkupLine($"[grey]Credential[/] {(token is null ? "[grey]none (dry run)[/]" : Markup.Escape(token.Origin))}");
        AnsiConsole.MarkupLine($"[grey]Level     [/] [cyan]{level ?? "every level on disk"}[/]");
        if (!settings.NoBlobs && !caps.BlobsAccepted)
            AnsiConsole.MarkupLine("[grey]Blobs     [/] [yellow]this receiver does not accept them — skipping[/]");
        AnsiConsole.WriteLine();

        PushRun run;
        try
        {
            run = await RunWithProgressAsync(push, options, settings);
        }
        catch (BrainPushException ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Code)}[/] — {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"  {Advice(ex)}");

            return 1;
        }

        AnsiConsole.WriteLine();
        if (run.EndpointChanged)
        {
            AnsiConsole.MarkupLine(
                "[yellow]The manifest pointed at a different endpoint.[/] This one has none of the data, so everything on disk was offered again.");
            AnsiConsole.WriteLine();
        }

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Dry run:[/] would send [cyan]{run.ChunksConsidered:N0}[/] chunk(s) and [cyan]{run.BlobsConsidered:N0}[/] blob(s) to {Markup.Escape(endpoint.ApiBase)}.");
            if (run.ChunksMissingLocally > 0)
                AnsiConsole.MarkupLine($"[yellow]{run.ChunksMissingLocally:N0} chunk file(s) referenced by the manifest are gone from disk.[/]");

            return 0;
        }

        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Duration[/]", run.Duration.ToString(@"hh\:mm\:ss\.fff"));
        t.AddRow("[grey]Sync sessions[/]", run.Syncs.ToString("N0"));
        t.AddRow("[grey]Chunks uploaded[/]", $"{run.ChunksUploaded:N0}  [grey]({FormatBytes(run.BytesUploaded)}, {run.ChunksKnown:N0} already there)[/]");
        if (run.BlobsConsidered > 0)
            t.AddRow("[grey]Blobs uploaded[/]", $"{run.BlobsUploaded:N0}  [grey]({FormatBytes(run.BlobBytesUploaded)}, {run.BlobsKnown:N0} already there)[/]");
        t.AddRow("[grey]Chunks queued[/]", $"{run.Queued:N0}  [grey]({run.QueuedDuplicate:N0} the server already had)[/]");
        t.AddRow("[grey]Events queued[/]", $"{run.QueuedEvents:N0}  [grey](what those chunks hold)[/]");
        if (run.Days.Count > 0)
            t.AddRow("[grey]Days touched[/]", $"{run.Days.Count:N0}  [grey]({run.Days.First()} … {run.Days.Last()})[/]");
        if (run.Projection is { } projection)
        {
            t.AddRow("[grey]Projected[/]", projection.Done
                ? $"[green]{projection.Events:N0} event(s) — up to date[/]"
                : $"[yellow]{projection.Projected:N0} of {projection.LogLines:N0} chunk(s) — still folding[/]");
        }
        else if (run.Pending > 0)
        {
            t.AddRow("[grey]Waiting to fold[/]", $"{run.Pending:N0} chunk(s)  [grey](the server does this in the background)[/]");
        }
        if (run.StorageBytes > 0)
            t.AddRow("[grey]Stored on server[/]", FormatBytes(run.StorageBytes));
        AnsiConsole.Write(t);

        if (run.Projection is { ServerMasked: > 0 } masked)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]The server has masked {masked.ServerMasked:N0} secret(s) this client missed.[/] The raw values were on the wire — upgrade pks-cli.");
        }

        if (run.Projection is null && run.Pending > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[grey]Your data is stored. The graphs fill in as the server folds it — re-run with [bold]--wait[/] to watch.[/]");
        }

        if (run.ChunksMissingLocally > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{run.ChunksMissingLocally:N0} chunk file(s) in the manifest are gone from disk.[/] Re-run [bold]pks brain export --force[/] to rebuild them.");
        }

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
        AnsiConsole.MarkupLine($"Your data: [bold]{Markup.Escape(endpoint.Origin)}/u/[/][grey]<handle>[/][bold]/brain[/]");
        AnsiConsole.MarkupLine("Make it a habit: [bold]pks brain daemon install[/] runs export + push once a day.");

        return run.Failures.Count > 0 ? 1 : 0;
    }

    /// Three renderings, because a push is watched in three different ways.
    ///
    /// A terminal gets a live bar: 457 chunks and 13,000 blobs is thousands of
    /// lines of scrollback that say nothing a bar does not say better. A pipe or
    /// a cron log gets one line per batch, since a bar redrawn into a file is
    /// noise of a different kind. `--verbose` gets the per-artifact trace, which
    /// is the only view that helps when one chunk is the problem.
    private static async Task<PushRun> RunWithProgressAsync(
        IBrainPushService push,
        PushOptions options,
        BrainPushSettings settings)
    {
        if (settings.Quiet) return await push.RunAsync(options, NullPushProgress.Instance);

        // A dry run never uploads, so a bar would sit at zero and read as a stall.
        if (settings.Verbose || settings.DryRun || !AnsiConsole.Profile.Capabilities.Interactive)
            return await push.RunAsync(options, new LinePushProgress(settings.Verbose));

        return await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new CountColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx => await push.RunAsync(options, new BarPushProgress(ctx)));
    }

    /// `--wait` with no number means the default; no `--wait` at all means don't.
    private static TimeSpan WaitFor(BrainPushSettings settings)
    {
        if (settings.DryRun || settings.Wait is not { IsSet: true } wait) return TimeSpan.Zero;

        return TimeSpan.FromSeconds(wait.Value > 0 ? wait.Value : 120);
    }

    private static string Advice(BrainPushException ex) => ex.Code switch
    {
        "unauthorized" => "Sign in again with [bold]pks agentics init[/], or mint a fresh upload token.",
        "level_exceeded" => "Your token's maximum level is lower than the chunks you are pushing. Mint a token with a higher level, or push with [bold]--level[/].",
        "quota_exceeded" => ex.QuotaBytes is > 0
            ? $"Using {FormatBytes(ex.QuotaUsedBytes ?? 0)} of {FormatBytes(ex.QuotaBytes.Value)}. Delete old days on the settings page, or ask for a larger quota."
            : "Delete old days on the settings page, or ask for a larger quota.",
        "rate_limited" => "Too many sync sessions this hour. Try again later — nothing was lost.",
        "unreachable" => "Check the endpoint and your network.",
        _ => "Nothing was written on the server; re-running is safe.",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
    };
}

/// `120/457` beside the bar. The percentage alone hides the scale, and the scale
/// is what tells you whether to wait or go and make coffee.
internal sealed class CountColumn : ProgressColumn
{
    protected override bool NoWrap => true;

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime) =>
        new Markup($"[grey]{task.Value:N0}/{task.MaxValue:N0}[/]");
}

/// A live bar per artifact kind, plus one for the receiver's fold when `--wait`
/// is on. Every value it sets is absolute, so a retried batch redraws the same
/// number rather than advancing past the total.
internal sealed class BarPushProgress(ProgressContext ctx) : IPushProgress
{
    private readonly object _gate = new();
    private ProgressTask? _chunks;
    private ProgressTask? _blobs;
    private ProgressTask? _fold;

    public void Planned(int chunks, int blobs, long bytes)
    {
        lock (_gate)
        {
            if (chunks > 0) _chunks = ctx.AddTask("[grey]Chunks[/]", maxValue: chunks);
            if (blobs > 0) _blobs = ctx.AddTask("[grey]Blobs[/]", maxValue: blobs);
        }
    }

    public void SyncOpened(string syncId, int missingChunks, int missingBlobs) { }
    public void Uploading(string kind, string hash, long bytes) { }
    public void Uploaded(string kind, string hash, bool duplicate) { }

    public void Advanced(PushProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_chunks is { } c) c.Value = Math.Min(snapshot.ChunksSettled, c.MaxValue);
            if (_blobs is { } b) b.Value = Math.Min(snapshot.BlobsSettled, b.MaxValue);
        }
    }

    public void Committed(CommitResult result) { }

    public void Projecting(ProjectionStatus status)
    {
        lock (_gate)
        {
            // Only once we are actually waiting for it — a bar that appears and
            // never moves is worse than no bar.
            _fold ??= ctx.AddTask("[grey]Folding[/]", maxValue: Math.Max(status.LogLines, 1));
            _fold.MaxValue = Math.Max(status.LogLines, 1);
            _fold.Value = status.Done ? _fold.MaxValue : status.Projected;
        }
    }
}

/// The pipe-friendly rendering: one line per phase, and per artifact only when
/// the user asked for it.
internal sealed class LinePushProgress(bool verbose) : IPushProgress
{
    public void Planned(int chunks, int blobs, long bytes) =>
        AnsiConsole.MarkupLine($"[grey]Offering[/] {chunks:N0} chunk(s), {blobs:N0} blob(s)");

    public void SyncOpened(string syncId, int missingChunks, int missingBlobs) =>
        AnsiConsole.MarkupLine(
            $"[grey]sync[/] {Markup.Escape(syncId)} [grey]→ needs[/] {missingChunks:N0} chunk(s), {missingBlobs:N0} blob(s)");

    public void Uploading(string kind, string hash, long bytes) { }

    public void Uploaded(string kind, string hash, bool duplicate)
    {
        if (!verbose) return;

        AnsiConsole.MarkupLine(
            $"  [grey]{kind}[/] {hash[..12]} {(duplicate ? "[grey]already there[/]" : "[green]sent[/]")}");
    }

    public void Advanced(PushProgressSnapshot snapshot) { }

    public void Committed(CommitResult result) =>
        AnsiConsole.MarkupLine(
            $"[grey]commit[/] [green]{result.Queued:N0} chunk(s) queued[/], {result.Duplicate:N0} already there"
          + $" [grey]({result.QueuedEvents:N0} event(s))[/]");

    public void Projecting(ProjectionStatus status) =>
        AnsiConsole.MarkupLine(status.Done
            ? $"[grey]projecting[/] [green]done[/] [grey]— {status.Events:N0} event(s) stored[/]"
            : $"[grey]projecting[/] {status.Projected:N0}/{status.LogLines:N0} chunk(s), {status.Dirty:N0} day(s) to fold");
}
