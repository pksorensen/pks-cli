using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;
using ZstdSharp;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// The way back. Spec: docs/specs/asf/05-blob-backup.md §Restore.
///
/// A restore runs when something has already gone wrong, so the failure modes
/// that matter are the ones that make it worse: writing a truncated file over a
/// good one, putting a transcript in the wrong project, or letting a filename
/// from the server escape the target directory. Each of those is pinned here.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class BrainRestoreServiceTests : TestBase
{
    private const string Endpoint = "https://brain.test";

    // ── fixture ───────────────────────────────────────────────────────────────

    private sealed record Seed(TestPaths Paths, BrainExportService Export, string Home, string Target);

    private Seed NewSeed()
    {
        var home = CreateTempDirectory();
        var paths = new TestPaths(home);
        var export = new BrainExportService(Array.Empty<IAgentSessionSource>(), new SecretMasker(), paths);

        return new Seed(paths, export, home, Path.Combine(CreateTempDirectory(), "restore"));
    }

    /// Writes a real zstd blob into the local store and returns its manifest row.
    private static BlobRecord StoreBlob(
        TestPaths paths,
        string content,
        string kind = "opencode-tool-output",
        string? origin = null,
        DateTimeOffset? capturedAt = null)
    {
        var raw = Encoding.UTF8.GetBytes(content);
        var sha = CanonicalJson.Hex(SHA256.HashData(raw));
        WriteBlobFile(paths.ExportBlobPath(sha), raw);

        return new BlobRecord
        {
            Sha = sha,
            Kind = kind,
            Src = AsfSource.OpenCode,
            Bytes = raw.Length,
            StoredBytes = new FileInfo(paths.ExportBlobPath(sha)).Length,
            CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
            Origin = origin ?? "tool_0001",
        };
    }

    private static void WriteBlobFile(string path, byte[] raw)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        using var compressor = new CompressionStream(output);
        compressor.Write(raw);
    }

    private static byte[] Compress(byte[] raw)
    {
        using var memory = new MemoryStream();
        using (var compressor = new CompressionStream(memory))
        {
            compressor.Write(raw);
        }

        return memory.ToArray();
    }

    private static async Task<BrainRestoreService> ServiceAsync(Seed seed, params BlobRecord[] blobs)
    {
        var manifest = new ExportManifest { Endpoint = Endpoint };
        manifest.Blobs.AddRange(blobs);
        await seed.Export.SaveManifestAsync(manifest);

        return new BrainRestoreService(new HttpClient(new NeverCalled()), seed.Export, seed.Paths);
    }

    private RestoreOptions Options(Seed seed, bool inPlace = false, bool overwrite = false, bool dryRun = false,
        bool fromRemote = false, string? kind = null, DateTimeOffset? since = null) =>
        new()
        {
            Endpoint = Endpoint,
            Token = "bkt_test",
            FromRemote = fromRemote,
            Kind = kind,
            Since = since,
            TargetDir = seed.Target,
            InPlace = inPlace,
            Overwrite = overwrite,
            DryRun = dryRun,
            RetryBaseDelay = TimeSpan.Zero,
        };

    // ── the local half: works with no server at all ───────────────────────────

    [Fact]
    public async Task Restores_from_the_local_blob_store_without_touching_the_network()
    {
        var seed = NewSeed();
        var blob = StoreBlob(seed.Paths, "the full tool output, all 91 KB of it");
        var service = await ServiceAsync(seed, blob);

        var run = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        run.Restored.Should().Be(1);
        run.FromLocalStore.Should().Be(1, "the bytes were already on this disk");
        run.Downloaded.Should().Be(0);
        File.ReadAllText(Path.Combine(seed.Target, "opencode-tool-output", "tool_0001"))
            .Should().Be("the full tool output, all 91 KB of it");
    }

    [Fact]
    public async Task A_blob_whose_bytes_do_not_hash_to_its_address_is_never_written()
    {
        var seed = NewSeed();
        var blob = StoreBlob(seed.Paths, "honest content");

        // Same address, different bytes — what a corrupted archive or a mixed-up
        // server would look like.
        WriteBlobFile(seed.Paths.ExportBlobPath(blob.Sha), Encoding.UTF8.GetBytes("tampered content"));
        var service = await ServiceAsync(seed, blob);

        var run = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        run.HashMismatches.Should().Be(1);
        run.Restored.Should().Be(0);
        run.Failures.Should().ContainSingle().Which.Should().Contain("nothing was written");
        File.Exists(Path.Combine(seed.Target, "opencode-tool-output", "tool_0001")).Should().BeFalse();
        Directory.GetFiles(Path.Combine(seed.Target, "opencode-tool-output"))
            .Should().BeEmpty("not even a temp file survives a failed verification");
    }

    [Fact]
    public async Task An_existing_file_is_left_alone_unless_overwrite_is_asked_for()
    {
        var seed = NewSeed();
        var blob = StoreBlob(seed.Paths, "restored");
        var service = await ServiceAsync(seed, blob);
        var destination = Path.Combine(seed.Target, "opencode-tool-output", "tool_0001");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "whatever is there now");

        var skipped = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        skipped.SkippedExisting.Should().Be(1);
        skipped.Restored.Should().Be(0);
        File.ReadAllText(destination).Should().Be("whatever is there now");

        var forced = await service.RunAsync(Options(seed, overwrite: true), NullRestoreProgress.Instance);

        forced.Restored.Should().Be(1);
        File.ReadAllText(destination).Should().Be("restored");
    }

    [Fact]
    public async Task A_dry_run_plans_every_destination_and_writes_nothing()
    {
        var seed = NewSeed();
        var service = await ServiceAsync(seed, StoreBlob(seed.Paths, "output"));

        var run = await service.RunAsync(Options(seed, dryRun: true), NullRestoreProgress.Instance);

        run.Plan.Should().ContainSingle().Which.Local.Should().BeTrue();
        run.Restored.Should().Be(0);
        Directory.Exists(seed.Target).Should().BeFalse("a dry run does not even create the target");
    }

    [Fact]
    public async Task Pruned_and_superseded_blobs_are_not_offered_by_the_local_catalog()
    {
        var seed = NewSeed();
        var live = StoreBlob(seed.Paths, "current", origin: "tool_0003");
        var pruned = StoreBlob(seed.Paths, "gone", origin: "tool_0001");
        pruned.PrunedAt = DateTimeOffset.UtcNow;
        var prefix = StoreBlob(seed.Paths, "a shorter prefix", origin: "tool_0002");
        prefix.SupersededBy = live.Sha;
        var service = await ServiceAsync(seed, live, pruned, prefix);

        var run = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        run.BlobsListed.Should().Be(1);
        run.Restored.Should().Be(1);
        Directory.GetFiles(Path.Combine(seed.Target, "opencode-tool-output"))
            .Select(Path.GetFileName).Should().BeEquivalentTo(["tool_0003"]);
    }

    [Fact]
    public async Task The_kind_and_since_filters_narrow_the_local_catalog()
    {
        var seed = NewSeed();
        var old = StoreBlob(seed.Paths, "old spill", origin: "tool_0001",
            capturedAt: DateTimeOffset.UtcNow.AddDays(-40));
        var recent = StoreBlob(seed.Paths, "recent spill", origin: "tool_0002");
        var transcript = StoreBlob(seed.Paths, "a claude session", kind: "claude-transcript", origin: "s1.jsonl");
        var service = await ServiceAsync(seed, old, recent, transcript);

        var byKind = await service.RunAsync(
            Options(seed, kind: "opencode-tool-output"), NullRestoreProgress.Instance);
        byKind.BlobsListed.Should().Be(2);

        var bySince = await service.RunAsync(
            Options(seed, since: DateTimeOffset.UtcNow.AddDays(-30), overwrite: true), NullRestoreProgress.Instance);
        bySince.BlobsListed.Should().Be(2, "the 40-day-old spill is outside the window");
        bySince.Plan.Select(p => Path.GetFileName(p.Destination))
            .Should().BeEquivalentTo(["tool_0002", "s1.jsonl"]);
    }

    // ── where the bytes land ──────────────────────────────────────────────────

    [Fact]
    public async Task In_place_puts_an_opencode_spill_back_where_opencode_looks_for_it()
    {
        var seed = NewSeed();
        var service = await ServiceAsync(seed, StoreBlob(seed.Paths, "the spilled output"));

        var run = await service.RunAsync(Options(seed, inPlace: true), NullRestoreProgress.Instance);

        run.Restored.Should().Be(1);
        File.ReadAllText(Path.Combine(seed.Paths.OpenCodeToolOutputRoot, "tool_0001"))
            .Should().Be("the spilled output", "the database still holds a pointer to exactly this path");
    }

    [Fact]
    public async Task In_place_refuses_the_kinds_whose_original_location_is_a_guess()
    {
        var seed = NewSeed();
        var service = await ServiceAsync(
            seed, StoreBlob(seed.Paths, "a claude session", kind: "claude-transcript", origin: "s1.jsonl"));

        var run = await service.RunAsync(Options(seed, inPlace: true), NullRestoreProgress.Instance);

        run.SkippedNoLocation.Should().Be(1, "a transcript lives under a project slug the server was never told");
        run.Restored.Should().Be(0);
        Directory.Exists(seed.Target).Should().BeFalse();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData("")]
    public async Task An_origin_from_the_wire_can_never_escape_the_target_directory(string origin)
    {
        var seed = NewSeed();
        var blob = StoreBlob(seed.Paths, "payload", origin: origin);
        var service = await ServiceAsync(seed, blob);

        var run = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        run.Restored.Should().Be(1);
        var written = run.Plan.Single().Destination;
        Path.GetFullPath(written).Should().StartWith(Path.GetFullPath(seed.Target));
        Path.GetFileName(written).Should().StartWith(blob.Sha, "an unusable name falls back to the content address");
    }

    [Fact]
    public async Task When_two_blobs_want_the_same_file_the_newer_capture_wins()
    {
        var seed = NewSeed();
        var older = StoreBlob(seed.Paths, "the first half", origin: "session.jsonl",
            capturedAt: DateTimeOffset.UtcNow.AddDays(-1));
        var newer = StoreBlob(seed.Paths, "the first half and the rest", origin: "session.jsonl");
        var service = await ServiceAsync(seed, older, newer);

        var run = await service.RunAsync(Options(seed), NullRestoreProgress.Instance);

        run.Restored.Should().Be(1);
        File.ReadAllText(Path.Combine(seed.Target, "opencode-tool-output", "session.jsonl"))
            .Should().Be("the first half and the rest");
    }

    // ── the remote half ───────────────────────────────────────────────────────

    [Fact]
    public async Task From_remote_downloads_what_this_machine_no_longer_has()
    {
        var seed = NewSeed();
        var server = new FakeBlobServer();
        server.Add("tool_0007", "opencode-tool-output", "the output this laptop lost");
        var service = new BrainRestoreService(new HttpClient(server), seed.Export, seed.Paths);

        var run = await service.RunAsync(Options(seed, fromRemote: true), NullRestoreProgress.Instance);

        run.Downloaded.Should().Be(1);
        run.FromLocalStore.Should().Be(0);
        run.BytesDownloaded.Should().BeGreaterThan(0);
        File.ReadAllText(Path.Combine(seed.Target, "opencode-tool-output", "tool_0007"))
            .Should().Be("the output this laptop lost");
        server.Requests.Should().Contain(r => r.Contains("/api/brain/v1/blobs?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task From_remote_passes_the_filters_to_the_server_and_reports_truncation()
    {
        var seed = NewSeed();
        var server = new FakeBlobServer { Truncated = true };
        server.Add("tool_0007", "opencode-tool-output", "output");
        var service = new BrainRestoreService(new HttpClient(server), seed.Export, seed.Paths);

        var run = await service.RunAsync(
            Options(seed, fromRemote: true, kind: "opencode-tool-output",
                since: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
            NullRestoreProgress.Instance);

        run.Truncated.Should().BeTrue("a full page means the answer was not complete");
        var catalog = server.Requests.Single(r => r.Contains("/blobs?", StringComparison.Ordinal));
        catalog.Should().Contain("kind=opencode-tool-output").And.Contain("since=2026-07-01");
    }

    [Fact]
    public async Task A_local_copy_is_preferred_over_downloading_the_same_bytes_again()
    {
        var seed = NewSeed();
        var server = new FakeBlobServer();
        var sha = server.Add("tool_0007", "opencode-tool-output", "identical bytes");
        WriteBlobFile(seed.Paths.ExportBlobPath(sha), Encoding.UTF8.GetBytes("identical bytes"));
        var service = new BrainRestoreService(new HttpClient(server), seed.Export, seed.Paths);

        var run = await service.RunAsync(Options(seed, fromRemote: true), NullRestoreProgress.Instance);

        run.FromLocalStore.Should().Be(1);
        server.Requests.Should().NotContain(r => r.Contains("/api/brain/v1/blob/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_credential_below_full_level_is_refused_by_the_catalog_not_worked_around()
    {
        var seed = NewSeed();
        var server = new FakeBlobServer { FailCatalogWith = HttpStatusCode.Forbidden };
        var service = new BrainRestoreService(new HttpClient(server), seed.Export, seed.Paths);

        var act = () => service.RunAsync(Options(seed, fromRemote: true), NullRestoreProgress.Instance);

        (await act.Should().ThrowAsync<BrainPushException>()).Which.Code.Should().Be("level_exceeded");
    }

    [Fact]
    public async Task A_blob_the_catalog_lists_but_the_store_has_lost_fails_only_itself()
    {
        var seed = NewSeed();
        var server = new FakeBlobServer();
        server.Add("tool_0007", "opencode-tool-output", "still there");
        server.Forget(server.Add("tool_0008", "opencode-tool-output", "gone from the store"));
        var service = new BrainRestoreService(new HttpClient(server), seed.Export, seed.Paths);

        var run = await service.RunAsync(Options(seed, fromRemote: true), NullRestoreProgress.Instance);

        run.Restored.Should().Be(1, "one bad blob does not sink the restore");
        run.Failures.Should().ContainSingle().Which.Should().Contain("not_found");
        File.Exists(Path.Combine(seed.Target, "opencode-tool-output", "tool_0007")).Should().BeTrue();
    }

    // ── doubles ───────────────────────────────────────────────────────────────

    /// Asserts the local path really is local: any request at all is a bug.
    private sealed class NeverCalled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"The local restore path must not call the network: {request.RequestUri}");
    }

    /// Just enough of `GET /blobs` and `GET /blob/<sha>` to hold the client honest.
    private sealed class FakeBlobServer : HttpMessageHandler
    {
        public readonly List<string> Requests = new();
        public bool Truncated;
        public HttpStatusCode? FailCatalogWith;

        private readonly List<JsonObject> _catalog = new();
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);

        public string Add(string origin, string kind, string content)
        {
            var raw = Encoding.UTF8.GetBytes(content);
            var sha = CanonicalJson.Hex(SHA256.HashData(raw));
            _bytes[sha] = Compress(raw);
            _catalog.Add(new JsonObject
            {
                ["sha"] = sha,
                ["kind"] = kind,
                ["bytes"] = raw.Length,
                ["origin"] = origin,
                ["capturedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            });

            return sha;
        }

        /// The catalog still lists it; the bytes are gone.
        public void Forget(string sha) => _bytes.Remove(sha);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add($"{request.Method} {url}");

            if (url.Contains("/api/brain/v1/blobs", StringComparison.Ordinal))
            {
                if (FailCatalogWith is { } status) return Task.FromResult(Error(status, "level_exceeded"));

                return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject
                {
                    ["blobs"] = new JsonArray(_catalog.Select(b => (JsonNode)b.DeepClone()).ToArray()),
                    ["totalBytes"] = _catalog.Count,
                    ["truncated"] = Truncated,
                }));
            }

            var sha = url[(url.LastIndexOf('/') + 1)..];
            if (!_bytes.TryGetValue(sha, out var payload))
                return Task.FromResult(Error(HttpStatusCode.NotFound, "bad_request"));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
            Json(status, new JsonObject { ["code"] = code, ["message"] = code.Replace('_', ' ') });

        private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
            new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    /// A path resolver pinned to a temp home. `BrainPathResolver` honours
    /// XDG_DATA_HOME for the opencode roots, which on a real machine would point
    /// the `--in-place` test at the developer's own spill directory.
    private sealed class TestPaths(string home) : IBrainPathResolver
    {
        private readonly BrainPathResolver _inner = new(home);

        public string OpenCodeToolOutputRoot => Path.Combine(home, ".local", "share", "opencode", "tool-output");
        public string OpenCodeDbPath => Path.Combine(home, ".local", "share", "opencode", "opencode.db");

        public string ClaudeProjectsRoot => _inner.ClaudeProjectsRoot;
        public string ClaudePlansRoot => _inner.ClaudePlansRoot;
        public string CodexSessionsRoot => _inner.CodexSessionsRoot;
        public string CodexArchivedSessionsRoot => _inner.CodexArchivedSessionsRoot;
        public string ExportRoot => _inner.ExportRoot;
        public string ExportChunkDir(DateOnly day) => _inner.ExportChunkDir(day);
        public string ExportBlobPath(string sha) => _inner.ExportBlobPath(sha);
        public string ExportManifestPath => _inner.ExportManifestPath;
        public string GlobalRoot => _inner.GlobalRoot;
        public string GlobalProjectDir(string slug) => _inner.GlobalProjectDir(slug);
        public string GlobalSessionFile(string slug, string sessionId) => _inner.GlobalSessionFile(slug, sessionId);
        public string GlobalFirehose(BrainFirehose firehose) => _inner.GlobalFirehose(firehose);
        public string GlobalIndexPath => _inner.GlobalIndexPath;
        public string GlobalIngestRunsPath => _inner.GlobalIngestRunsPath;
        public string GlobalPlansIndexPath => _inner.GlobalPlansIndexPath;
        public string? ResolveProjectRoot(string cwd) => _inner.ResolveProjectRoot(cwd);
        public string EncodeSlug(string realPath) => _inner.EncodeSlug(realPath);
        public string DecodeSlug(string slug) => _inner.DecodeSlug(slug);
        public string? Normalize(string? path) => _inner.Normalize(path);
    }
}
