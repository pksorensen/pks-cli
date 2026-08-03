using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// Golden-file tests: one synthetic fixture per source (Claude transcript JSONL,
/// Codex rollout JSONL, opencode SQLite) asserted against the ASF event stream it
/// must produce, and against the <see cref="AsfSessionProjector"/> rows derived
/// from that stream.
///
/// These exist because ASF is now the *only* ingest path: the local firehoses and
/// the chunks uploaded to agentics.dk are both projections of the same events. A
/// parser that quietly starts emitting one more `usage` event, or stops emitting a
/// `file_op`, moves both the local heartbeat and the published profile graphs at
/// once — and does so silently, because nothing else recomputes those numbers.
/// Pinning the whole stream (not just a couple of counters) is what makes such a
/// change show up as a failing test instead of as a jump in someone's chart.
/// </summary>
public abstract class AsfSourceGoldenTestBase : TestBase
{
    protected static readonly SecretMasker Masker = new();

    /// A well-known credential form, planted in each source's payload so every
    /// parser is proven to mask on the way in — before anything is hashed, stored
    /// or uploaded.
    protected const string PlantedSecret = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";

    protected static async Task<List<AsfEvent>> ReadAllAsync(
        IAgentSessionSource source, DiscoveredAgentSession session)
    {
        var events = new List<AsfEvent>();
        await foreach (var e in source.ReadAsync(session, Masker)) events.Add(e);

        return events;
    }

    /// The event stream reduced to its shape — what a golden file would hold.
    protected static string[] Kinds(IEnumerable<AsfEvent> events) => events.Select(e => e.Kind).ToArray();

    protected static void AssertEnvelopeInvariants(IReadOnlyList<AsfEvent> events, string expectedSrc)
    {
        events.Should().NotBeEmpty();
        events[0].Kind.Should().Be(AsfKind.SessionStart);
        events[^1].Kind.Should().Be(AsfKind.SessionEnd);

        events.Select(e => e.Seq).Should().Equal(Enumerable.Range(0, events.Count),
            "seq must be gap-free and follow call order — ids and therefore dedupe depend on it");
        events.Should().OnlyContain(e => e.Src == expectedSrc);
        events.Should().OnlyContain(e => e.Id != null && e.Id!.StartsWith("e_"));
        events.Select(e => e.Session).Distinct().Should().ContainSingle();
        events.Select(e => e.Project).Distinct().Should().ContainSingle();
        events.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }
}

