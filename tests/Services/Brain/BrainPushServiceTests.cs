using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// The upload half of ASF. Everything here is about one question: after a push,
/// does the manifest's idea of "sent" match what the receiver actually
/// acknowledged?
///
/// Getting that wrong is silent. A chunk stamped `uploadedAt` that never landed
/// is never offered again, and the gap only shows up as a hole in a graph months
/// later — so the stamping rules are pinned rather than trusted.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class BrainPushServiceTests : TestBase
{
    private const string Endpoint = "https://brain.test";

    // ── fixture ───────────────────────────────────────────────────────────────

    /// An export tree with `count` sealed chunks at `level`, plus one blob.
    /// The chunk bodies are not real zstd — nothing on this side of the wire
    /// decompresses them, and the fake server verifies the hash it was given
    /// rather than recomputing it.
    private async Task<(BrainPathResolver Paths, BrainExportService Export)> SeedAsync(
        int count = 2,
        string level = AsfLevel.Full,
        bool withBlob = true)
    {
        var paths = new BrainPathResolver(CreateTempDirectory());
        var export = new BrainExportService(Array.Empty<IAgentSessionSource>(), new SecretMasker(), paths);
        var manifest = new ExportManifest();

        for (var i = 0; i < count; i++)
        {
            var day = new DateOnly(2026, 8, 1).AddDays(i);
            var dir = paths.ExportChunkDir(day);
            Directory.CreateDirectory(dir);
            var name = $"claude-{level}-00.ndjson.zst";
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"chunk-body-{i}"));

            manifest.Chunks.Add(new ChunkManifest
            {
                ChunkHash = Hash($"chunk-{i}"),
                Src = AsfSource.Claude,
                Level = level,
                Day = day.ToString("yyyy-MM-dd"),
                Ordinal = 0,
                Events = 100 + i,
                Bytes = new FileInfo(path).Length,
                UncompressedBytes = 4096,
                SessionIds = new List<string> { $"s_{i}" },
                Path = Path.GetRelativePath(paths.ExportRoot, path).Replace('\\', '/'),
            });
        }

        if (withBlob)
        {
            var sha = Hash("blob-0");
            var blobPath = paths.ExportBlobPath(sha);
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            File.WriteAllBytes(blobPath, Encoding.UTF8.GetBytes("blob-body"));
            manifest.Blobs.Add(new BlobRecord
            {
                Sha = sha,
                Kind = "opencode-tool-output",
                Src = AsfSource.OpenCode,
                Bytes = 9,
                StoredBytes = 9,
                CapturedAt = DateTimeOffset.UtcNow,
            });
        }

        await export.SaveManifestAsync(manifest);

        return (paths, export);
    }

    private static string Hash(string seed) =>
        CanonicalJson.Sha256HexOfString(seed);

    private static (BrainPushService Push, FakeBrainServer Server) PushServiceFor(
        BrainPathResolver paths,
        BrainExportService export,
        FakeBrainServer? server = null)
    {
        server ??= new FakeBrainServer();

        return (new BrainPushService(new HttpClient(server), export, paths), server);
    }

    private static PushOptions Options(bool force = false, bool dryRun = false, string? level = null, bool blobs = true) =>
        new()
        {
            Endpoint = Endpoint,
            Token = "bkt_test",
            Force = force,
            DryRun = dryRun,
            LevelFilter = level,
            IncludeBlobs = blobs,
            RetryBaseDelay = TimeSpan.Zero,
        };

    // ── the happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Uploads_every_chunk_the_server_is_missing_and_stamps_the_manifest()
    {
        var (paths, export) = await SeedAsync();
        var (push, server) = PushServiceFor(paths, export);

        var run = await push.RunAsync(Options(), NullPushProgress.Instance);

        run.ChunksUploaded.Should().Be(2);
        run.BlobsUploaded.Should().Be(1);
        run.Accepted.Should().Be(201, "the fake server counts the events the chunks declared");
        run.Failures.Should().BeEmpty();
        server.Commits.Should().Be(1);

        var manifest = await export.LoadManifestAsync();
        manifest.Chunks.Should().OnlyContain(c => c.UploadedAt != null && c.SyncId != null);
        manifest.Blobs.Should().OnlyContain(b => b.UploadedAt != null);
        manifest.Endpoint.Should().Be(Endpoint);
    }

    [Fact]
    public async Task Sends_nothing_the_second_time()
    {
        var (paths, export) = await SeedAsync();
        var (push, _) = PushServiceFor(paths, export);
        await push.RunAsync(Options(), NullPushProgress.Instance);

        var (again, server) = PushServiceFor(paths, export);
        var run = await again.RunAsync(Options(), NullPushProgress.Instance);

        run.ChunksConsidered.Should().Be(0);
        run.Syncs.Should().Be(0);
        server.Requests.Should().BeEmpty("an already-pushed manifest must not even open a sync session");
    }

    [Fact]
    public async Task Force_re_offers_chunks_but_the_server_still_stores_them_once()
    {
        var (paths, export) = await SeedAsync();
        var (push, server) = PushServiceFor(paths, export);
        await push.RunAsync(Options(), NullPushProgress.Instance);

        var run = await push.RunAsync(Options(force: true), NullPushProgress.Instance);

        run.ChunksConsidered.Should().Be(2);
        run.ChunksUploaded.Should().Be(0, "the manifest step answered 'already have it'");
        run.ChunksKnown.Should().Be(2);
        server.PutCount.Should().Be(3, "two chunks and one blob, from the first push only");
    }

    [Fact]
    public async Task Dry_run_touches_the_network_not_at_all()
    {
        var (paths, export) = await SeedAsync();
        var (push, server) = PushServiceFor(paths, export);

        var run = await push.RunAsync(Options(dryRun: true), NullPushProgress.Instance);

        run.ChunksConsidered.Should().Be(2);
        server.Requests.Should().BeEmpty();
        (await export.LoadManifestAsync()).Chunks.Should().OnlyContain(c => c.UploadedAt == null);
    }

    // ── what must never be claimed ────────────────────────────────────────────

    [Fact]
    public async Task A_chunk_whose_file_is_gone_is_reported_not_claimed()
    {
        var (paths, export) = await SeedAsync();
        var manifest = await export.LoadManifestAsync();
        var orphan = manifest.Chunks[0];
        File.Delete(Path.Combine(paths.ExportRoot, orphan.Path!));

        var (push, _) = PushServiceFor(paths, export);
        var run = await push.RunAsync(Options(), NullPushProgress.Instance);

        run.ChunksMissingLocally.Should().Be(1);
        run.ChunksUploaded.Should().Be(1);

        var after = await export.LoadManifestAsync();
        after.Chunks.Single(c => c.ChunkHash == orphan.ChunkHash).UploadedAt
            .Should().BeNull("a chunk that never left the machine must be offered again after a re-export");
    }

    [Fact]
    public async Task A_hash_mismatch_drops_that_chunk_and_the_rest_still_lands()
    {
        var (paths, export) = await SeedAsync();
        var manifest = await export.LoadManifestAsync();
        var bad = manifest.Chunks[0].ChunkHash;

        var server = new FakeBrainServer { RejectWithMismatch = { bad } };
        var (push, _) = PushServiceFor(paths, export, server);

        var run = await push.RunAsync(Options(), NullPushProgress.Instance);

        run.Failures.Should().ContainSingle().Which.Should().Contain("hash_mismatch");
        run.ChunksUploaded.Should().Be(1);
        server.Commits.Should().Be(1, "the batch was reopened without the bad chunk and then committed");

        var after = await export.LoadManifestAsync();
        after.Chunks.Single(c => c.ChunkHash == bad).UploadedAt.Should().BeNull();
        after.Chunks.Single(c => c.ChunkHash != bad).UploadedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Nothing_is_stamped_when_the_commit_never_succeeds()
    {
        var (paths, export) = await SeedAsync();
        var server = new FakeBrainServer { FailCommitWith = HttpStatusCode.Gone, FailCommitCode = "sync_expired" };
        var (push, _) = PushServiceFor(paths, export, server);

        var act = () => push.RunAsync(Options(), NullPushProgress.Instance);

        await act.Should().ThrowAsync<BrainPushException>();
        server.Commits.Should().Be(2, "an expired session is reopened once, then the failure is real");
        (await export.LoadManifestAsync()).Chunks.Should().OnlyContain(c => c.UploadedAt == null,
            "bytes on the server that were never committed are not data the profile can count");
    }

    // ── refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_level_refusal_stops_the_push_instead_of_dropping_data_quietly()
    {
        var (paths, export) = await SeedAsync();
        var server = new FakeBrainServer { FailSyncWith = HttpStatusCode.Forbidden, FailSyncCode = "level_exceeded" };
        var (push, _) = PushServiceFor(paths, export, server);

        var act = () => push.RunAsync(Options(), NullPushProgress.Instance);

        var ex = (await act.Should().ThrowAsync<BrainPushException>()).Which;
        ex.Code.Should().Be("level_exceeded");
        ex.IsFatal.Should().BeTrue();
    }

    [Fact]
    public async Task A_500_is_retried_and_the_push_still_completes()
    {
        var (paths, export) = await SeedAsync(count: 1, withBlob: false);
        var server = new FakeBrainServer { FailNextSyncAttempts = 2 };
        var (push, _) = PushServiceFor(paths, export, server);

        var run = await push.RunAsync(Options(), NullPushProgress.Instance);

        run.ChunksUploaded.Should().Be(1);
        server.SyncAttempts.Should().Be(3, "two failures then the success");
    }

    // ── level and endpoint rules ──────────────────────────────────────────────

    [Fact]
    public async Task Blobs_do_not_travel_with_a_metrics_export()
    {
        var (paths, export) = await SeedAsync(level: AsfLevel.Metrics);
        var (push, server) = PushServiceFor(paths, export);

        var run = await push.RunAsync(Options(), NullPushProgress.Instance);

        run.BlobsConsidered.Should().Be(0, "raw transcripts are full-level data whatever the token allows");
        run.ChunksUploaded.Should().Be(2);
        server.Requests.Should().NotContain(r => r.Contains("/blob/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pointing_at_a_new_endpoint_offers_everything_again()
    {
        var (paths, export) = await SeedAsync();
        var (push, _) = PushServiceFor(paths, export);
        await push.RunAsync(Options(), NullPushProgress.Instance);

        var (elsewhere, server) = PushServiceFor(paths, export);
        var run = await elsewhere.RunAsync(
            new PushOptions { Endpoint = "https://selfhosted.example", Token = "bkt_test", RetryBaseDelay = TimeSpan.Zero },
            NullPushProgress.Instance);

        run.EndpointChanged.Should().BeTrue();
        run.ChunksUploaded.Should().Be(2, "the new host has none of it");
        server.Requests.Should().OnlyContain(r => r.Contains("selfhosted.example", StringComparison.Ordinal));
        (await export.LoadManifestAsync()).Endpoint.Should().Be("https://selfhosted.example");
    }

    [Theory]
    [InlineData("agentics.dk", "https://agentics.dk")]
    [InlineData("https://agentics.dk/", "https://agentics.dk")]
    [InlineData("http://localhost:3001", "http://localhost:3001")]
    [InlineData("", "https://agentics.dk")]
    public void Endpoints_are_normalized_before_they_are_compared(string input, string expected) =>
        BrainPushService.NormalizeEndpoint(input).Should().Be(expected);

    // ── a receiver that behaves like the spec ─────────────────────────────────

    /// Implements just enough of docs/specs/asf/04-sync-protocol.md to hold the
    /// client honest: content-addressed storage, missing-only replies, and a
    /// commit that refuses a manifest whose chunks never arrived.
    private sealed class FakeBrainServer : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public readonly HashSet<string> Stored = new(StringComparer.Ordinal);
        public readonly HashSet<string> RejectWithMismatch = new(StringComparer.Ordinal);
        public int PutCount;
        public int Commits;
        public int SyncAttempts;

        /// Number of leading `POST /sync` attempts to answer with a 503.
        public int FailNextSyncAttempts;

        public HttpStatusCode? FailSyncWith;
        public string FailSyncCode = "unauthorized";
        public HttpStatusCode? FailCommitWith;
        public string FailCommitCode = "sync_expired";

        private readonly Dictionary<string, (List<(string Hash, int Events)> Chunks, List<string> Blobs)> _sessions =
            new(StringComparer.Ordinal);

        private int _sync;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");

            if (url.EndsWith("/api/brain/v1/sync", StringComparison.Ordinal))
                return await SyncAsync(request, ct);
            if (url.EndsWith("/api/brain/v1/sync/commit", StringComparison.Ordinal))
                return await CommitAsync(request, ct);

            return await PutAsync(request, url, ct);
        }

        private async Task<HttpResponseMessage> SyncAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SyncAttempts++;
            if (SyncAttempts <= FailNextSyncAttempts) return Error(HttpStatusCode.ServiceUnavailable, "internal");
            if (FailSyncWith is { } status) return Error(status, FailSyncCode);

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            var chunks = body["chunks"]!.AsArray()
                .Select(c => (Hash: c!["chunkHash"]!.GetValue<string>(), Events: c["events"]!.GetValue<int>()))
                .ToList();
            var blobs = body["blobs"]!.AsArray().Select(b => b!["sha"]!.GetValue<string>()).ToList();

            var syncId = $"sy_{++_sync:D4}";
            _sessions[syncId] = (chunks, blobs);

            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["syncId"] = syncId,
                ["missingChunks"] = new JsonArray(chunks.Where(c => !Stored.Contains(c.Hash))
                    .Select(c => (JsonNode)JsonValue.Create(c.Hash)!).ToArray()),
                ["missingBlobs"] = new JsonArray(blobs.Where(b => !Stored.Contains(b))
                    .Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
                ["knownChunks"] = chunks.Count(c => Stored.Contains(c.Hash)),
                ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            });
        }

        private async Task<HttpResponseMessage> PutAsync(HttpRequestMessage request, string url, CancellationToken ct)
        {
            PutCount++;
            _ = await request.Content!.ReadAsByteArrayAsync(ct);
            var hash = url[(url.LastIndexOf('/') + 1)..];

            if (RejectWithMismatch.Contains(hash))
                return Error(HttpStatusCode.Conflict, "hash_mismatch");

            var duplicate = !Stored.Add(hash);

            return Json(duplicate ? HttpStatusCode.OK : HttpStatusCode.Created,
                new JsonObject { ["duplicate"] = duplicate });
        }

        private async Task<HttpResponseMessage> CommitAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Commits++;
            if (FailCommitWith is { } status) return Error(status, FailCommitCode);

            var syncId = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["syncId"]!.GetValue<string>();
            var (chunks, blobs) = _sessions[syncId];

            var missing = chunks.Where(c => !Stored.Contains(c.Hash)).Select(c => c.Hash).ToList();
            if (missing.Count > 0)
            {
                return Json(HttpStatusCode.Conflict, new JsonObject
                {
                    ["code"] = "chunks_missing",
                    ["message"] = "Some chunks were never uploaded.",
                });
            }

            _ = blobs;

            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["accepted"] = chunks.Sum(c => c.Events),
                ["enriched"] = 0,
                ["duplicate"] = 0,
                ["rejected"] = 0,
                ["masked"] = 0,
                ["days"] = new JsonArray(new JsonNode[] { JsonValue.Create("2026-08-01")! }),
                ["storageBytes"] = 4096,
            });
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
            Json(status, new JsonObject { ["code"] = code, ["message"] = code.Replace('_', ' ') });

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
            new(status)
            {
                Content = new StringContent(body.ToJsonString(new JsonSerializerOptions()), Encoding.UTF8, "application/json"),
            };
    }
}
