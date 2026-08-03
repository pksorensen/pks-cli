using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainPushSettings : BrainSettings
{
    [CommandOption("-e|--endpoint <URL>")]
    [Description("Where to push. Defaults to the last endpoint used, then https://agentics.dk.")]
    public string? Endpoint { get; set; }

    [CommandOption("-t|--token <TOKEN>")]
    [Description("Upload token. Otherwise PKS_BRAIN_TOKEN, then the credentials from `pks agentics init`.")]
    public string? Token { get; set; }

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
    [Description("Suppress per-chunk output — just print the summary.")]
    public bool Quiet { get; set; }
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
    IBrainExportService export) : AsyncCommand<BrainPushSettings>
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
        var endpoint = BrainPushService.NormalizeEndpoint(
            settings.Endpoint ?? manifest.Endpoint ?? PushOptions.DefaultEndpoint);

        if (manifest.Chunks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing has been exported yet.[/]");
            AnsiConsole.MarkupLine("Run [bold]pks brain export --level all[/] first.");

            return 0;
        }

        var token = await tokens.ResolveAsync(settings.Token, endpoint);
        if (token is null && !settings.DryRun)
        {
            AnsiConsole.MarkupLine("[red]No credential found.[/]");
            AnsiConsole.MarkupLine(
                $"  Sign in with [bold]pks agentics init[/], or mint an upload token on {endpoint}/u/<handle>/settings/brain");
            AnsiConsole.MarkupLine("  and pass it as [bold]--token[/] or in [bold]PKS_BRAIN_TOKEN[/].");

            return 1;
        }
        if (token?.Warning is { Length: > 0 } warning)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");

        var options = new PushOptions
        {
            Endpoint = endpoint,
            Token = token?.Value ?? "",
            LevelFilter = level,
            SourceFilter = settings.Source?.Trim().ToLowerInvariant(),
            DryRun = settings.DryRun,
            IncludeBlobs = !settings.NoBlobs,
            Force = settings.Force,
        };

        AnsiConsole.MarkupLine($"[grey]Endpoint  [/] [cyan]{Markup.Escape(endpoint)}[/]");
        AnsiConsole.MarkupLine($"[grey]Credential[/] {(token is null ? "[grey]none (dry run)[/]" : Markup.Escape(token.Origin))}");
        AnsiConsole.MarkupLine($"[grey]Level     [/] [cyan]{level ?? "every level on disk"}[/]");
        AnsiConsole.WriteLine();

        PushRun run;
        try
        {
            run = await push.RunAsync(options, settings.Quiet ? NullPushProgress.Instance : new SpectrePushProgress());
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
                $"[bold]Dry run:[/] would send [cyan]{run.ChunksConsidered:N0}[/] chunk(s) and [cyan]{run.BlobsConsidered:N0}[/] blob(s) to {Markup.Escape(endpoint)}.");
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
        t.AddRow("[grey]Events accepted[/]", run.Accepted.ToString("N0"));
        if (run.Enriched > 0)
            t.AddRow("[grey]Events enriched[/]", $"{run.Enriched:N0}  [grey](same ids, higher level)[/]");
        t.AddRow("[grey]Events duplicate[/]", run.Duplicate.ToString("N0"));
        if (run.Rejected > 0)
            t.AddRow("[grey]Events rejected[/]", $"[yellow]{run.Rejected:N0}[/]");
        if (run.Days.Count > 0)
            t.AddRow("[grey]Days touched[/]", $"{run.Days.Count:N0}  [grey]({run.Days.First()} … {run.Days.Last()})[/]");
        if (run.StorageBytes > 0)
            t.AddRow("[grey]Stored on server[/]", FormatBytes(run.StorageBytes));
        AnsiConsole.Write(t);

        if (run.Masked > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]The server masked {run.Masked:N0} secret(s) this client missed.[/] The raw values were on the wire — upgrade pks-cli.");
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
        AnsiConsole.MarkupLine($"Your data: [bold]{Markup.Escape(endpoint)}/u/[/][grey]<handle>[/][bold]/brain[/]");
        AnsiConsole.MarkupLine("Make it a habit: [bold]pks brain daemon install[/] runs export + push once a day.");

        return run.Failures.Count > 0 ? 1 : 0;
    }

    private static string Advice(BrainPushException ex) => ex.Code switch
    {
        "unauthorized" => "Sign in again with [bold]pks agentics init[/], or mint a fresh upload token.",
        "level_exceeded" => "Your token's maximum level is lower than the chunks you are pushing. Mint a token with a higher level, or push with [bold]--level[/].",
        "quota_exceeded" => "Delete old days on the settings page, or ask for a larger quota.",
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

internal sealed class SpectrePushProgress : IPushProgress
{
    public void Planned(int chunks, int blobs, long bytes) =>
        AnsiConsole.MarkupLine($"[grey]Offering[/] {chunks:N0} chunk(s), {blobs:N0} blob(s)");

    public void SyncOpened(string syncId, int missingChunks, int missingBlobs) =>
        AnsiConsole.MarkupLine(
            $"[grey]sync[/] {Markup.Escape(syncId)} [grey]→ needs[/] {missingChunks:N0} chunk(s), {missingBlobs:N0} blob(s)");

    public void Uploading(string kind, string hash, long bytes) { }

    public void Uploaded(string kind, string hash, bool duplicate) =>
        AnsiConsole.MarkupLine(
            $"  [grey]{kind}[/] {hash[..12]} {(duplicate ? "[grey]already there[/]" : "[green]sent[/]")}");

    public void Committed(CommitResult result) =>
        AnsiConsole.MarkupLine(
            $"[grey]commit[/] [green]{result.Accepted:N0} accepted[/], {result.Duplicate:N0} duplicate, {result.Enriched:N0} enriched");
}