// ── Claude ────────────────────────────────────────────────────────────────────

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class ClaudeAsfSourceGoldenTests : AsfSourceGoldenTestBase
{
    private const string ProjectSlug = "-workspaces-test-fixture";
    private const string SessionId = "test-session";
    private const string ProjectRoot = "/workspaces/test-fixture";

    /// The 16-line fixture inherited from the retired Claude-only parser tests —
    /// every filter edge case the parser has ever had to handle, in one file.
    private static readonly string[] Lines =
    {
        // 1. Meta-only session line — must NOT produce a prompt or tool.
        """{"type":"permission-mode","permissionMode":"bypassPermissions","sessionId":"test-session"}""",

        // 2. user with STRING content — IS a real prompt. Claude Code emits string
        //    content for harness-injected prompts (plan-resume etc.).
        """{"type":"user","uuid":"U1","timestamp":"2026-01-01T10:00:00Z","sessionId":"test-session","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":"plain string content"}}""",

        // 3. REAL user prompt: array-content with text block, sets cwd + gitBranch.
        """{"type":"user","uuid":"U2","timestamp":"2026-01-01T10:00:01Z","sessionId":"test-session","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"real prompt"}]},"promptId":"p1"}""",

        // 4. assistant with tool_use t1 (Bash) whose command carries a token — the
        //    masking check. usage on the message → 100/50/10/20, model sonnet.
        """{"type":"assistant","uuid":"U3","timestamp":"2026-01-01T10:00:02Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"text","text":"running"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"export GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789 && ls -la"}}],"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":20}}}""",

        // 5. user with tool_result for t1 — closes t1 successfully. NOT a prompt.
        """{"type":"user","uuid":"U4","timestamp":"2026-01-01T10:00:05Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file listing here","is_error":false}]}}""",

        // 6. user with array-content "[Request interrupted" — an interruption, not a prompt.
        """{"type":"user","uuid":"U5","timestamp":"2026-01-01T10:00:06Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",

        // 7. user with isMeta=true — excluded.
        """{"type":"user","uuid":"U6","timestamp":"2026-01-01T10:00:07Z","sessionId":"test-session","isMeta":true,"message":{"role":"user","content":[{"type":"text","text":"<local-command-caveat>"}]}}""",

        // 8. assistant with Edit tool_use → file op pending, path taken from the input.
        """{"type":"assistant","uuid":"U7","timestamp":"2026-01-01T10:00:08Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"file_path":"/foo/bar.cs","old_string":"x","new_string":"y"}}]}}""",

        // 9. user tool_result for t2 with is_error=true → error row + file op ok=false.
        """{"type":"user","uuid":"U8","timestamp":"2026-01-01T10:00:10Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":"error: permission denied"}]}}""",

        // 10. assistant ExitPlanMode → plan event captured, plan body in the input.
        """{"type":"assistant","uuid":"U9","timestamp":"2026-01-01T10:00:11Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-opus-4-7","content":[{"type":"tool_use","id":"t3","name":"ExitPlanMode","input":{"plan":"# my plan\n\nbody here"}}]}}""",

        // 11. assistant with thinking block → thinking event, text never transported.
        """{"type":"assistant","uuid":"U10","timestamp":"2026-01-01T10:00:12Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-opus-4-7","content":[{"type":"thinking","thinking":"deliberating internally"},{"type":"text","text":"answer text"}]}}""",

        // 12. assistant with subagent Agent tool_use.
        """{"type":"assistant","uuid":"U11","timestamp":"2026-01-01T10:00:13Z","sessionId":"test-session","message":{"role":"assistant","content":[{"type":"tool_use","id":"t4","name":"Agent","input":{"subagent_type":"Explore","description":"find symbol","prompt":"do thing"}}]}}""",

        // 13. tool_result closing t4.
        """{"type":"user","uuid":"U12","timestamp":"2026-01-01T10:00:14Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t4","content":"result"}]}}""",

        // 14. assistant with mcp__ tool → tagged isMcp=true.
        """{"type":"assistant","uuid":"U13","timestamp":"2026-01-01T10:00:15Z","sessionId":"test-session","message":{"role":"assistant","content":[{"type":"tool_use","id":"t5","name":"mcp__aspire__doctor","input":{}}]}}""",

        // 15. tool_result closing t5.
        """{"type":"user","uuid":"U14","timestamp":"2026-01-01T10:00:16Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t5","content":"ok"}]}}""",

        // 16. REAL user prompt that is a slash command.
        """{"type":"user","uuid":"U15","timestamp":"2026-01-01T10:00:17Z","sessionId":"test-session","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"/build-banner my-blog.md"}]}}""",
    };

    private async Task<(List<AsfEvent> Events, DiscoveredAgentSession Session)> ReadFixtureAsync()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, SessionId + ".jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', Lines) + "\n", Encoding.UTF8);

        var session = new DiscoveredAgentSession(
            AsfSource.Claude, SessionId, ProjectSlug, ProjectRoot, path,
            new FileInfo(path).LastWriteTimeUtc, new FileInfo(path).Length);

        var source = new ClaudeAsfSource(new BrainPathResolver(dir));

        return (await ReadAllAsync(source, session), session);
    }

    [Fact]
    public async Task Produces_the_expected_event_stream()
    {
        var (events, _) = await ReadFixtureAsync();

        AssertEnvelopeInvariants(events, AsfSource.Claude);
        Kinds(events).Should().Equal(
            AsfKind.SessionStart,
            AsfKind.Prompt,                              // U1, string content
            AsfKind.Prompt,                              // U2
            AsfKind.Assistant, AsfKind.ToolCall, AsfKind.Usage,   // U3
            AsfKind.ToolResult,                          // U4 closes t1
            AsfKind.Prompt,                              // U5, the interruption marker
            AsfKind.ToolCall,                            // U7 Edit
            AsfKind.ToolResult, AsfKind.FileOp,          // U8 closes t2 and edits the file
            AsfKind.ToolCall,                            // U9 ExitPlanMode (never closed)
            AsfKind.Thinking, AsfKind.Assistant,         // U10
            AsfKind.ToolCall, AsfKind.ToolResult,        // U11/U12 Agent
            AsfKind.ToolCall, AsfKind.ToolResult,        // U13/U14 MCP
            AsfKind.Prompt,                              // U15 slash command
            AsfKind.SessionEnd);

        // permission-mode (line 1) and isMeta (U6) contribute nothing at all.
        events.Should().NotContain(e => e.Text != null && e.Text.Contains("local-command-caveat"));
    }

    [Fact]
    public async Task Session_start_and_end_are_synthetic_and_carry_the_context()
    {
        var (events, _) = await ReadFixtureAsync();

        var start = events[0];
        start.Synthetic.Should().BeTrue();
        start.Cwd.Should().Be(ProjectRoot);
        start.GitBranch.Should().Be("main");
        start.Ts.Should().Be(DateTimeOffset.Parse("2026-01-01T10:00:00Z"));

        var end = events[^1];
        end.Synthetic.Should().BeTrue();
        end.Reason.Should().Be("eof");
        end.EndedAt.Should().Be(DateTimeOffset.Parse("2026-01-01T10:00:17Z"));
    }

    [Fact]
    public async Task Only_one_usage_event_even_though_the_message_has_several_blocks()
    {
        var (events, _) = await ReadFixtureAsync();

        // Claude repeats an identical `message.usage` on every content-block line.
        // Emitting one per line overstates tokens by ~72% on a real transcript.
        var usage = events.Should().ContainSingle(e => e.Kind == AsfKind.Usage).Subject;
        usage.Model.Should().Be("claude-sonnet-4-6");
        usage.InputTokens.Should().Be(100);
        usage.OutputTokens.Should().Be(50);
        usage.CacheReadTokens.Should().Be(10);
        usage.CacheWriteTokens.Should().Be(20);
    }

    [Fact]
    public async Task File_op_path_comes_from_the_call_input_not_the_result()
    {
        var (events, _) = await ReadFixtureAsync();

        var op = events.Should().ContainSingle(e => e.Kind == AsfKind.FileOp).Subject;
        op.Op.Should().Be("edit");
        op.Path.Should().Be("/foo/bar.cs");
        op.Ext.Should().Be("cs");
        op.Depth.Should().Be(2);
        op.Ok.Should().BeFalse("the tool_result carried is_error");
    }

    [Fact]
    public async Task Masks_credentials_in_tool_arguments_before_hashing()
    {
        var (events, _) = await ReadFixtureAsync();

        var bash = events.Single(e => e.Kind == AsfKind.ToolCall && e.Tool == "Bash");
        var args = bash.Args!.ToJsonString();
        args.Should().NotContain(PlantedSecret);
        args.Should().Contain(SecretMasker.Mask4);
        bash.ArgsHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Thinking_text_is_never_transported()
    {
        var (events, _) = await ReadFixtureAsync();

        var thinking = events.Should().ContainSingle(e => e.Kind == AsfKind.Thinking).Subject;
        thinking.Text.Should().BeNull();
        thinking.TextLen.Should().Be("deliberating internally".Length);
        thinking.TextHash.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reparsing_yields_identical_ids()
    {
        var (first, _) = await ReadFixtureAsync();
        var (second, _) = await ReadFixtureAsync();

        // Different temp directory, same bytes: nothing about the id may depend on
        // where the file happened to live, or every daily export re-uploads.
        second.Select(e => e.Id).Should().Equal(first.Select(e => e.Id));
    }

    [Fact]
    public async Task Projects_to_the_expected_firehose_rows()
    {
        var (events, session) = await ReadFixtureAsync();
        var parsed = new AsfSessionProjector().Project(session, events);

        parsed.Metadata.SessionId.Should().Be(SessionId);
        parsed.Metadata.ProjectSlug.Should().Be(ProjectSlug);
        parsed.Metadata.LineCount.Should().Be(events.Count, "LineCount counts ASF events, not source lines");
        parsed.Metadata.Cwd.Should().Be(ProjectRoot);
        parsed.Metadata.GitBranches.Should().ContainSingle().Which.Should().Be("main");
        parsed.Metadata.Models.Should().Equal("claude-sonnet-4-6", "claude-opus-4-7");

        parsed.Prompts.Select(p => p.Text).Should().Equal(
            "plain string content", "real prompt", "/build-banner my-blog.md");
        parsed.Prompts[2].IsSlash.Should().BeTrue();
        parsed.Prompts[2].SlashCommand.Should().Be("build-banner");
        parsed.Prompts[2].SlashArgs.Should().Be("my-blog.md");

        parsed.ToolCalls.Should().HaveCount(5);
        parsed.ToolCalls.Single(t => t.ToolUseId == "t1").DurationMs.Should().Be(3000);
        parsed.ToolCalls.Single(t => t.ToolUseId == "t2").IsError.Should().BeTrue();
        parsed.ToolCalls.Single(t => t.ToolUseId == "t3").DurationMs.Should()
            .BeNull("ExitPlanMode was never closed — the call still counts");
        parsed.ToolCalls.Single(t => t.ToolUseId == "t4").SubagentType.Should().Be("Explore");
        parsed.ToolCalls.Single(t => t.ToolUseId == "t5").IsMcp.Should().BeTrue();

        parsed.FileOps.Should().ContainSingle();
        parsed.FileOps[0].Success.Should().BeFalse();

        // Interruptions now land in the errors firehose as their own kind, so they
        // are searchable; ToolErrorCount still counts only real tool failures.
        parsed.Errors.Should().HaveCount(2);
        parsed.Errors.Single(e => e.Kind == "error").Snippet.Should().Contain("permission denied");
        parsed.Errors.Single(e => e.Kind == "interruption").Snippet.Should().StartWith("[Request interrupted");
        parsed.Metadata.ToolErrorCount.Should().Be(1);
        parsed.Metadata.InterruptionCount.Should().Be(1);

        parsed.PlanEvents.Should().ContainSingle();
        parsed.PlanEvents[0].PlanBody.Should().Contain("# my plan");

        parsed.Metadata.ThinkingBlockCount.Should().Be(1);
        parsed.Metadata.SubagentInvocationCount.Should().Be(1);
        parsed.Metadata.FileOpCount.Should().Be(1);

        // One `usage` event is one model response — the honest definition of a turn.
        parsed.Metadata.AssistantTurnCount.Should().Be(1);

        var sonnet = parsed.Metadata.TokensByModel.Single(m => m.Model == "claude-sonnet-4-6");
        sonnet.InputTokens.Should().Be(100);
        sonnet.OutputTokens.Should().Be(50);
        sonnet.CacheReadInputTokens.Should().Be(10);
        sonnet.CacheCreationInputTokens.Should().Be(20);
    }

    [Fact]
    public async Task Empty_transcript_yields_no_events_and_no_throw()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "empty.jsonl");
        await File.WriteAllTextAsync(path, "");

        var session = new DiscoveredAgentSession(
            AsfSource.Claude, "empty", ProjectSlug, ProjectRoot, path, DateTime.UtcNow, 0);
        var events = await ReadAllAsync(new ClaudeAsfSource(new BrainPathResolver(dir)), session);

        events.Should().BeEmpty();

        var parsed = new AsfSessionProjector().Project(session, events);
        parsed.Metadata.PromptCount.Should().Be(0);
        parsed.ToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Discover_walks_the_project_tree_and_reads_cwd_from_the_transcript()
    {
        var home = CreateTempDirectory();
        var projectDir = Path.Combine(home, ".claude", "projects", ProjectSlug);
        Directory.CreateDirectory(Path.Combine(projectDir, SessionId, "subagents"));
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, SessionId + ".jsonl"), string.Join('\n', Lines) + "\n");
        await File.WriteAllTextAsync(
            Path.Combine(projectDir, SessionId, "subagents", "sub-1.jsonl"), Lines[1] + "\n");

        var found = new ClaudeAsfSource(new BrainPathResolver(home)).Discover().ToList();

        found.Should().HaveCount(2, "subagent transcripts live in nested directories and count too");
        found.Should().OnlyContain(d => d.ProjectSlug == ProjectSlug);
        found.Should().OnlyContain(d => d.ProjectRoot == ProjectRoot);
        found.Select(d => d.NativeSessionId).Should().BeEquivalentTo(new[] { SessionId, "sub-1" });
        found[0].CursorKey.Should().StartWith("claude:");

        new ClaudeAsfSource(new BrainPathResolver(home)).Discover("no-such-project").Should().BeEmpty();
    }
}

