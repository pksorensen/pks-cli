using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// The two halves of "don't ingest it twice".
///
/// A session can genuinely be found in more than one place once rescued docker
/// volumes are discoverable: the host has it because the repo is bind-mounted,
/// and the container's config volume has it because that is where the agent
/// wrote it. Provenance has to be recorded (so the copies can be told apart
/// later) *without* becoming part of identity (so they still collapse to one
/// event) — and one copy has to be dropped before anything reads it, because the
/// local firehoses are appended to and nothing downstream would notice.
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class AsfOriginAndDedupeTests
{
    // ---- origin is provenance, not identity --------------------------------

    [Fact]
    public void Origin_does_not_change_the_event_id()
    {
        var host = SampleEvent();
        var rescued = SampleEvent();
        rescued.Origin = "docker:claude-code-config-abc123";

        AsfEventId.Compute(rescued).Should().Be(
            AsfEventId.Compute(host),
            "the same session read from two places is one event, not two");
    }

    [Fact]
    public void Origin_does_not_change_the_content_hash()
    {
        var host = SampleEvent();
        var rescued = SampleEvent();
        rescued.Origin = "docker:vol";

        AsfEventId.ContentHash(rescued).Should().Be(AsfEventId.ContentHash(host));
    }

    [Fact]
    public void Two_different_origins_still_agree_on_the_id()
    {
        var a = SampleEvent();
        a.Origin = "docker:vol-a";
        var b = SampleEvent();
        b.Origin = "docker:vol-b";

        AsfEventId.Compute(a).Should().Be(AsfEventId.Compute(b));
    }

    [Fact]
    public void Origin_survives_every_level()
    {
        var full = SampleEvent().WithId();
        full.Origin = "docker:vol";

        foreach (var level in new[] { AsfLevel.Full, AsfLevel.Prompts, AsfLevel.Metrics })
        {
            AsfLevelProjector.Project(full, level).Origin.Should().Be(
                "docker:vol",
                "splitting metrics by where they came from has to work at the level " +
                "someone actually uploads at");
        }
    }

    [Fact]
    public void Builder_stamps_origin_on_every_event()
    {
        var builder = new AsfEventBuilder(
            new SecretMasker(), AsfSource.Claude, "1.0.0", "sess-1", "/workspaces/repo", "docker:vol");

        var start = builder.Seal(builder.Begin(AsfKind.SessionStart, DateTimeOffset.UnixEpoch));
        var prompt = builder.Seal(builder.Begin(AsfKind.Prompt, DateTimeOffset.UnixEpoch));

        start.Origin.Should().Be("docker:vol");
        prompt.Origin.Should().Be("docker:vol");
    }

    [Fact]
    public void Builder_without_origin_leaves_it_absent()
    {
        var builder = new AsfEventBuilder(
            new SecretMasker(), AsfSource.Claude, "1.0.0", "sess-1", "/workspaces/repo");

        builder.Seal(builder.Begin(AsfKind.Prompt, DateTimeOffset.UnixEpoch)).Origin.Should().BeNull();
    }

    // ---- one copy per session ----------------------------------------------

    [Fact]
    public void Dedupe_keeps_the_copy_with_more_history()
    {
        var host = Session("claude", "s1", "/home/me/.claude/projects/x/s1.jsonl", bytes: 1000);
        var volume = Session("claude", "s1", "/bulk/vol/projects/x/s1.jsonl", bytes: 4000, origin: "docker:vol");

        var kept = AgentSessionDedupe.OnePerSession(
            [(Src(), host), (Src(), volume)], out var dropped);

        dropped.Should().Be(1);
        kept.Should().ContainSingle().Which.Session.SourcePath.Should().Be("/bulk/vol/projects/x/s1.jsonl");
    }

    [Fact]
    public void Dedupe_prefers_the_host_copy_when_the_copies_are_identical()
    {
        var host = Session("claude", "s1", "/home/me/.claude/projects/x/s1.jsonl", bytes: 1000);
        var volume = Session("claude", "s1", "/bulk/vol/projects/x/s1.jsonl", bytes: 1000, origin: "docker:vol");

        // Registration order must not decide the outcome, or a chunk hash would
        // depend on which root happened to be enumerated first.
        foreach (var order in new[]
                 {
                     new[] { (Src(), host), (Src(), volume) },
                     [(Src(), volume), (Src(), host)],
                 })
        {
            var kept = AgentSessionDedupe.OnePerSession(order, out _);
            kept.Should().ContainSingle().Which.Session.Origin.Should().BeNull();
        }
    }

    [Fact]
    public void Dedupe_leaves_different_sessions_alone()
    {
        var a = Session("claude", "s1", "/a.jsonl", bytes: 10);
        var b = Session("claude", "s2", "/b.jsonl", bytes: 10);
        var c = Session("codex", "s1", "/c.jsonl", bytes: 10);

        var kept = AgentSessionDedupe.OnePerSession(
            [(Src(), a), (Src(), b), (Src(), c)], out var dropped);

        dropped.Should().Be(0);
        kept.Should().HaveCount(3, "the cursor key is source-scoped, so claude:s1 and codex:s1 are two sessions");
    }

    [Fact]
    public void Dedupe_preserves_discovery_order()
    {
        var a = Session("claude", "s1", "/a.jsonl", bytes: 10);
        var b = Session("claude", "s2", "/b.jsonl", bytes: 10);
        var bAgain = Session("claude", "s2", "/bulk/b.jsonl", bytes: 99, origin: "docker:vol");
        var c = Session("claude", "s3", "/c.jsonl", bytes: 10);

        var kept = AgentSessionDedupe.OnePerSession(
            [(Src(), a), (Src(), b), (Src(), bAgain), (Src(), c)], out _);

        kept.Select(k => k.Session.NativeSessionId).Should().Equal("s1", "s2", "s3");
        kept[1].Session.Origin.Should().Be("docker:vol", "the richer copy replaces the incumbent in place");
    }

    // ---- discovery -----------------------------------------------------------

    [Fact]
    public void Discovery_skips_the_workflow_journal_but_keeps_subagent_transcripts()
    {
        var home = Directory.CreateTempSubdirectory("pks-claude-").FullName;
        try
        {
            var project = Path.Combine(home, ".claude", "projects", "-workspaces-repo");
            var session = Path.Combine(project, "1111");
            Directory.CreateDirectory(Path.Combine(session, "subagents"));
            Directory.CreateDirectory(Path.Combine(session, "subagents", "workflows", "wf_a"));
            Directory.CreateDirectory(Path.Combine(session, "subagents", "workflows", "wf_b"));

            File.WriteAllText(Path.Combine(project, "1111.jsonl"), "{\"cwd\":\"/workspaces/repo\"}\n");
            File.WriteAllText(Path.Combine(session, "subagents", "agent-7f3.jsonl"),
                "{\"cwd\":\"/workspaces/repo\"}\n");
            foreach (var wf in new[] { "wf_a", "wf_b" })
                File.WriteAllText(
                    Path.Combine(session, "subagents", "workflows", wf, "journal.jsonl"),
                    "{\"type\":\"started\",\"key\":\"v2:abc\",\"agentId\":\"a088\"}\n");

            var found = new ClaudeAsfSource(new BrainPathResolver(home)).Discover().ToList();

            found.Select(f => f.NativeSessionId).Should().BeEquivalentTo(
                ["1111", "agent-7f3"],
                "a workflow ledger is not a transcript, and both of them would answer " +
                "to the id \"journal\" — which is how one of them would silently disappear");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Resumed_codex_rollouts_are_separate_sessions()
    {
        var home = Directory.CreateTempSubdirectory("pks-codex-").FullName;
        try
        {
            var day = Path.Combine(home, ".codex", "sessions", "2026", "07", "11");
            Directory.CreateDirectory(day);

            // Resuming a thread opens a new rollout that keeps the original
            // session_id and holds only the new turns.
            foreach (var (rollout, thread) in new[] { ("aaa", "aaa"), ("bbb", "aaa"), ("ccc", "aaa") })
            {
                File.WriteAllText(
                    Path.Combine(day, $"rollout-2026-07-11T15-52-22-{rollout}.jsonl"),
                    "{\"type\":\"session_meta\",\"payload\":{" +
                    $"\"id\":\"{rollout}\",\"session_id\":\"{thread}\",\"cwd\":\"/workspaces/repo\"}}}}\n");
            }

            var found = new CodexAsfSource(new BrainPathResolver(home)).Discover().ToList();
            var kept = AgentSessionDedupe.OnePerSession(
                found.Select(f => (Src(), f)).ToList(), out var dropped);

            dropped.Should().Be(0);
            kept.Should().HaveCount(3, "three runs of one thread are three files of history, not one file copied thrice");
            found.Select(f => f.NativeSessionId).Should().BeEquivalentTo(["aaa", "bbb", "ccc"]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ---- registry ------------------------------------------------------------

    [Fact]
    public void Registry_skips_an_unreachable_root_without_forgetting_it()
    {
        var home = Directory.CreateTempSubdirectory("pks-roots-").FullName;
        try
        {
            var registry = new BrainRootRegistry(new BrainPathResolver(home));

            var present = Path.Combine(home, "present");
            Directory.CreateDirectory(Path.Combine(present, "projects"));
            var gone = Path.Combine(home, "gone");
            var emptied = Path.Combine(home, "emptied");
            Directory.CreateDirectory(emptied);

            registry.AddRange([
                new BrainSessionRoot(present, "docker:a", DateTimeOffset.UnixEpoch),
                new BrainSessionRoot(gone, "docker:b", DateTimeOffset.UnixEpoch),
                new BrainSessionRoot(emptied, "docker:c", DateTimeOffset.UnixEpoch),
            ]);

            var reloaded = new BrainRootRegistry(new BrainPathResolver(home));

            reloaded.All().Should().HaveCount(3, "a mount that is down today comes back tomorrow");
            reloaded.Usable().Should().ContainSingle().Which.Origin.Should().Be(
                "docker:a",
                "an empty directory is what a vanished bind mount leaves behind, so it is not usable either");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Registry_re_registration_keeps_the_original_added_date()
    {
        var home = Directory.CreateTempSubdirectory("pks-roots-").FullName;
        try
        {
            var registry = new BrainRootRegistry(new BrainPathResolver(home));
            var root = Path.Combine(home, "vol");
            Directory.CreateDirectory(root);

            var first = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
            registry.Add(new BrainSessionRoot(root, "docker:vol", first)).Should().BeTrue();
            registry.Add(new BrainSessionRoot(root, "docker:vol", first.AddDays(30))).Should().BeFalse();

            registry.All().Should().ContainSingle().Which.AddedUtc.Should().Be(first);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ---- helpers -------------------------------------------------------------

    private static AsfEvent SampleEvent() => new()
    {
        V = 1,
        Seq = 7,
        Ts = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero),
        Src = AsfSource.Claude,
        SrcVersion = "1.2.3",
        Session = AsfEventId.SessionHandle(AsfSource.Claude, "sess-1"),
        Project = AsfEventId.ProjectHandle("/workspaces/repo"),
        Kind = AsfKind.Prompt,
        Level = AsfLevel.Full,
        Text = "hello",
        TextLen = 5,
        TextHash = AsfEventId.OpaqueHash("hello"),
    };

    private static DiscoveredAgentSession Session(
        string kind, string id, string path, long bytes, string? origin = null) =>
        new(kind, id, "-workspaces-repo", "/workspaces/repo", path,
            new DateTime(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc), bytes)
        {
            Origin = origin,
        };

    /// The dedupe helper only ever reads the session, never the source, so any
    /// instance will do.
    private static IAgentSessionSource Src() => new ClaudeAsfSource(new BrainPathResolver("/nonexistent"));
}
