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
    public void ParsePrompts_DropsTaskNotificationOriginInjections()
    {
        var taskNotificationLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>abc</task-id>\n</task-notification>"}}
            """;

        var jsonl = string.Join("\n",
            taskNotificationLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_DropsAnyNonNullOriginKind()
    {
        // Defensive variant: any non-null origin.kind means injected, not user-typed.
        var futureKindLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","origin":{"kind":"some-future-kind"},"message":{"role":"user","content":"injected by some future feature"}}
            """;

        var jsonl = string.Join("\n",
            futureKindLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_DropsCompactSummaryContinuation()
    {
        var compactLine = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","isCompactSummary":true,"isVisibleInTranscriptOnly":true,"message":{"role":"user","content":"This session is being continued from a previous conversation that ran out of context..."}}
            """;

        var jsonl = string.Join("\n",
            compactLine,
            UserLine("u2", "2026-05-01T10:01:00Z", "Real prompt"));

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt");
    }

    [Fact]
    public void ParsePrompts_KeepsPromptWithMissingOriginField()
    {
        // Regression guard: a normal prompt with no origin / no isCompactSummary is kept.
        var jsonl = UserLine("u1", "2026-05-01T10:00:00Z", "Real prompt without origin");

        var result = PromptwallTranscript.ParsePrompts(jsonl, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt without origin");
    }

    [Fact]
    public void ParsePrompts_KeepsPromptWithExplicitOriginNull()
    {
        // Regression guard: literal "origin": null (as Claude Code emits for human prompts)
        // must not trigger the synthetic filter — only non-null origin.kind drops.
        var explicitNullOrigin = """
            {"type":"user","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","origin":null,"message":{"role":"user","content":"Real prompt with explicit null origin"}}
            """;

        var result = PromptwallTranscript.ParsePrompts(explicitNullOrigin, file: "session.jsonl");

        result.Should().ContainSingle();
        result[0].Text.Should().Be("Real prompt with explicit null origin");
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

    // ── ProbeProject / DiscoverProjects ─────────────────────────────────────

    [Fact]
    public void ProbeProject_ReturnsNullWhenDirectoryMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pw-probe-missing-" + Guid.NewGuid());
        var info = PromptwallTranscript.ProbeProject(missing);
        info.Should().BeNull();
    }

    [Fact]
    public void ProbeProject_ReturnsNullWhenNoJsonlFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-probe-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var info = PromptwallTranscript.ProbeProject(dir);
            info.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProbeProject_ExtractsCwdFromFirstJsonlLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-probe-cwd-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var line = """
                {"type":"user","cwd":"/some/path","uuid":"u1","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":"hi"}}
                """;
            File.WriteAllText(Path.Combine(dir, "session.jsonl"), line + "\n");

            var info = PromptwallTranscript.ProbeProject(dir);

            info.Should().NotBeNull();
            info!.Cwd.Should().Be("/some/path");
            info.Dir.Should().Be(dir);
            info.SessionCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ProbeProject_FallsBackToDecodedSlugWhenCwdMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "pw-probe-fallback-" + Guid.NewGuid());
        var dir = Path.Combine(root, "-foo-bar");
        Directory.CreateDirectory(dir);
        try
        {
            // 10 lines with no cwd field anywhere.
            var lines = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                lines.Add("{\"type\":\"user\",\"uuid\":\"u" + i + "\",\"timestamp\":\"2026-05-01T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}");
            }
            File.WriteAllText(Path.Combine(dir, "session.jsonl"), string.Join("\n", lines) + "\n");

            var info = PromptwallTranscript.ProbeProject(dir);

            info.Should().NotBeNull();
            info!.Cwd.Should().Be("/foo/bar");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProbeProject_CountsSessionsAndPicksLatestMtime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-probe-mtime-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var f1 = Path.Combine(dir, "a.jsonl");
            var f2 = Path.Combine(dir, "b.jsonl");
            var f3 = Path.Combine(dir, "c.jsonl");

            var oldLine = """{"type":"user","cwd":"/old","uuid":"u","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":"x"}}""";
            var newLine = """{"type":"user","cwd":"/new","uuid":"u","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":"x"}}""";

            File.WriteAllText(f1, oldLine + "\n");
            File.WriteAllText(f2, oldLine + "\n");
            File.WriteAllText(f3, newLine + "\n");

            var oldMtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var midMtime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            var newMtime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

            File.SetLastWriteTimeUtc(f1, oldMtime);
            File.SetLastWriteTimeUtc(f2, midMtime);
            File.SetLastWriteTimeUtc(f3, newMtime);

            var info = PromptwallTranscript.ProbeProject(dir);

            info.Should().NotBeNull();
            info!.SessionCount.Should().Be(3);
            info.LastActivity.Should().Be(newMtime);
            info.Cwd.Should().Be("/new");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverProjects_OrdersByLastActivityDescending()
    {
        var root = Path.Combine(Path.GetTempPath(), "pw-discover-order-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var dirA = Path.Combine(root, "-a");
            var dirB = Path.Combine(root, "-b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            var lineA = """{"type":"user","cwd":"/a","uuid":"u","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":"x"}}""";
            var lineB = """{"type":"user","cwd":"/b","uuid":"u","timestamp":"2026-05-01T10:00:00Z","message":{"role":"user","content":"x"}}""";

            var fileA = Path.Combine(dirA, "s.jsonl");
            var fileB = Path.Combine(dirB, "s.jsonl");
            File.WriteAllText(fileA, lineA + "\n");
            File.WriteAllText(fileB, lineB + "\n");

            File.SetLastWriteTimeUtc(fileA, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(fileB, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

            var projects = PromptwallTranscript.DiscoverProjects(root);

            projects.Should().HaveCount(2);
            projects[0].Cwd.Should().Be("/b");
            projects[1].Cwd.Should().Be("/a");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoverProjects_ReturnsEmptyWhenClaudeRootMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pw-discover-missing-" + Guid.NewGuid());
        var projects = PromptwallTranscript.DiscoverProjects(missing);
        projects.Should().BeEmpty();
    }

    // ── CollectFromFiles ─────────────────────────────────────────────────────

    [Fact]
    public void CollectFromFiles_StopsAfterReachingTargetCount()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-collect-stop-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // 5 files with 100 prompts each = 500 prompts total.
            var files = new List<string>();
            for (int f = 0; f < 5; f++)
            {
                var path = Path.Combine(dir, $"s{f}.jsonl");
                var lines = new List<string>();
                for (int i = 0; i < 100; i++)
                {
                    var ts = $"2026-05-01T{10 + f:D2}:{i / 60:D2}:{i % 60:D2}Z";
                    lines.Add(UserLine($"u-{f}-{i}", ts, $"prompt {f}-{i}"));
                }
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                files.Add(path);
            }

            var result = PromptwallTranscript.CollectFromFiles(files, targetCount: 10);

            // Must have hit at least 10 candidates and stopped early (not parsed all 5 files).
            result.Count.Should().BeGreaterThanOrEqualTo(10);
            var distinctFiles = result.Select(c => c.File).Distinct().Count();
            distinctFiles.Should().BeLessThan(5,
                "early-exit must skip later files once the target count is reached");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CollectFromFiles_ReadsAllFilesWhenTargetNotReached()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-collect-all-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // 3 files with 5 prompts each = 15 prompts total.
            var files = new List<string>();
            for (int f = 0; f < 3; f++)
            {
                var path = Path.Combine(dir, $"s{f}.jsonl");
                var lines = new List<string>();
                for (int i = 0; i < 5; i++)
                {
                    var ts = $"2026-05-01T{10 + f:D2}:00:{i:D2}Z";
                    lines.Add(UserLine($"u-{f}-{i}", ts, $"prompt {f}-{i}"));
                }
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                files.Add(path);
            }

            var result = PromptwallTranscript.CollectFromFiles(files, targetCount: 100);

            result.Should().HaveCount(15);
            result.Select(c => c.File).Distinct().Should().HaveCount(3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CollectFromFiles_SkipsCorruptFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-collect-corrupt-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var realPath = Path.Combine(dir, "real.jsonl");
            File.WriteAllText(realPath,
                UserLine("u1", "2026-05-01T10:00:00Z", "Real prompt") + "\n");

            var missingPath = Path.Combine(dir, "does-not-exist.jsonl");

            var result = PromptwallTranscript.CollectFromFiles(
                [missingPath, realPath], targetCount: 100);

            result.Should().ContainSingle();
            result[0].Text.Should().Be("Real prompt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CollectFromFiles_PreservesProvidedFileOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw-collect-order-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var fileA = Path.Combine(dir, "a.jsonl");
            var fileB = Path.Combine(dir, "b.jsonl");
            var fileC = Path.Combine(dir, "c.jsonl");

            // Each file holds a single prompt; timestamps deliberately do NOT match
            // the iteration order, to prove CollectFromFiles doesn't reorder.
            File.WriteAllText(fileA, UserLine("uA", "2026-05-03T10:00:00Z", "from-A") + "\n");
            File.WriteAllText(fileB, UserLine("uB", "2026-05-01T10:00:00Z", "from-B") + "\n");
            File.WriteAllText(fileC, UserLine("uC", "2026-05-02T10:00:00Z", "from-C") + "\n");

            var result = PromptwallTranscript.CollectFromFiles(
                [fileC, fileA, fileB], targetCount: 100);

            result.Should().HaveCount(3);
            result.Select(c => c.File).Should().Equal(fileC, fileA, fileB);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
