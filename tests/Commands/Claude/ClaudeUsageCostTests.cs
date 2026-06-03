using System.Text;
using System.Text.Json;
using FluentAssertions;
using PKS.Commands.Claude;
using Xunit;

namespace PKS.CLI.Tests.Commands.Claude;

/// <summary>
/// Regression tests for <see cref="ClaudeUsageCommand"/> cost de-duplication.
///
/// A single billed Anthropic request is written to the transcript as many rows — one per
/// content block (thinking / text / tool_use), each repeating the same requestId,
/// message.id and the request's *cumulative* usage. Naively summing per-row over-counts
/// cost (~2.5x in real data, up to 57x for one big agentic turn). These tests pin the
/// invariant: count each billed request (requestId + message.id) exactly once, both within
/// a file and across files (forked / resumed / workflow re-logging).
/// </summary>
public class ClaudeUsageCostTests
{
    // No LiteLLM doc -> pricing falls back to the hardcoded table (opus-4-8 is present).
    private static readonly JsonElement? NoLiteLLM = null;

    private static Dictionary<string, ClaudeUsageCommand.ModelPricing> PricingCache() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One assistant response, big usage, written as N block-rows that all repeat
    /// the same requestId/message.id and cumulative usage (the real-world shape).</summary>
    private static string MultiBlockResponse(string reqId, string msgId, string model, int blocks,
        long input, long output, long cacheCreate, long cacheRead)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < blocks; i++)
        {
            var line = new
            {
                type = "assistant",
                timestamp = $"2026-05-31T21:0{i % 6}:0{i % 6}.000Z",
                requestId = reqId,
                uuid = $"{msgId}-block-{i}",
                message = new
                {
                    id = msgId,
                    role = "assistant",
                    model,
                    stop_reason = "end_turn",
                    content = new[] { new { type = i % 2 == 0 ? "thinking" : "tool_use" } },
                    usage = new
                    {
                        input_tokens = input,
                        output_tokens = output,
                        cache_creation_input_tokens = cacheCreate,
                        cache_read_input_tokens = cacheRead,
                    }
                }
            };
            sb.AppendLine(JsonSerializer.Serialize(line));
        }
        return sb.ToString();
    }

    private static async Task<List<ClaudeUsageCommand.UsageRow>> ParseAsync(string contents)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, contents);
            return await ClaudeUsageCommand.ParseUsageRowsAsync(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task ParseUsageRows_CollapsesOneResponseWrittenAs57Blocks_ToASingleRow()
    {
        // The real 05-31 case: msg_01Pm... appeared 57 times with identical usage.
        var contents = MultiBlockResponse("req_A", "msg_A", "claude-opus-4-8", blocks: 57,
            input: 201, output: 41850, cacheCreate: 4013, cacheRead: 574382);

        var rows = await ParseAsync(contents);

        rows.Should().HaveCount(1, "57 content-block rows are one billed request");
        rows[0].Output.Should().Be(41850);
        rows[0].CacheRead.Should().Be(574382);
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildEntries_PricesEachRequestOnce_NotPerBlock()
    {
        var rows = await ParseAsync(MultiBlockResponse("req_A", "msg_A", "claude-opus-4-8", 57,
            201, 41850, 4013, 574382));

        var entries = ClaudeUsageCommand.BuildEntries(rows, NoLiteLLM, PricingCache());

        // opus-4-8 ($5/$25 per Mtok): in 5e-6, out 2.5e-5, cacheCreate 6.25e-6, cacheRead 5e-7
        double expected = 201 * 5e-6 + 41850 * 2.5e-5 + 4013 * 6.25e-6 + 574382 * 5e-7;
        entries.Should().HaveCount(1);
        entries[0].Cost.Should().BeApproximately(expected, 1e-6);
        // Sanity: the naive (buggy) number would be 57x this.
        entries[0].Cost.Should().BeLessThan(expected * 1.5);
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildEntries_DedupsSameRequestAcrossFiles_ForkResumeOrWorkflowReLogging()
    {
        // Same billed request (same requestId+message.id) re-logged into a second file —
        // e.g. --fork copies the parent transcript, or a workflow subagent response is
        // mirrored. Must be counted once across the merged set.
        var rowsA = await ParseAsync(MultiBlockResponse("req_X", "msg_X", "claude-opus-4-7", 3,
            100, 2000, 0, 0));
        var rowsB = await ParseAsync(MultiBlockResponse("req_X", "msg_X", "claude-opus-4-7", 5,
            100, 2000, 0, 0));

        var merged = rowsA.Concat(rowsB);
        var entries = ClaudeUsageCommand.BuildEntries(merged, NoLiteLLM, PricingCache());

        entries.Should().HaveCount(1, "the same requestId is one bill regardless of how many files log it");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildEntries_KeepsGenuinelySeparateRequests()
    {
        // Distinct requestIds = distinct billed calls (e.g. real workflow subagents) — keep all.
        var a = await ParseAsync(MultiBlockResponse("req_1", "msg_1", "claude-sonnet-4-6", 4, 50, 1000, 0, 0));
        var b = await ParseAsync(MultiBlockResponse("req_2", "msg_2", "claude-sonnet-4-6", 4, 50, 1000, 0, 0));
        var c = await ParseAsync(MultiBlockResponse("req_3", "msg_3", "claude-sonnet-4-6", 4, 50, 1000, 0, 0));

        var entries = ClaudeUsageCommand.BuildEntries(a.Concat(b).Concat(c), NoLiteLLM, PricingCache());

        entries.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "Claude")]
    public void GetJsonlFiles_WithSessionFilter_KeepsOnlyMatchingSessionsAcrossProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "pks-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Two unrelated project folders, three sessions total scattered across them.
            var projA = Path.Combine(root, "-proj-a");
            var projB = Path.Combine(root, "-proj-b");
            Directory.CreateDirectory(projA);
            Directory.CreateDirectory(projB);
            var wanted1 = Path.Combine(projA, "aaaa1111-2222-3333-4444-555566667777.jsonl");
            var wanted2 = Path.Combine(projB, "bbbb1111-2222-3333-4444-555566667777.jsonl");
            var other   = Path.Combine(projA, "cccc1111-2222-3333-4444-555566667777.jsonl");
            File.WriteAllText(wanted1, "");
            File.WriteAllText(wanted2, "");
            File.WriteAllText(other, "");

            // Full id + short prefix, no project filter — both wanted sessions, ignoring folders.
            var files = ClaudeUsageCommand
                .GetJsonlFiles(root, projectName: null, sessions: ["aaaa1111-2222-3333-4444-555566667777", "bbbb1111"])
                .ToList();

            files.Should().BeEquivalentTo([wanted1, wanted2]);
            files.Should().NotContain(other);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task ParseUsageRows_KeepsRowsWithoutIds_CannotBeDeduped()
    {
        // Legacy/edge rows with neither requestId nor message.id get a file-local key and
        // are all kept (we can't prove they're the same request).
        var line1 = JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = "2026-05-31T10:00:00.000Z",
            message = new { model = "claude-haiku-4-5-20251001", usage = new { input_tokens = 10, output_tokens = 100 } }
        });
        var line2 = JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = "2026-05-31T10:00:01.000Z",
            message = new { model = "claude-haiku-4-5-20251001", usage = new { input_tokens = 10, output_tokens = 100 } }
        });

        var rows = await ParseAsync(line1 + "\n" + line2 + "\n");

        rows.Should().HaveCount(2);
        var entries = ClaudeUsageCommand.BuildEntries(rows, NoLiteLLM, PricingCache());
        entries.Should().HaveCount(2);
    }
}
