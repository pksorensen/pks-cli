using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// The export half of ASF: rolling chunks, content-addressed blobs, and the
/// cursors that make a daily run cheap.
///
/// Two properties carry the whole design and are asserted here directly:
///
/// 1. **Determinism.** Two exports of the same source bytes produce the same
///    chunk hash. Without it, every re-export is a full re-upload and the
///    server's "do I already have this?" check is worthless.
/// 2. **Level-independent ids.** The same event exported as `metrics` and later
///    as `full` carries the same id, so raising a level enriches the stored rows
///    instead of duplicating them — and the totals do not move.
///
/// Both are invisible in normal use and only surface as a slowly drifting graph
/// on someone's profile, which is exactly why they are pinned.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class BrainExportServiceTests : TestBase
{
    private static readonly SecretMasker Masker = new();

    private const string ProjectSlug = "-workspaces-test-fixture";
    private const string SessionId = "export-fixture";
    private const string PlantedSecret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    /// A small but complete Claude session: prompt → tool call → result → prompt.
    /// The Bash command carries a credential, so every assertion about what ends
    /// up in a chunk is also an assertion that masking ran first.
    private static readonly string[] Lines =
    {
        """{"type":"user","uuid":"U1","timestamp":"2026-01-01T10:00:00Z","sessionId":"export-fixture","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"first prompt"}]}}""",
        // Built by concatenation: a raw interpolated literal cannot hold this many
        // consecutive closing braces.
        """{"type":"assistant","uuid":"U2","timestamp":"2026-01-01T10:00:02Z","sessionId":"export-fixture","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"text","text":"working"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"export GITHUB_TOKEN=""" +
        PlantedSecret +
        """ && ls"}}],"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":20}}}""",
        """{"type":"user","uuid":"U3","timestamp":"2026-01-01T10:00:05Z","sessionId":"export-fixture","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"listing","is_error":false}]}}""",
        """{"type":"user","uuid":"U4","timestamp":"2026-01-01T10:00:07Z","sessionId":"export-fixture","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"second prompt"}]}}""",
    };

    /// Two extra lines used by the incremental-export test.
    private static readonly string[] MoreLines =
    {
        """{"type":"assistant","uuid":"U5","timestamp":"2026-01-01T10:00:09Z","sessionId":"export-fixture","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"text","text":"and again"}]}}""",
        """{"type":"user","uuid":"U6","timestamp":"2026-01-01T10:00:11Z","sessionId":"export-fixture","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"third prompt"}]}}""",
    };

    // ── fixture plumbing ──────────────────────────────────────────────────────

    /// A fake $HOME holding one Claude transcript. The export root lives under the
    /// same home, so a test never touches the machine's real brain.
    private string NewHome()
    {
        var home = CreateTempDirectory();
        WriteTranscript(home, Lines);

        return home;
    }

    private static string TranscriptPath(string home) =>
        Path.Combine(home, ".claude", "projects", ProjectSlug, SessionId + ".jsonl");

    private static void WriteTranscript(string home, IEnumerable<string> lines)
    {
        var path = TranscriptPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        // Pinned so `mtime` cannot make two otherwise-identical fixtures differ.
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 10, 0, 12, DateTimeKind.Utc));
    }

    /// Only the Claude source is registered: this suite is about the export
    /// machinery, and the per-source parsers have their own golden tests.
    private static BrainExportService ServiceFor(IBrainPathResolver paths) =>
        new(new IAgentSessionSource[] { new ClaudeAsfSource(paths) }, Masker, paths);

    private static async Task<(ExportRun Run, IBrainPathResolver Paths)> ExportAsync(
        string home,
        string level = AsfLevel.Metrics,
        bool force = false,
        bool blobs = true,
        TimeSpan? quietPeriod = null,
        TimeSpan? supersededGrace = null)
    {
        var paths = new BrainPathResolver(home);
        var run = await ServiceFor(paths).RunAsync(
            new ExportOptions
            {
                Level = level,
                Force = force,
                IncludeBlobs = blobs,

                // The fixtures are stamped in the past, so the default 6-hour quiet
                // period is already satisfied; tests that care set it explicitly.
                BlobQuietPeriod = quietPeriod ?? TimeSpan.FromHours(6),
                BlobSupersededGrace = supersededGrace ?? TimeSpan.FromDays(7),
            },
            NullExportProgress.Instance);

        return (run, paths);
    }

    private static List<JsonObject> EventsIn(IBrainPathResolver paths) =>
        Directory.EnumerateFiles(paths.ExportRoot, "*.ndjson.zst", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(AsfChunkWriter.ReadLines)
            .Select(line => (JsonObject)JsonNode.Parse(line)!)
            .ToList();

    private static string[] IdsIn(IBrainPathResolver paths) =>
        EventsIn(paths).Select(e => e["id"]!.GetValue<string>()).ToArray();

    private static string RawTextOf(IBrainPathResolver paths) =>
        string.Join('\n',
            Directory.EnumerateFiles(paths.ExportRoot, "*.ndjson.zst", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .SelectMany(AsfChunkWriter.ReadLines));

    // ── the two load-bearing properties ───────────────────────────────────────

    [Fact]
    public async Task Two_runs_over_the_same_sources_produce_identical_chunk_hashes()
    {
        var (first, firstPaths) = await ExportAsync(NewHome());
        var (second, secondPaths) = await ExportAsync(NewHome());

        first.ChunksSealed.Should().Be(1);
        second.ChunksSealed.Should().Be(1);

        var a = (await ServiceFor(firstPaths).LoadManifestAsync()).Chunks.Select(c => c.ChunkHash);
        var b = (await ServiceFor(secondPaths).LoadManifestAsync()).Chunks.Select(c => c.ChunkHash);

        b.Should().Equal(a, "identical source bytes must yield identical chunks, or every " +
                            "re-export becomes a full re-upload");

        // And the hash must describe the file that was actually written.
        var chunkFile = Directory.EnumerateFiles(firstPaths.ExportRoot, "*.ndjson.zst", SearchOption.AllDirectories).Single();
        AsfChunkWriter.HashOf(chunkFile).Should().Be(a.Single());
    }

    [Fact]
    public async Task Same_events_exported_at_metrics_and_full_keep_their_ids()
    {
        var (_, metricsPaths) = await ExportAsync(NewHome(), AsfLevel.Metrics);
        var (_, fullPaths) = await ExportAsync(NewHome(), AsfLevel.Full);

        IdsIn(fullPaths).Should().Equal(IdsIn(metricsPaths),
            "ids hash the full event before redaction, so a level change enriches " +
            "rows on the receiver instead of duplicating them");

        // Same identity, genuinely different payloads — otherwise the levels do nothing.
        EventsIn(metricsPaths).Should().OnlyContain(e => e["level"]!.GetValue<string>() == AsfLevel.Metrics);
        EventsIn(fullPaths).Should().OnlyContain(e => e["level"]!.GetValue<string>() == AsfLevel.Full);
        RawTextOf(fullPaths).Should().Contain("first prompt");
        RawTextOf(metricsPaths).Should().NotContain("first prompt");
    }

    [Fact]
    public async Task Masking_runs_before_anything_is_written()
    {
        var (_, paths) = await ExportAsync(NewHome(), AsfLevel.Full);

        var text = RawTextOf(paths);
        text.Should().Contain("ls", "the command itself is kept at level=full");
        text.Should().NotContain(PlantedSecret);
        text.Should().NotContain("ghp_");
    }

    // ── cursors ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unchanged_session_is_skipped_on_the_next_run()
    {
        var home = NewHome();
        await ExportAsync(home);
        var (second, paths) = await ExportAsync(home);

        second.SessionsSkipped.Should().Be(1);
        second.SessionsExported.Should().Be(0);
        second.EventsWritten.Should().Be(0);
        second.ChunksSealed.Should().Be(0);

        (await ServiceFor(paths).LoadManifestAsync()).Chunks.Should().ContainSingle(
            "a skipped run must not seal a second copy of the same events");
    }

    [Fact]
    public async Task Raising_the_level_re_exports_the_whole_session_with_the_same_ids()
    {
        var home = NewHome();
        var (first, _) = await ExportAsync(home, AsfLevel.Metrics);
        var (second, paths) = await ExportAsync(home, AsfLevel.Full);

        second.SessionsSkipped.Should().Be(0);
        second.SessionsExported.Should().Be(1);
        second.EventsWritten.Should().Be(first.EventsWritten,
            "an upgrade re-emits the session from seq 0");

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        manifest.Chunks.Should().HaveCount(2);
        manifest.Cursors.Values.Single().Level.Should().Be(AsfLevel.Full);

        // Two chunks, one at each level, but only one set of event ids.
        var ids = IdsIn(paths);
        ids.Should().HaveCount((int)(first.EventsWritten * 2));
        ids.Distinct().Should().HaveCount((int)first.EventsWritten);
    }

    [Fact]
    public async Task Lowering_the_level_does_nothing()
    {
        var home = NewHome();
        await ExportAsync(home, AsfLevel.Full);
        var (second, paths) = await ExportAsync(home, AsfLevel.Metrics);

        second.SessionsSkipped.Should().Be(1);
        second.EventsWritten.Should().Be(0);
        (await ServiceFor(paths).LoadManifestAsync()).Cursors.Values.Single().Level
            .Should().Be(AsfLevel.Full, "data already sent is not un-sent by asking for less");
    }

    [Fact]
    public async Task A_grown_session_exports_only_its_new_events_and_loses_none()
    {
        var home = NewHome();
        var (first, _) = await ExportAsync(home);
        var firstIds = IdsIn(new BrainPathResolver(home));

        WriteTranscript(home, Lines.Concat(MoreLines));
        var (second, paths) = await ExportAsync(home);

        second.SessionsExported.Should().Be(1);
        second.EventsSkipped.Should().BeGreaterThan(0, "the body of the session is append-only");
        second.EventsWritten.Should().BeLessThan(first.EventsWritten + 4);

        // The real risk: the synthetic session_end moves to a higher seq when the
        // session grows, so a cursor that advanced past it would silently swallow
        // the first genuinely new event. Every event of the grown session must be
        // present across the two chunks.
        var grown = await ReadAllEventsAsync(paths, home);
        var exported = IdsIn(paths).ToHashSet();
        grown.Where(e => e.Kind != AsfKind.SessionEnd)
            .Select(e => e.Id!)
            .Should().OnlyContain(id => exported.Contains(id),
                "no event may fall between two export runs");

        firstIds.Should().OnlyContain(id => exported.Contains(id));
    }

    /// Re-reads the session straight from the source, bypassing the export, so the
    /// test compares against ground truth rather than against itself.
    private static async Task<List<AsfEvent>> ReadAllEventsAsync(IBrainPathResolver paths, string home)
    {
        var source = new ClaudeAsfSource(paths);
        var session = source.Discover().Single();
        var events = new List<AsfEvent>();
        await foreach (var e in source.ReadAsync(session, Masker)) events.Add(e);

        return events;
    }

    [Fact]
    public async Task Force_re_emits_everything()
    {
        var home = NewHome();
        var (first, _) = await ExportAsync(home);
        var (second, _) = await ExportAsync(home, force: true);

        second.SessionsSkipped.Should().Be(0);
        second.EventsWritten.Should().Be(first.EventsWritten);
        second.EventsSkipped.Should().Be(0);
    }

    // ── blobs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_raw_transcript_is_archived_as_a_content_addressed_blob()
    {
        var home = NewHome();
        var (run, paths) = await ExportAsync(home);

        run.BlobsAdded.Should().Be(1);
        run.BlobBytes.Should().Be(new FileInfo(TranscriptPath(home)).Length);

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        var blob = manifest.Blobs.Should().ContainSingle().Subject;
        blob.Kind.Should().Be("claude-transcript");
        blob.Origin.Should().Be(SessionId + ".jsonl", "only the basename — a full path would " +
                                                      "publish the machine's directory layout");
        blob.Origin.Should().NotContain(Path.DirectorySeparatorChar.ToString());
        File.Exists(paths.ExportBlobPath(blob.Sha)).Should().BeTrue();
        manifest.Cursors.Values.Single().BlobSha.Should().Be(blob.Sha);

        // The blob is the escape hatch from our own parser, so it must be the file
        // verbatim — including the secret we masked out of the chunks.
        blob.Sha.Should().Be(Sha256OfFile(TranscriptPath(home)));
    }

    [Fact]
    public async Task An_unchanged_blob_is_stored_once()
    {
        var home = NewHome();
        await ExportAsync(home);
        var (second, paths) = await ExportAsync(home, force: true);

        second.BlobsAdded.Should().Be(0);
        second.BlobsAlreadyPresent.Should().Be(1);
        (await ServiceFor(paths).LoadManifestAsync()).Blobs.Should().ContainSingle();
    }

    [Fact]
    public async Task No_blobs_skips_the_raw_backup()
    {
        var (run, paths) = await ExportAsync(NewHome(), blobs: false);

        run.BlobsAdded.Should().Be(0);
        run.ChunksSealed.Should().Be(1, "chunks are unaffected");
        Directory.Exists(Path.Combine(paths.ExportRoot, "blobs")).Should().BeFalse();
    }

    // ── growing files (spec: docs/specs/asf/05-blob-backup.md §Growing files) ──

    /// The fixture is stamped in the past, so "still being written" is expressed as
    /// a quiet period wide enough to cover it. The service compares wall-clock now
    /// against the file's mtime; nothing else about the fixture changes.
    private static readonly TimeSpan StillLive = TimeSpan.FromDays(3650);

    [Fact]
    public async Task A_session_that_is_still_being_written_defers_its_blob()
    {
        var (run, paths) = await ExportAsync(NewHome(), quietPeriod: StillLive);

        run.BlobsDeferred.Should().Be(1);
        run.BlobsAdded.Should().Be(0);
        run.ChunksSealed.Should().Be(1, "events are exported as usual — only the raw copy waits");

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        manifest.Blobs.Should().BeEmpty();
        manifest.Cursors.Values.Single().BlobSha.Should().BeNull();
    }

    [Fact]
    public async Task A_deferred_blob_is_archived_once_the_session_falls_quiet()
    {
        var home = NewHome();
        await ExportAsync(home, quietPeriod: StillLive);

        // Nothing about the session changed — its events are already exported, so
        // the cursor is satisfied and the session is skipped. The blob must still
        // be picked up, or a session that fell silent while live would never be
        // archived at all.
        var (second, paths) = await ExportAsync(home);

        second.SessionsSkipped.Should().Be(1);
        second.EventsWritten.Should().Be(0);
        second.BlobsAdded.Should().Be(1);

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        var blob = manifest.Blobs.Should().ContainSingle().Subject;
        blob.Sha.Should().Be(Sha256OfFile(TranscriptPath(home)));

        var cursor = manifest.Cursors.Values.Single();
        cursor.BlobSha.Should().Be(blob.Sha);
        cursor.BlobBytes.Should().Be(new FileInfo(TranscriptPath(home)).Length,
            "the recorded size is what stops this session being revisited every run");
    }

    [Fact]
    public async Task A_grown_session_supersedes_its_earlier_blob_without_deleting_it()
    {
        var home = NewHome();
        var (first, _) = await ExportAsync(home);
        var oldSha = (await ServiceFor(new BrainPathResolver(home)).LoadManifestAsync()).Blobs.Single().Sha;

        WriteTranscript(home, Lines.Concat(MoreLines));
        var (second, paths) = await ExportAsync(home);

        first.BlobsAdded.Should().Be(1);
        second.BlobsAdded.Should().Be(1);
        second.BlobsSuperseded.Should().Be(1);
        second.BlobsPruned.Should().Be(0, "the grace period has not passed");

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        var newSha = manifest.Cursors.Values.Single().BlobSha!;
        newSha.Should().Be(Sha256OfFile(TranscriptPath(home))).And.NotBe(oldSha);

        var superseded = manifest.Blobs.Single(b => b.Sha == oldSha);
        superseded.SupersededBy.Should().Be(newSha);
        superseded.SupersededAt.Should().NotBeNull();
        superseded.PrunedAt.Should().BeNull();

        File.Exists(paths.ExportBlobPath(oldSha)).Should().BeTrue(
            "the prefix is kept until we are sure its successor is safe");
        File.Exists(paths.ExportBlobPath(newSha)).Should().BeTrue();
    }

    [Fact]
    public async Task A_superseded_blob_is_pruned_once_its_grace_expires()
    {
        var home = NewHome();
        await ExportAsync(home);
        var oldSha = (await ServiceFor(new BrainPathResolver(home)).LoadManifestAsync()).Blobs.Single().Sha;

        WriteTranscript(home, Lines.Concat(MoreLines));
        await ExportAsync(home);

        // A zero grace is the "seven days later" run: the successor is on disk, so
        // the strict prefix carries nothing the successor lacks.
        var (third, paths) = await ExportAsync(home, supersededGrace: TimeSpan.Zero);

        third.BlobsPruned.Should().Be(1);
        third.BlobBytesPruned.Should().BeGreaterThan(0);
        File.Exists(paths.ExportBlobPath(oldSha)).Should().BeFalse();

        var manifest = await ServiceFor(paths).LoadManifestAsync();
        var pruned = manifest.Blobs.Single(b => b.Sha == oldSha);
        pruned.PrunedAt.Should().NotBeNull(
            "the record outlives the bytes — it is the only evidence they existed");
        File.Exists(paths.ExportBlobPath(pruned.SupersededBy!)).Should().BeTrue();

        // And it stays pruned: a fourth run must not count it again.
        var (fourth, _) = await ExportAsync(home, supersededGrace: TimeSpan.Zero);
        fourth.BlobsPruned.Should().Be(0);
    }

    private static string Sha256OfFile(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);

        return CanonicalJson.Hex(sha.ComputeHash(stream));
    }
}
