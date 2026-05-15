using System.Text;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// Drives the SessionParser TDD. Each filter edge case from
/// src/apps/www-site/src/lib/sync-parser.ts:62-213 is covered by a fact below
/// against a single 16-line synthetic JSONL fixture.
public class SessionParserTests : TestBase
{
    private const string ProjectSlug = "-workspaces-test-fixture";
    private const string SessionId = "test-session";

    private static string FixturePath(string testDir) =>
        Path.Combine(testDir, SessionId + ".jsonl");

    private static async Task WriteFixtureAsync(string dir)
    {
        // 16 lines covering every edge case the parser must handle.
        // See SessionParserTests at top of this file for the case-by-case map.
        var lines = new[]
        {
            // 1. Meta-only session line — must NOT produce a prompt or tool.
            """{"type":"permission-mode","permissionMode":"bypassPermissions","sessionId":"test-session"}""",

            // 2. user with STRING content — IS a real prompt. Claude Code emits string
            //    content for harness-injected prompts (plan-resume etc.). The original
            //    sync-parser.ts:125 filter was too aggressive for our data.
            """{"type":"user","uuid":"U1","timestamp":"2026-01-01T10:00:00Z","sessionId":"test-session","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":"plain string content"}}""",

            // 3. REAL user prompt: array-content with text block, sets cwd + gitBranch.
            """{"type":"user","uuid":"U2","timestamp":"2026-01-01T10:00:01Z","sessionId":"test-session","cwd":"/workspaces/test-fixture","gitBranch":"main","message":{"role":"user","content":[{"type":"text","text":"real prompt"}]},"promptId":"p1"}""",

            // 4. assistant with tool_use t1 (Bash). usage on message → 100/50 tokens, model claude-sonnet-4-6.
            """{"type":"assistant","uuid":"U3","timestamp":"2026-01-01T10:00:02Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"text","text":"running"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}],"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":20}}}""",

            // 5. user with tool_result for t1 — closes t1 successfully. NOT a prompt.
            """{"type":"user","uuid":"U4","timestamp":"2026-01-01T10:00:05Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"file listing here","is_error":false}]}}""",

            // 6. user with array-content "[Request interrupted" — excluded per sync-parser.ts:147.
            """{"type":"user","uuid":"U5","timestamp":"2026-01-01T10:00:06Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",

            // 7. user with isMeta=true — excluded.
            """{"type":"user","uuid":"U6","timestamp":"2026-01-01T10:00:07Z","sessionId":"test-session","isMeta":true,"message":{"role":"user","content":[{"type":"text","text":"<local-command-caveat>"}]}}""",

            // 8. assistant with Edit tool_use → file op pending.
            """{"type":"assistant","uuid":"U7","timestamp":"2026-01-01T10:00:08Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-sonnet-4-6","content":[{"type":"tool_use","id":"t2","name":"Edit","input":{"file_path":"/foo/bar.cs","old_string":"x","new_string":"y"}}]}}""",

            // 9. user tool_result for t2 with is_error=true → error row + file op success=false.
            """{"type":"user","uuid":"U8","timestamp":"2026-01-01T10:00:10Z","sessionId":"test-session","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","is_error":true,"content":"error: permission denied"}]}}""",

            // 10. assistant ExitPlanMode → plan event captured, plan body in toolInput.plan.
            """{"type":"assistant","uuid":"U9","timestamp":"2026-01-01T10:00:11Z","sessionId":"test-session","message":{"role":"assistant","model":"claude-opus-4-7","content":[{"type":"tool_use","id":"t3","name":"ExitPlanMode","input":{"plan":"# my plan\n\nbody here"}}]}}""",

            // 11. assistant with thinking block → ThinkingBlockCount++.
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
        await File.WriteAllTextAsync(FixturePath(dir), string.Join('\n', lines) + "\n", Encoding.UTF8);
    }

    private async Task<ParsedSession> ParseFixtureAsync()
    {
        var dir = CreateTempDirectory();
        await WriteFixtureAsync(dir);
        var parser = new SessionParser();
        return await parser.ParseAsync(FixturePath(dir), ProjectSlug);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Metadata_basics_populated()
    {
        var s = await ParseFixtureAsync();
        s.Metadata.SessionId.Should().Be(SessionId);
        s.Metadata.ProjectSlug.Should().Be(ProjectSlug);
        s.Metadata.LineCount.Should().Be(16);
        s.Metadata.SourceBytes.Should().BeGreaterThan(0);
        s.Metadata.FirstTimestampUtc.Should().Be(DateTime.Parse("2026-01-01T10:00:00Z").ToUniversalTime());
        s.Metadata.LastTimestampUtc.Should().Be(DateTime.Parse("2026-01-01T10:00:17Z").ToUniversalTime());
        s.Metadata.Cwd.Should().Be("/workspaces/test-fixture");
        s.Metadata.GitBranches.Should().ContainSingle().Which.Should().Be("main");
        s.Metadata.Models.Should().BeEquivalentTo(new[] { "claude-sonnet-4-6", "claude-opus-4-7" });
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Prompts_capture_string_array_text_and_slash_kinds()
    {
        var s = await ParseFixtureAsync();
        // U1 (string), U2 (array text), U15 (slash) — interrupted (U5), isMeta (U6), tool_result envelopes excluded.
        s.Prompts.Should().HaveCount(3);
        s.Metadata.PromptCount.Should().Be(3);
        s.Prompts[0].Text.Should().Be("plain string content");
        s.Prompts[0].IsSlash.Should().BeFalse();
        s.Prompts[1].Text.Should().Be("real prompt");
        s.Prompts[2].Text.Should().Be("/build-banner my-blog.md");
        s.Prompts[2].IsSlash.Should().BeTrue();
        s.Prompts[2].SlashCommand.Should().Be("build-banner");
        s.Prompts[2].SlashArgs.Should().Be("my-blog.md");
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Tool_calls_emitted_and_matched_with_tool_results()
    {
        var s = await ParseFixtureAsync();
        s.ToolCalls.Should().HaveCount(5);
        s.Metadata.ToolCallCount.Should().Be(5);

        var t1 = s.ToolCalls.Single(t => t.ToolUseId == "t1");
        t1.ToolName.Should().Be("Bash");
        t1.DurationMs.Should().Be(3000);
        t1.IsError.Should().BeFalse();
        t1.IsMcp.Should().BeFalse();
        t1.IsSubagent.Should().BeFalse();

        var t2 = s.ToolCalls.Single(t => t.ToolUseId == "t2");
        t2.DurationMs.Should().Be(2000);
        t2.IsError.Should().BeTrue();

        var t3 = s.ToolCalls.Single(t => t.ToolUseId == "t3");
        t3.ToolName.Should().Be("ExitPlanMode");
        t3.DurationMs.Should().BeNull();        // unmatched — that's fine
        t3.IsError.Should().BeFalse();

        var t4 = s.ToolCalls.Single(t => t.ToolUseId == "t4");
        t4.ToolName.Should().Be("Agent");
        t4.IsSubagent.Should().BeTrue();
        t4.SubagentType.Should().Be("Explore");

        var t5 = s.ToolCalls.Single(t => t.ToolUseId == "t5");
        t5.IsMcp.Should().BeTrue();
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task File_ops_extracted_from_edit_tool_use()
    {
        var s = await ParseFixtureAsync();
        s.FileOps.Should().ContainSingle();
        var op = s.FileOps[0];
        op.Op.Should().Be("edit");
        op.FilePath.Should().Be("/foo/bar.cs");
        op.Success.Should().BeFalse();        // t2 ended with is_error=true
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Errors_recorded_when_tool_result_is_error()
    {
        var s = await ParseFixtureAsync();
        s.Errors.Should().ContainSingle();
        s.Errors[0].ToolName.Should().Be("Edit");
        s.Errors[0].ToolUseId.Should().Be("t2");
        s.Errors[0].Kind.Should().Be("error");
        s.Errors[0].Snippet.Should().Contain("permission denied");
        s.Metadata.ToolErrorCount.Should().Be(1);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Plan_events_capture_ExitPlanMode_with_body()
    {
        var s = await ParseFixtureAsync();
        s.PlanEvents.Should().ContainSingle();
        s.PlanEvents[0].ToolUseId.Should().Be("t3");
        s.PlanEvents[0].PlanBody.Should().Contain("# my plan");
        s.PlanEvents[0].PlanHash.Should().NotBeNullOrWhiteSpace();
        s.Metadata.PlanEventCount.Should().Be(1);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Counters_for_thinking_subagent_interruption_assistant_turns()
    {
        var s = await ParseFixtureAsync();
        s.Metadata.ThinkingBlockCount.Should().Be(1);
        s.Metadata.SubagentInvocationCount.Should().Be(1);
        s.Metadata.InterruptionCount.Should().Be(1);          // U5 array-text with [Request interrupted
        s.Metadata.AssistantTurnCount.Should().Be(6);         // U3 U7 U9 U10 U11 U13
        s.Metadata.FileOpCount.Should().Be(1);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Token_totals_grouped_by_model()
    {
        var s = await ParseFixtureAsync();
        var sonnet = s.Metadata.TokensByModel.Single(m => m.Model == "claude-sonnet-4-6");
        sonnet.InputTokens.Should().Be(100);
        sonnet.OutputTokens.Should().Be(50);
        sonnet.CacheReadInputTokens.Should().Be(10);
        sonnet.CacheCreationInputTokens.Should().Be(20);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Empty_file_returns_zero_counts_and_no_throw()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "empty.jsonl");
        await File.WriteAllTextAsync(path, "");
        var parser = new SessionParser();
        var s = await parser.ParseAsync(path, ProjectSlug);
        s.Metadata.LineCount.Should().Be(0);
        s.Metadata.PromptCount.Should().Be(0);
        s.Prompts.Should().BeEmpty();
        s.ToolCalls.Should().BeEmpty();
    }

    [Theory, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    [InlineData("/workspaces/agentic-live-www/foo/bar.cs", false, null)]
    [InlineData("/home/node/.claude", false, null)]
    [InlineData("/loop 5m /foo", true, "loop")]
    [InlineData("/build-banner my-blog.md", true, "build-banner")]
    [InlineData("/init", true, "init")]
    [InlineData("not a slash command", false, null)]
    [InlineData("/ Note: started with slash + space", false, null)]
    public async Task Slash_command_parser_distinguishes_paths_from_commands(string text, bool expectedSlash, string? expectedCommand)
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "slash.jsonl");
        var textJson = System.Text.Json.JsonSerializer.Serialize(text);
        var line = "{\"type\":\"user\",\"uuid\":\"U1\",\"timestamp\":\"2026-01-01T10:00:00Z\",\"sessionId\":\"s\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" + textJson + "}]}}";
        await File.WriteAllTextAsync(path, line);
        var parser = new SessionParser();
        var s = await parser.ParseAsync(path, ProjectSlug);
        s.Prompts.Should().ContainSingle();
        s.Prompts[0].IsSlash.Should().Be(expectedSlash);
        s.Prompts[0].SlashCommand.Should().Be(expectedCommand);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Malformed_json_lines_are_skipped_silently()
    {
        var dir = CreateTempDirectory();
        var path = Path.Combine(dir, "mixed.jsonl");
        await File.WriteAllTextAsync(path, string.Join('\n', new[]
        {
            "not valid json",
            """{"type":"user","uuid":"U1","timestamp":"2026-01-01T10:00:00Z","sessionId":"s","message":{"role":"user","content":[{"type":"text","text":"survivor"}]}}""",
            "",
            "  ",
        }));
        var parser = new SessionParser();
        var s = await parser.ParseAsync(path, ProjectSlug);
        s.Prompts.Should().ContainSingle().Which.Text.Should().Be("survivor");
    }
}
