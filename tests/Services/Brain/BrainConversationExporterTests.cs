using System.Text;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

public class BrainConversationExporterTests : TestBase
{
    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Export_keeps_human_and_assistant_text_and_references_omitted_blocks()
    {
        var dir = CreateTempDirectory();
        var source = Path.Combine(dir, "session-1.jsonl");
        var output = Path.Combine(dir, "conversation.md");
        var lines = new[]
        {
            """{"type":"user","uuid":"u1","timestamp":"2026-07-16T10:00:00Z","origin":{"kind":"human"},"message":{"role":"user","content":"Can remote MCP onboard a user?"}}""",
            """{"type":"assistant","timestamp":"2026-07-16T10:00:01Z","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"Let me verify the platform behavior."},{"type":"tool_use","id":"t1","name":"WebSearch","input":{"query":"remote MCP"}}]}}""",
            """{"type":"user","timestamp":"2026-07-16T10:00:02Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"a very expensive result"}]}}""",
            """{"type":"assistant","timestamp":"2026-07-16T10:00:03Z","message":{"role":"assistant","stop_reason":"end_turn","content":[{"type":"thinking","thinking":"private reasoning"},{"type":"text","text":"Yes, but authentication differs by host."}]}}""",
            """{"type":"user","uuid":"u2","timestamp":"2026-07-16T10:00:04Z","message":{"role":"user","content":"What did we learn?"}}""",
        };
        await File.WriteAllTextAsync(source, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        var exporter = new BrainConversationExporter();
        var result = await exporter.ExportAsync(new BrainConversationExportOptions
        {
            SourcePath = source,
            OutputPath = output,
        });

        var markdown = await File.ReadAllTextAsync(output);
        markdown.Should().Contain("Can remote MCP onboard a user?");
        markdown.Should().NotContain("Let me verify the platform behavior.");
        markdown.Should().Contain("Yes, but authentication differs by host.");
        markdown.Should().Contain("What did we learn?");
        markdown.Should().NotContain("a very expensive result");
        markdown.Should().NotContain("private reasoning");
        markdown.Should().Contain("Omitted tool call `WebSearch`");
        markdown.Should().Contain("Omitted intermediate assistant text");
        markdown.Should().Contain("Omitted tool result `t1`");
        markdown.Should().Contain("raw L2, bytes ");
        markdown.Should().Contain("<!-- raw L1, bytes 0-");
        markdown.Should().Contain("source_sha256:");
        result.HumanMessages.Should().Be(2);
        result.AssistantTextBlocks.Should().Be(1);
        result.OmittedBlocks.Should().Be(4);
    }

    [Fact, Trait("Category", "Unit"), Trait("Speed", "Fast")]
    public async Task Export_filters_synthetic_user_events_and_truncates_large_visible_text()
    {
        var dir = CreateTempDirectory();
        var source = Path.Combine(dir, "session-2.jsonl");
        var output = Path.Combine(dir, "conversation.md");
        var lines = new[]
        {
            """{"type":"user","timestamp":"2026-07-16T10:00:00Z","isCompactSummary":true,"message":{"role":"user","content":"synthetic compact summary"}}""",
            """{"type":"user","timestamp":"2026-07-16T10:00:01Z","origin":{"kind":"task-notification"},"message":{"role":"user","content":"synthetic task result"}}""",
            """{"type":"user","timestamp":"2026-07-16T10:00:02Z","message":{"role":"user","content":"1234567890"}}""",
        };
        await File.WriteAllTextAsync(source, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        var exporter = new BrainConversationExporter();
        await exporter.ExportAsync(new BrainConversationExportOptions
        {
            SourcePath = source,
            OutputPath = output,
            MaxVisibleCharsPerBlock = 5,
        });

        var markdown = await File.ReadAllTextAsync(output);
        markdown.Should().NotContain("synthetic compact summary");
        markdown.Should().NotContain("synthetic task result");
        markdown.Should().Contain("12345");
        markdown.Should().NotContain("123456");
        markdown.Should().Contain("Omitted synthetic event");
        markdown.Should().Contain("Omitted 5 trailing characters");
    }
}