// ── Codex ─────────────────────────────────────────────────────────────────────

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class CodexAsfSourceGoldenTests : AsfSourceGoldenTestBase
{
    private const string SessionId = "cx-1";
    private const string ProjectRoot = "/workspaces/test-fixture";

    private static readonly string[] Lines =
    {
        """{"timestamp":"2026-01-01T09:00:00.000Z","type":"session_meta","payload":{"id":"rollout-1","session_id":"cx-1","cwd":"/workspaces/test-fixture","originator":"codex_cli_rs","cli_version":"0.146.0","model_provider":"openai"}}""",
        """{"timestamp":"2026-01-01T09:00:01.000Z","type":"turn_context","payload":{"turn_id":"turn-1","model":"gpt-5.5-codex","cwd":"/workspaces/test-fixture"}}""",
        """{"timestamp":"2026-01-01T09:00:02.000Z","type":"event_msg","payload":{"type":"user_message","turn_id":"turn-1","message":"add a test"}}""",
        """{"timestamp":"2026-01-01T09:00:03.000Z","type":"response_item","payload":{"type":"reasoning","summary":[{"type":"summary_text","text":"pondering"}],"internal_chat_message_metadata_passthrough":{"turn_id":"turn-1"}}}""",
        """{"timestamp":"2026-01-01T09:00:04.000Z","type":"response_item","payload":{"type":"function_call","call_id":"c1","name":"shell","arguments":"{\"command\":[\"ls\"]}","internal_chat_message_metadata_passthrough":{"turn_id":"turn-1"}}}""",
        """{"timestamp":"2026-01-01T09:00:06.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"README.md","internal_chat_message_metadata_passthrough":{"turn_id":"turn-1"}}}""",

        // The system prompt and the assistant/user echoes: skipped, or every prompt
        // chart doubles.
        """{"timestamp":"2026-01-01T09:00:06.500Z","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"you are codex"}]}}""",
        """{"timestamp":"2026-01-01T09:00:06.700Z","type":"world_state","payload":{"agents_md":"a full re-dump every turn"}}""",

        """{"timestamp":"2026-01-01T09:00:07.000Z","type":"event_msg","payload":{"type":"patch_apply_end","turn_id":"turn-1","call_id":"c2","success":true,"changes":{"/workspaces/test-fixture/a.cs":{"type":"update","unified_diff":"--- a\n+++ b\n+added line\n-removed line\n"}}}}""",

        // token_count is cumulative and fires on nearly every stream tick — only the
        // last per turn survives, and only as a delta.
        """{"timestamp":"2026-01-01T09:00:08.000Z","type":"event_msg","payload":{"type":"token_count","turn_id":"turn-1","info":{"total_token_usage":{"input_tokens":100,"output_tokens":20,"cached_input_tokens":5,"reasoning_output_tokens":7}}}}""",
        """{"timestamp":"2026-01-01T09:00:09.000Z","type":"event_msg","payload":{"type":"token_count","turn_id":"turn-1","info":{"total_token_usage":{"input_tokens":250,"output_tokens":60,"cached_input_tokens":15,"reasoning_output_tokens":19}}}}""",

        """{"timestamp":"2026-01-01T09:00:10.000Z","type":"event_msg","payload":{"type":"agent_message","turn_id":"turn-1","message":"done"}}""",
        """{"timestamp":"2026-01-01T09:00:11.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-1"}}""",

        // One MCP call becomes both a call and a result: Codex writes no begin event
        // and no response_item twin for these.
        """{"timestamp":"2026-01-01T09:00:13.000Z","type":"event_msg","payload":{"type":"mcp_tool_call_end","turn_id":"turn-2","call_id":"m1","invocation":{"server":"aspire","tool":"doctor","arguments":{"verbose":true,"token":"ghp_abcdefghijklmnopqrstuvwxyz0123456789"}},"duration":{"secs":1,"nanos":500000000},"result":{"Ok":{"content":[{"type":"text","text":"all good"}]}}}}""",

        """{"timestamp":"2026-01-01T09:00:14.000Z","type":"event_msg","payload":{"type":"turn_aborted","turn_id":"turn-2","reason":"interrupted","duration_ms":900}}""",
    };

    private async Task<(List<AsfEvent> Events, DiscoveredAgentSession Session)> ReadFixtureAsync()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "rollout-2026-01-01T09-00-00-cx-1.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', Lines) + "\n", Encoding.UTF8);

        var session = new DiscoveredAgentSession(
            AsfSource.Codex, SessionId, "-workspaces-test-fixture", ProjectRoot, path,
            new FileInfo(path).LastWriteTimeUtc, new FileInfo(path).Length);

        return (await ReadAllAsync(new CodexAsfSource(new BrainPathResolver(dir)), session), session);
    }

    [Fact]
    public async Task Produces_the_expected_event_stream()
    {
        var (events, _) = await ReadFixtureAsync();

        AssertEnvelopeInvariants(events, AsfSource.Codex);
        Kinds(events).Should().Equal(
            AsfKind.SessionStart,
            AsfKind.Prompt,                              // user_message
            AsfKind.Thinking,                            // reasoning
            AsfKind.ToolCall, AsfKind.ToolResult,        // shell c1
            AsfKind.FileOp,                              // patch_apply_end
            AsfKind.Assistant,                           // agent_message
            AsfKind.Usage,                               // flushed by task_complete
            AsfKind.ToolCall, AsfKind.ToolResult,        // mcp_tool_call_end → both halves
            AsfKind.Error,                               // turn_aborted
            AsfKind.SessionEnd);

        events[0].Entrypoint.Should().Be("codex_cli_rs");
        events[0].SrcVersion.Should().Be("0.146.0");
        events[^1].Reason.Should().Be("eof");
        events[^1].Turns.Should().Be(2);
    }

    [Fact]
    public async Task Cumulative_token_counts_collapse_to_one_delta_per_turn()
    {
        var (events, _) = await ReadFixtureAsync();

        // Two cumulative ticks, one usage event carrying the latest total minus what
        // was already emitted — summing usage events must equal the session total,
        // not a triangular number.
        var usage = events.Should().ContainSingle(e => e.Kind == AsfKind.Usage).Subject;
        usage.Model.Should().Be("gpt-5.5-codex");
        usage.InputTokens.Should().Be(250);
        usage.OutputTokens.Should().Be(60);
        usage.CacheReadTokens.Should().Be(15);
        usage.ReasoningTokens.Should().Be(19);
    }

    [Fact]
    public async Task Function_call_arguments_are_parsed_not_kept_as_a_json_string()
    {
        var (events, _) = await ReadFixtureAsync();

        var call = events.Single(e => e.Kind == AsfKind.ToolCall && e.Tool == "shell");
        call.Args.Should().BeOfType<JsonObject>("a JSON string would hash differently on key reorder");
        call.Args!["command"]!.AsArray().Count.Should().Be(1);
        call.TurnId.Should().Be("turn-1");

        var result = events.Single(e => e.Kind == AsfKind.ToolResult && e.CallId == "c1");
        result.Tool.Should().Be("shell");
        result.Output.Should().Be("README.md");
        result.DurationMs.Should().Be(2000);
    }

    [Fact]
    public async Task Patch_apply_becomes_a_file_op_with_diff_line_counts()
    {
        var (events, _) = await ReadFixtureAsync();

        var op = events.Should().ContainSingle(e => e.Kind == AsfKind.FileOp).Subject;
        op.Op.Should().Be("edit");
        op.Path.Should().Be("/workspaces/test-fixture/a.cs");
        op.Ok.Should().BeTrue();
        op.LinesAdded.Should().Be(1);
        op.LinesRemoved.Should().Be(1, "the +++/--- headers are not content lines");
    }

    [Fact]
    public async Task Mcp_call_emits_both_halves_and_masks_its_arguments()
    {
        var (events, _) = await ReadFixtureAsync();

        var call = events.Single(e => e.Kind == AsfKind.ToolCall && e.CallId == "m1");
        call.Tool.Should().Be("mcp__aspire__doctor");
        call.IsMcp.Should().BeTrue();
        call.Args!.ToJsonString().Should().NotContain(PlantedSecret).And.Contain(SecretMasker.Mask4);

        var result = events.Single(e => e.Kind == AsfKind.ToolResult && e.CallId == "m1");
        result.IsMcp.Should().BeTrue();
        result.IsError.Should().BeNull("the payload carried result.Ok");
        result.DurationMs.Should().Be(1500, "Codex durations are {secs, nanos}");
        result.Output.Should().Be("all good");
    }

    [Fact]
    public async Task Turn_aborted_becomes_a_non_fatal_error_carrying_the_reason()
    {
        var (events, _) = await ReadFixtureAsync();

        var error = events.Should().ContainSingle(e => e.Kind == AsfKind.Error).Subject;
        error.ErrorClass.Should().Be("turn_aborted");
        error.Message.Should().Be("interrupted");
        error.DurationMs.Should().Be(900);
        error.Fatal.Should().BeNull();
    }

    [Fact]
    public async Task Projects_to_the_expected_firehose_rows()
    {
        var (events, session) = await ReadFixtureAsync();
        var parsed = new AsfSessionProjector().Project(session, events);

        parsed.Prompts.Should().ContainSingle().Which.Text.Should().Be("add a test");
        parsed.ToolCalls.Should().HaveCount(2);
        parsed.ToolCalls.Should().Contain(t => t.IsMcp);
        parsed.FileOps.Should().ContainSingle();
        parsed.Errors.Should().ContainSingle().Which.Kind.Should().Be("error");
        parsed.Metadata.ThinkingBlockCount.Should().Be(1);
        parsed.Metadata.AssistantTurnCount.Should().Be(1);
        parsed.Metadata.TokensByModel.Should().ContainSingle()
            .Which.InputTokens.Should().Be(250);
    }

    [Fact]
    public async Task Discover_prefers_the_rollouts_own_session_id_over_the_filename()
    {
        var home = CreateTempDirectory();
        var day = Path.Combine(home, ".codex", "sessions", "2026", "01", "01");
        Directory.CreateDirectory(day);
        // Deliberately a filename that does not match the session_id inside: a copied
        // or renamed rollout must not mint a second session.
        await File.WriteAllTextAsync(
            Path.Combine(day, "rollout-renamed-by-hand.jsonl"), string.Join('\n', Lines) + "\n");

        var found = new CodexAsfSource(new BrainPathResolver(home)).Discover().ToList();

        found.Should().ContainSingle();
        found[0].NativeSessionId.Should().Be(SessionId);
        found[0].ProjectRoot.Should().Be(ProjectRoot);
        found[0].CursorKey.Should().Be("codex:cx-1");
    }
}

