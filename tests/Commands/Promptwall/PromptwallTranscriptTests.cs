using FluentAssertions;
using PKS.Commands.Promptwall;
using Xunit;

namespace PKS.CLI.Tests.Commands.Promptwall;

[Trait("Category", "Promptwall")]
public class PromptwallTranscriptTests
{
    // ── ParsePrompts ─────────────────────────────────────────────────────────

    [Fact]
    public void ParsePrompts_ReturnsOnlyUserPrompts()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Real prompt one"),
            AssistantLine("a1", "2026-05-01T10:00:05Z", "Reply one"),
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt two"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().HaveCount(2);
        result.Select(p => p.Text).Should().BeEquivalentTo(["Real prompt one", "Real prompt two"]);
    }

    [Fact]
    public void ParsePrompts_FiltersToolResults()
    {
        // user line whose message.content is an array of tool_result blocks (not a string).
        var toolResultLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"file contents"}]}}
            """;

        var jsonl = string.Join("\n",
            toolResultLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_FiltersSidechain()
    {
        var sidechainLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","isSidechain":true,"message":{"role":"user","content":"Sub-agent prompt"}}
            """;

        var jsonl = string.Join("\n",
            sidechainLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_FiltersMeta()
    {
        var metaLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","isMeta":true,"message":{"role":"user","content":"Meta marker"}}
            """;

        var jsonl = string.Join("\n",
            metaLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_FiltersCommandEchoes()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "<command-name>/foo</command-name><command-args></command-args>"),
            UserLine("u2", "2026-05-01T10:00:05Z", "<local-command-stdout>some output</local-command-stdout>"),
            UserLine("u3", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_StripsTrailingSystemReminder()
    {
        var raw = "Tell me about cats\n<system-reminder>\nNote about behavior\n</system-reminder>";
        var jsonl = UserLine("u1", "2026-05-01T10:00:00Z", raw);

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Tell me about cats");
    }

    [Fact]
    public void ParsePrompts_FiltersPromptThatIsOnlySystemReminder()
    {
        var raw = "<system-reminder>\nharness only\n</system-reminder>";
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", raw),
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_StripsCommandMessageWrapper()
    {
        var raw = "<command-message>summon assistant</command-message><command-args>arg1</command-args>actual prompt body";
        var jsonl = UserLine("u1", "2026-05-01T10:00:00Z", raw);

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("actual prompt body");
    }

    [Fact]
    public void ParsePrompts_OrdersByTimestampDescending()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "First"),
            UserLine("u2", "2026-05-01T11:00:00Z", "Second"),
            UserLine("u3", "2026-05-01T12:00:00Z", "Third"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Select(p => p.Text).Should().Equal("Third", "Second", "First");
    }

    [Fact]
    public void ParsePrompts_SkipsCorruptLines()
    {
        var jsonl = string.Join("\n",
            "this is not json",
            "",
            UserLine("u1", "2026-05-01T10:00:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_PreservesFileAndUuid()
    {
        var jsonl = UserLine("u-abc", "2026-05-01T10:00:00Z", "Hi");

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].File.Should().Be("session.jsonl");
        result[0].Uuid.Should().Be("u-abc");
    }

    // ── ExtractReply ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractReply_ReturnsAssistantTextAfterPrompt()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Question"),
            AssistantLine("a1", "2026-05-01T10:00:05Z", "Answer one"),
            AssistantLine("a2", "2026-05-01T10:00:10Z", "Answer two", stopReason: "end_turn"));

        var reply = PromptwallTranscript.ExtractReply(jsonl, "u1");

        reply.Should().Be("Answer one\n\nAnswer two");
    }

    [Fact]
    public void ExtractReply_StopsAtNextUserPrompt()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Question one"),
            AssistantLine("a1", "2026-05-01T10:00:05Z", "Reply to one"),
            UserLine("u2", "2026-05-01T10:00:10Z", "Question two"),
            AssistantLine("a2", "2026-05-01T10:00:15Z", "Reply to two", stopReason: "end_turn"));

        var reply = PromptwallTranscript.ExtractReply(jsonl, "u1");

        reply.Should().Be("Reply to one");
    }

    [Fact]
    public void ExtractReply_StopsAtEndTurn()
    {
        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Q"),
            AssistantLine("a1", "2026-05-01T10:00:05Z", "Final reply", stopReason: "end_turn"),
            AssistantLine("a2", "2026-05-01T10:00:10Z", "Should not be included"));

        var reply = PromptwallTranscript.ExtractReply(jsonl, "u1");

        reply.Should().Be("Final reply");
    }

    [Fact]
    public void ExtractReply_FiltersThinkingAndToolUseBlocks()
    {
        // assistant line with mixed content blocks: thinking + tool_use + text.
        var assistantMixed = """
            {"type":"assistant","uuid":"a1","parentUuid":"u1","timestamp":"2026-05-01T10:00:05Z","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"thinking","thinking":"hidden reasoning"},{"type":"tool_use","id":"tu1","name":"Read","input":{}},{"type":"text","text":"Visible reply"}]}}
            """;

        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Q"),
            assistantMixed);

        var reply = PromptwallTranscript.ExtractReply(jsonl, "u1");

        reply.Should().Be("Visible reply");
    }

    [Fact]
    public void ExtractReply_ReturnsNullWhenNoTextBlocks()
    {
        // assistant only emitted a tool_use, no text.
        var assistantToolOnly = """
            {"type":"assistant","uuid":"a1","parentUuid":"u1","timestamp":"2026-05-01T10:00:05Z","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"tu1","name":"Read","input":{}}]}}
            """;

        var jsonl = string.Join("\n",
            UserLine("u1", "2026-05-01T10:00:00Z", "Q"),
            assistantToolOnly);

        var reply = PromptwallTranscript.ExtractReply(jsonl, "u1");

        reply.Should().BeNull();
    }

    [Fact]
    public void ExtractReply_ReturnsNullWhenPromptNotFound()
    {
        var jsonl = UserLine("u1", "2026-05-01T10:00:00Z", "Q");
        var reply = PromptwallTranscript.ExtractReply(jsonl, "no-such-uuid");
        reply.Should().BeNull();
    }

    // ── BuildImagePrompts ────────────────────────────────────────────────────

    [Fact]
    public void BuildImagePrompts_OneImage_WhenPromptOnly()
    {
        var images = PromptwallTranscript.BuildImagePrompts(
            promptText: "Hello",
            replyText: null,
            combinedThreshold: 600);

        images.Should().ContainSingle();
        images[0].Label.Should().Be("PROMPT");
        images[0].ImagePrompt.Should().Contain("Hello");
        images[0].ImagePrompt.Should().Contain("PROMPT");
    }

    [Fact]
    public void BuildImagePrompts_OneImage_WhenCombinedShort()
    {
        var images = PromptwallTranscript.BuildImagePrompts(
            promptText: "short prompt",
            replyText: "short reply",
            combinedThreshold: 600);

        images.Should().ContainSingle();
        images[0].Label.Should().Be("PROMPT");
        images[0].ImagePrompt.Should().Contain("short prompt");
        images[0].ImagePrompt.Should().Contain("short reply");
    }

    [Fact]
    public void BuildImagePrompts_TwoImages_WhenCombinedExceedsThreshold()
    {
        var bigPrompt = new string('a', 350);
        var bigReply = new string('b', 350);

        var images = PromptwallTranscript.BuildImagePrompts(
            promptText: bigPrompt,
            replyText: bigReply,
            combinedThreshold: 600);

        images.Should().HaveCount(2);
        images[0].Label.Should().Be("PROMPT");
        images[0].ImagePrompt.Should().Contain(bigPrompt);
        images[1].Label.Should().Be("REPLY");
        images[1].ImagePrompt.Should().Contain(bigReply);
    }

    [Fact]
    public void BuildImagePrompts_TwoImages_WhenEitherSideExceedsIndividualCap()
    {
        var bigPrompt = new string('a', 450);
        var smallReply = "short reply";

        var images = PromptwallTranscript.BuildImagePrompts(
            promptText: bigPrompt,
            replyText: smallReply,
            combinedThreshold: 600);

        // even though combined < 600, prompt alone > 400 forces two images.
        images.Should().HaveCount(2);
    }

    [Fact]
    public void BuildImagePrompts_TruncatesOversizedText()
    {
        var huge = new string('x', 1500);

        var images = PromptwallTranscript.BuildImagePrompts(
            promptText: huge,
            replyText: null,
            combinedThreshold: 600);

        images.Should().ContainSingle();
        // hard cap 1200 chars + ellipsis
        images[0].ImagePrompt.Should().Contain("…");
        images[0].ImagePrompt.Should().NotContain(huge);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string UserLine(string uuid, string ts, string content)
    {
        // Use System.Text.Json to safely encode the content string.
        var encoded = System.Text.Json.JsonSerializer.Serialize(content);
        return "{\"type\":\"user\",\"uuid\":\"" + uuid +
               "\",\"timestamp\":\"" + ts +
               "\",\"message\":{\"role\":\"user\",\"content\":" + encoded + "}}";
    }

    private static string AssistantLine(string uuid, string ts, string text, string? stopReason = null)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(text);
        var stop = stopReason is null ? "" : ",\"stop_reason\":\"" + stopReason + "\"";
        return "{\"type\":\"assistant\",\"uuid\":\"" + uuid +
               "\",\"parentUuid\":\"u\",\"timestamp\":\"" + ts +
               "\",\"message\":{\"role\":\"assistant\"" + stop +
               ",\"content\":[{\"type\":\"text\",\"text\":" + encoded + "}]}}";
    }
}