// ── opencode ──────────────────────────────────────────────────────────────────

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class OpenCodeAsfSourceGoldenTests : AsfSourceGoldenTestBase
{
    private const string SessionId = "ses_1";
    private const string ProjectRoot = "/workspaces/test-fixture";

    /// 2026-01-01T10:00:00Z. opencode stores epoch-**ms**.
    private const long T0 = 1767261600000L;

    private const string SpillMarkerFormat =
        "head line\n... output truncated; full content saved to {0} ...\ntail line";

    /// Builds the three tables the parser joins. Only the columns it reads are
    /// created — the real schema is wider, and pinning all of it here would make
    /// this fixture break on every unrelated opencode migration.
    private (string DbPath, string SpillPath) BuildDatabase()
    {
        var dir = CreateTempDirectory();
        var dbPath = Path.Combine(dir, "opencode.db");
        var livingSpill = Path.Combine(dir, "tool-output", "tool_0000000001");
        var sweptSpill = Path.Combine(dir, "tool-output", "tool_0000000002");
        Directory.CreateDirectory(Path.GetDirectoryName(livingSpill)!);
        File.WriteAllText(livingSpill, "THE FULL 60 KB OUTPUT");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        Exec(conn,
            """
            CREATE TABLE session (
              id TEXT PRIMARY KEY, directory TEXT, version TEXT, agent TEXT, model TEXT,
              cost REAL, time_created INTEGER, time_updated INTEGER, time_archived INTEGER);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, data TEXT);
            CREATE TABLE part (
              id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT,
              time_created INTEGER, data TEXT);
            """);

        Exec(conn,
            """
            INSERT INTO session VALUES (
              'ses_1', '/workspaces/test-fixture', '1.18.11', 'build',
              '{"id":"claude-opus-5","providerID":"anthropic","variant":null}',
              0.42, $created, $updated, NULL)
            """,
            ("$created", T0), ("$updated", T0 + 9000));

        Exec(conn,
            """
            INSERT INTO message VALUES ('msg_u', 'ses_1', '{"role":"user"}');
            INSERT INTO message VALUES ('msg_a', 'ses_1',
              '{"role":"assistant","modelID":"claude-opus-5","providerID":"anthropic","finish":"stop"}');
            """);

        InsertPart(conn, "prt_01", "msg_u", T0, """{"type":"text","text":"hello opencode"}""");
        InsertPart(conn, "prt_02", "msg_a", T0 + 1000, """{"type":"step-start"}""");
        InsertPart(conn, "prt_03", "msg_a", T0 + 2000, new JsonObject
        {
            ["type"] = "reasoning",
            ["text"] = "pondering",
            ["time"] = new JsonObject { ["start"] = T0 + 2000, ["end"] = T0 + 2500 },
        }.ToJsonString());
        InsertPart(conn, "prt_04", "msg_a", T0 + 3000, """{"type":"text","text":"here you go"}""");

        // A completed call whose output was spilled and is still on disk.
        InsertPart(conn, "prt_05", "msg_a", T0 + 4000, ToolPart(
            "call_1", "bash", "completed",
            """{"command":"grep -r foo ."}""",
            string.Format(SpillMarkerFormat, livingSpill),
            T0 + 4000, T0 + 4250));

        // Same, but opencode's hourly job already swept the file (7-day retention).
        InsertPart(conn, "prt_06", "msg_a", T0 + 5000, ToolPart(
            "call_2", "bash", "completed",
            """{"command":"cat big.log"}""",
            string.Format(SpillMarkerFormat, sweptSpill),
            T0 + 5000, T0 + 5100));

        // Interrupted mid-call: the call is real, there is simply no result. Its
        // arguments carry a credential, so this doubles as the masking check.
        InsertPart(conn, "prt_07", "msg_a", T0 + 6000, ToolPart(
            "call_3", "bash", "running",
            $$"""{"command":"gh auth login --with-token {{PlantedSecret}}"}""",
            null, null, null));

        InsertPart(conn, "prt_08", "msg_a", T0 + 7000,
            """{"type":"patch","hash":"abc123","files":["/workspaces/test-fixture/x.ts"]}""");
        InsertPart(conn, "prt_09", "msg_a", T0 + 8000,
            """
            {"type":"step-finish","reason":"stop","cost":0.42,
             "tokens":{"input":10,"output":20,"reasoning":5,"cache":{"read":1,"write":2}}}
            """);

        return (dbPath, livingSpill);
    }

    private static string ToolPart(
        string callId, string tool, string status, string input,
        string? output, long? start, long? end)
    {
        var state = new JsonObject
        {
            ["status"] = status,
            ["input"] = JsonNode.Parse(input),
        };
        if (output is not null) state["output"] = output;
        if (start is not null && end is not null)
            state["time"] = new JsonObject { ["start"] = start, ["end"] = end };

        return new JsonObject
        {
            ["type"] = "tool",
            ["tool"] = tool,
            ["callID"] = callId,
            ["state"] = state,
        }.ToJsonString();
    }

    private static void InsertPart(SqliteConnection conn, string id, string messageId, long ts, string data) =>
        Exec(conn,
            "INSERT INTO part VALUES ($id, $mid, 'ses_1', $ts, $data)",
            ("$id", id), ("$mid", messageId), ("$ts", ts), ("$data", data));

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private async Task<(List<AsfEvent> Events, DiscoveredAgentSession Session)> ReadFixtureAsync()
    {
        var (dbPath, _) = BuildDatabase();

        // Many sessions share one database, so the discovered path carries the
        // session id after a '#'. BackingFile is null for exactly this reason: the
        // blob backup must synthesize a per-session dump instead of copying a
        // multi-megabyte database once per session.
        var session = new DiscoveredAgentSession(
            AsfSource.OpenCode, SessionId, "-workspaces-test-fixture", ProjectRoot,
            $"{dbPath}#{SessionId}", DateTimeOffset.FromUnixTimeMilliseconds(T0 + 9000).UtcDateTime, 9);
        session.BackingFile.Should().BeNull();

        var source = new OpenCodeAsfSource(new BrainPathResolver(CreateTempDirectory()));

        return (await ReadAllAsync(source, session), session);
    }

    [Fact]
    public async Task Produces_the_expected_event_stream()
    {
        var (events, _) = await ReadFixtureAsync();

        AssertEnvelopeInvariants(events, AsfSource.OpenCode);
        Kinds(events).Should().Equal(
            AsfKind.SessionStart,
            AsfKind.Prompt,                              // prt_01
            AsfKind.Thinking,                            // prt_03 (step-start emits nothing)
            AsfKind.Assistant,                           // prt_04
            AsfKind.ToolCall, AsfKind.ToolResult,        // prt_05, spill recovered
            AsfKind.ToolCall, AsfKind.ToolResult,        // prt_06, spill already swept
            AsfKind.ToolCall,                            // prt_07, interrupted — no result
            AsfKind.FileOp,                              // prt_08
            AsfKind.Usage,                               // prt_09
            AsfKind.SessionEnd);

        var start = events[0];
        start.Cwd.Should().Be(ProjectRoot);
        start.Agent.Should().Be("build");
        start.Model.Should().Be("anthropic/claude-opus-5");
        start.SrcVersion.Should().Be("1.18.11");
        start.Ts.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(T0));

        var end = events[^1];
        end.Reason.Should().Be("idle");
        end.Turns.Should().Be(1);
        end.CostUsd.Should().Be(0.42);
    }

    [Fact]
    public async Task Recovers_a_spilled_tool_output_while_it_is_still_on_disk()
    {
        var (events, _) = await ReadFixtureAsync();

        // This is the whole reason the backup job runs daily: opencode's
        // ToolOutputStore deletes tool-output/tool_* after a hardcoded 7 days.
        var recovered = events.Single(e => e.Kind == AsfKind.ToolResult && e.CallId == "call_1");
        recovered.Output.Should().Be("THE FULL 60 KB OUTPUT");
        recovered.Spilled.Should().BeTrue();
        recovered.Truncated.Should().BeTrue();
        recovered.Reason.Should().BeNull();
        recovered.DurationMs.Should().Be(250);
    }

    [Fact]
    public async Task Keeps_the_retained_head_and_tail_when_the_spill_was_already_swept()
    {
        var (events, _) = await ReadFixtureAsync();

        var expired = events.Single(e => e.Kind == AsfKind.ToolResult && e.CallId == "call_2");
        expired.Spilled.Should().BeTrue();
        expired.Reason.Should().Be("spill-expired");
        expired.Output.Should().StartWith("head line", "a short output beats a missing event");

        // The retained marker names the swept file by absolute path. It must not
        // survive: it would publish a local path, and because the id hashes the full
        // output it would also make the id machine-dependent.
        expired.Output.Should().NotContain("tool-output").And.NotContain("/tool_");
        expired.Output.Should().Contain("spill file expired");
    }

    [Fact]
    public async Task An_interrupted_call_still_counts_and_its_arguments_are_masked()
    {
        var (events, _) = await ReadFixtureAsync();

        var call = events.Single(e => e.Kind == AsfKind.ToolCall && e.CallId == "call_3");
        events.Should().NotContain(e => e.Kind == AsfKind.ToolResult && e.CallId == "call_3");
        call.Args!.ToJsonString().Should().NotContain(PlantedSecret).And.Contain(SecretMasker.Mask4);
    }

    [Fact]
    public async Task Usage_carries_the_step_finish_totals()
    {
        var (events, _) = await ReadFixtureAsync();

        var usage = events.Should().ContainSingle(e => e.Kind == AsfKind.Usage).Subject;
        usage.Model.Should().Be("anthropic/claude-opus-5");
        usage.InputTokens.Should().Be(10);
        usage.OutputTokens.Should().Be(20);
        usage.ReasoningTokens.Should().Be(5);
        usage.CacheReadTokens.Should().Be(1);
        usage.CacheWriteTokens.Should().Be(2);
        usage.CostUsd.Should().Be(0.42);
    }

    [Fact]
    public async Task Reparsing_yields_identical_ids()
    {
        var (first, _) = await ReadFixtureAsync();
        var (second, _) = await ReadFixtureAsync();

        // A different database file each time — nothing in the id may depend on
        // SQLite's rowids or on where the database happened to live.
        second.Select(e => e.Id).Should().Equal(first.Select(e => e.Id));
    }

    [Fact]
    public async Task Projects_to_the_expected_firehose_rows()
    {
        var (events, session) = await ReadFixtureAsync();
        var parsed = new AsfSessionProjector().Project(session, events);

        parsed.Prompts.Should().ContainSingle().Which.Text.Should().Be("hello opencode");
        parsed.ToolCalls.Should().HaveCount(3, "the interrupted call counts too");
        parsed.FileOps.Should().ContainSingle().Which.FilePath.Should().Be("/workspaces/test-fixture/x.ts");
        parsed.Errors.Should().BeEmpty();
        parsed.Metadata.ThinkingBlockCount.Should().Be(1);
        parsed.Metadata.AssistantTurnCount.Should().Be(1);
        parsed.Metadata.Cwd.Should().Be(ProjectRoot);
        parsed.Metadata.TokensByModel.Should().ContainSingle()
            .Which.Model.Should().Be("anthropic/claude-opus-5");
    }
}
