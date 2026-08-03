using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using PKS.Infrastructure.Services.Brain.Asf;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// The ASF invariants from docs/specs/asf/ (agentic-live-www workspace):
/// canonical JSON, always-on masking, monotone redaction, and level-independent
/// event ids. These four are what make dedupe, level upgrades and honest
/// statistics work; if any of them regresses, uploaded history silently starts
/// double-counting, so they are asserted mechanically rather than by review.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AsfCanonicalJsonTests
{
    [Fact]
    public void SortsKeys_AndStripsWhitespace()
    {
        var node = JsonNode.Parse("""{ "b" : 1 , "a" : 2 }""");

        CanonicalJson.Serialize(node).Should().Be("""{"a":2,"b":1}""");
    }

    [Theory]
    // Spec test vectors — docs/specs/asf/03-chunks-and-hashing.md.
    // Hand-verifiable: printf '%s' '{"a":2,"b":1}' | sha256sum
    [InlineData("""{"b":1,"a":2}""", "d3626ac30a87e6f7a6428233b3c68299976865fa5508e4267c5415c76af7a772")]
    [InlineData("""{ "a" : "æ/b" }""", "9984208f501cac6cbc80d4ecf9ae08cfc2b043f27a8718c331e352db3a381f91")]
    public void MatchesSpecTestVectors(string input, string expectedSha256)
    {
        CanonicalJson.Sha256Hex(JsonNode.Parse(input)).Should().Be(expectedSha256);
    }

    [Fact]
    public void OmitsAbsentMembers()
    {
        // Third spec vector: an absent member must not reach the wire at all,
        // which is the same rule that makes redacted fields omitted-not-null.
        var node = new JsonObject { ["a"] = 1, ["b"] = null };
        node.Remove("b");

        CanonicalJson.Sha256Hex(node).Should()
            .Be("015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862");
    }

    [Fact]
    public void DoesNotEscapeSlashOrNonAscii()
    {
        var node = JsonNode.Parse("""{"p":"/workspaces/æøå & <x>"}""");

        CanonicalJson.Serialize(node).Should().Be("""{"p":"/workspaces/æøå & <x>"}""");
    }

    [Fact]
    public void SortsNestedKeysToo()
    {
        var node = JsonNode.Parse("""{"z":{"b":1,"a":[{"d":1,"c":2}]}}""");

        CanonicalJson.Serialize(node).Should().Be("""{"z":{"a":[{"c":2,"d":1}],"b":1}}""");
    }

    [Fact]
    public void PreservesArrayOrder()
    {
        var node = JsonNode.Parse("""{"a":[3,1,2]}""");

        CanonicalJson.Serialize(node).Should().Be("""{"a":[3,1,2]}""");
    }

    [Fact]
    public void ProducesIdenticalBytesForParsedAndConstructedValues()
    {
        // A parser building JsonValue from CLR ints and a reader building it from
        // JsonElement must hash the same, or two clients disagree on every id.
        var parsed = JsonNode.Parse("""{"n":42,"s":"x","b":true}""");
        var constructed = new JsonObject { ["n"] = 42, ["s"] = "x", ["b"] = true };

        CanonicalJson.Serialize(constructed).Should().Be(CanonicalJson.Serialize(parsed));
    }
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class SecretMaskerTests
{
    private static readonly SecretMasker Masker = new();

    [Theory]
    [InlineData("key is sk-ant-api03-abcdefghijklmnopqrstuvwxyz012345 ok")]
    [InlineData("export ANTHROPIC_API_KEY=sk-ant-secretvalue")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmno")]
    [InlineData("glpat-abcdefghijklmnopqrst")]
    [InlineData("gsk_abcdefghijklmnopqrstuvwx")]
    [InlineData("rnt_0123456789abcdef0123456789abcdef")]
    [InlineData("bkt_0123456789abcdef0123456789abcdef")]
    [InlineData("DATABASE_URL=postgres://user:pw@host/db")]
    [InlineData("token: abcdefghijklmnopqrstuvwxyz")]
    public void MasksKnownCredentialForms(string input)
    {
        var outcome = Masker.MaskWithCount(input);

        outcome.Text.Should().Contain(SecretMasker.Mask4);
        outcome.Hits.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MasksPemPrivateKeyBlocks()
    {
        var input = "-----BEGIN RSA PRIVATE KEY-----\nMIIEow…\nabc\n-----END RSA PRIVATE KEY-----";

        Masker.Mask(input).Should().Be(SecretMasker.Mask4);
    }

    [Fact]
    public void LeavesOrdinaryTextAlone()
    {
        const string input = "Read src/lib/masking.ts and explain the 40 patterns";

        var outcome = Masker.MaskWithCount(input);

        outcome.Text.Should().Be(input);
        outcome.Hits.Should().Be(0);
    }

    [Fact]
    public void LeavesGitShasAndBase64ImagesAlone()
    {
        // The entropy detector is context-gated precisely so these survive — an
        // unconditional rule would gut `full` exports for no security gain.
        const string input =
            "commit 9f2c4a1b8e7d6c5f4a3b2c1d0e9f8a7b6c5d4e3f data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ";

        Masker.Mask(input).Should().Be(input);
    }

    [Fact]
    public void MasksHighEntropyValuesInCredentialContext()
    {
        const string input = "MY_APP_SECRET=Zq7X2vP9kL4mN8bR3tY6wA1sD5fG0hJ2";

        var outcome = Masker.MaskWithCount(input);

        outcome.Text.Should().NotContain("Zq7X2vP9");
        outcome.Hits.Should().Be(1);
    }

    [Fact]
    public void MasksCredentialNamedJsonMembersWholesale()
    {
        var args = JsonNode.Parse("""{"url":"https://x/y","password":"hunter2","nested":{"client_secret":"s"}}""");

        var (masked, hits) = Masker.MaskJsonWithCount(args);

        masked!["password"]!.GetValue<string>().Should().Be(SecretMasker.Mask4);
        masked["nested"]!["client_secret"]!.GetValue<string>().Should().Be(SecretMasker.Mask4);
        masked["url"]!.GetValue<string>().Should().Be("https://x/y");
        hits.Should().Be(2);
    }

    [Fact]
    public void MasksStringLeavesInsideJson()
    {
        var args = JsonNode.Parse("""{"command":"curl -H 'Authorization: Bearer eyJhbGciOiJIUzI1NiJ9abcdef'"}""");

        var masked = Masker.MaskJson(args);

        masked!["command"]!.GetValue<string>().Should().Contain(SecretMasker.Mask4);
    }

    [Fact]
    public void HonorsProjectLocalPatternFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asf-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "mask-patterns.txt");
        File.WriteAllText(file, "# customer key format\nACME-[0-9]{6}\n\n");

        try
        {
            var masker = new SecretMasker(file);

            masker.Mask("id ACME-123456 here").Should().Be($"id {SecretMasker.Mask4} here");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SkipsMalformedPatternsInsteadOfFailingTheExport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asf-mask-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "mask-patterns.txt");
        File.WriteAllText(file, "([unclosed\nACME-[0-9]{6}\n");

        try
        {
            var masker = new SecretMasker(file);

            masker.Mask("ACME-123456").Should().Be(SecretMasker.Mask4);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AsfLevelProjectionTests
{
    private static AsfEventBuilder NewBuilder() =>
        new(new SecretMasker(), AsfSource.Claude, "2.1.7", "sess-1", "/workspaces/agentic-live-www");

    private static AsfEvent ToolCall(AsfEventBuilder b)
    {
        var e = b.Begin(AsfKind.ToolCall, DateTimeOffset.Parse("2026-08-03T08:12:44.019Z"));
        e.Tool = "Bash";
        e.CallId = "toolu_1";
        b.SetArgs(e, JsonNode.Parse("""{"command":"gh pr create --title x"}"""));
        b.SetContext(e, "/workspaces/agentic-live-www", "main");

        return b.Seal(e);
    }

    [Fact]
    public void RedactedFieldsAreOmittedNotNull()
    {
        var full = ToolCall(NewBuilder());

        var prompts = AsfLevelProjector.Project(full, AsfLevel.Prompts);
        var json = CanonicalJson.Serialize(prompts);

        json.Should().NotContain("\"args\"");
        json.Should().NotContain("null");
        // The call itself is still fully countable at the reduced level.
        json.Should().Contain("\"tool\":\"Bash\"");
        json.Should().Contain("\"argsHash\"");
        json.Should().Contain("\"argsBytes\"");
    }

    [Fact]
    public void RedactionIsMonotone_FieldsPresentLowerAreByteIdenticalHigher()
    {
        var b = NewBuilder();
        var events = new List<AsfEvent> { ToolCall(b) };

        var start = b.Begin(AsfKind.SessionStart, DateTimeOffset.Parse("2026-08-03T08:00:00Z"));
        b.SetContext(start, "/workspaces/agentic-live-www", "main");
        start.ProjectPath = "/workspaces/agentic-live-www";
        events.Add(b.Seal(start));

        var prompt = b.Begin(AsfKind.Prompt, DateTimeOffset.Parse("2026-08-03T08:01:00Z"));
        b.SetText(prompt, "hello world");
        events.Add(b.Seal(prompt));

        foreach (var full in events)
        {
            var levels = new[] { AsfLevel.Metrics, AsfLevel.Prompts, AsfLevel.Full }
                .Select(l => AsObject(AsfLevelProjector.Project(full, l)))
                .ToArray();

            for (var lower = 0; lower < levels.Length - 1; lower++)
            {
                foreach (var (key, value) in levels[lower])
                {
                    if (key == "level") continue; // the one field that must differ
                    for (var higher = lower + 1; higher < levels.Length; higher++)
                    {
                        levels[higher].Should().ContainKey(key,
                            "a field present at a lower level must exist at every higher level");
                        levels[higher][key].Should().Be(value,
                            $"'{key}' must be byte-identical across levels");
                    }
                }
            }
        }
    }

    [Fact]
    public void EventIdIsLevelIndependent()
    {
        var full = ToolCall(NewBuilder());

        var metrics = AsfLevelProjector.Project(full, AsfLevel.Metrics);
        var prompts = AsfLevelProjector.Project(full, AsfLevel.Prompts);

        metrics.Id.Should().Be(full.Id);
        prompts.Id.Should().Be(full.Id);
        // Same session exported twice at different levels enriches rather than
        // duplicating — the whole reason totals survive a level change.
        AsfLevelProjector.Enriches(AsfLevel.Metrics, AsfLevel.Full).Should().BeTrue();
        AsfLevelProjector.Enriches(AsfLevel.Full, AsfLevel.Metrics).Should().BeFalse();
    }

    [Fact]
    public void MetricsKeepsCountableFieldsAndDropsAllContent()
    {
        var b = NewBuilder();
        var e = b.Begin(AsfKind.Prompt, DateTimeOffset.Parse("2026-08-03T08:01:00Z"));
        b.SetText(e, "refactor the masking module");
        b.SetContext(e, "/workspaces/agentic-live-www", "main");
        var full = b.Seal(e);

        var metrics = AsfLevelProjector.Project(full, AsfLevel.Metrics);

        metrics.Text.Should().BeNull();
        metrics.Cwd.Should().BeNull();
        metrics.GitBranch.Should().BeNull();
        metrics.TextLen.Should().Be(full.TextLen);
        metrics.TextHash.Should().Be(full.TextHash);
        metrics.CwdHash.Should().Be(full.CwdHash);
        metrics.GitBranchHash.Should().Be(full.GitBranchHash);
    }

    [Fact]
    public void ThinkingTextIsNeverTransportedAtAnyLevel()
    {
        var b = NewBuilder();
        var e = b.Begin(AsfKind.Thinking, DateTimeOffset.Parse("2026-08-03T08:02:00Z"));
        b.SetThinking(e, "long internal reasoning");
        var full = b.Seal(e);

        full.Text.Should().BeNull();
        full.TextLen.Should().Be("long internal reasoning".Length);
        AsfLevelProjector.Project(full, AsfLevel.Full).Text.Should().BeNull();
    }

    [Fact]
    public void SealRejectsEventsMissingDerivedFields()
    {
        var b = NewBuilder();
        var e = b.Begin(AsfKind.Prompt, DateTimeOffset.UtcNow);
        e.Text = "assigned directly, bypassing SetText";

        var seal = () => b.Seal(e);

        seal.Should().Throw<InvalidOperationException>()
            .WithMessage("*TextHash*");
    }

    [Fact]
    public void HashesAreTakenOverTheMaskedValue()
    {
        // A hash must never confirm a guess at an unmasked secret.
        var b = NewBuilder();
        var e = b.Begin(AsfKind.Prompt, DateTimeOffset.UtcNow);
        b.SetText(e, "key ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        b.Seal(e);

        e.Text.Should().NotContain("ghp_");
        e.TextHash.Should().Be(CanonicalJson.Sha256HexOfString(e.Text!));
    }

    [Fact]
    public void ExportIsDeterministic_SameSourceYieldsSameIds()
    {
        var first = ToolCall(NewBuilder());
        var second = ToolCall(NewBuilder());

        second.Id.Should().Be(first.Id);
        CanonicalJson.Serialize(second).Should().Be(CanonicalJson.Serialize(first));
    }

    [Fact]
    public void SeqIsGapFreeAndFromCallOrderNotWallClock()
    {
        var b = NewBuilder();
        // Deliberately out-of-order timestamps: seq must follow call order.
        var a = b.Seal(b.Begin(AsfKind.SessionStart, DateTimeOffset.Parse("2026-08-03T09:00:00Z")));
        var c = b.Seal(b.Begin(AsfKind.SessionEnd, DateTimeOffset.Parse("2026-08-03T08:00:00Z")));

        a.Seq.Should().Be(0);
        c.Seq.Should().Be(1);
        b.Count.Should().Be(2);
    }

    [Fact]
    public void SessionAndProjectHandlesAreOpaqueAndStable()
    {
        var handle = AsfEventId.SessionHandle(AsfSource.Codex, "019fbcaf-1234");

        handle.Should().StartWith("s_").And.HaveLength(34);
        handle.Should().NotContain("019fbcaf");
        AsfEventId.SessionHandle(AsfSource.Codex, "019fbcaf-1234").Should().Be(handle);
        // Same native id from a different tool is a different session.
        AsfEventId.SessionHandle(AsfSource.Claude, "019fbcaf-1234").Should().NotBe(handle);
    }

    private static Dictionary<string, string> AsObject(AsfEvent e)
    {
        var node = (JsonObject)JsonNode.Parse(CanonicalJson.Serialize(e))!;

        return node.ToDictionary(p => p.Key, p => CanonicalJson.Serialize(p.Value));
    }
}

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AsfLevelParsingTests
{
    [Theory]
    [InlineData("all", AsfLevel.Full)]
    [InlineData("full", AsfLevel.Full)]
    [InlineData("Prompts-Only", AsfLevel.Prompts)]
    [InlineData("prompts", AsfLevel.Prompts)]
    [InlineData("metrics", AsfLevel.Metrics)]
    [InlineData("stats", AsfLevel.Metrics)]
    public void ParsesCliSpellings(string input, string expected)
        => AsfLevel.Parse(input).Should().Be(expected);

    [Fact]
    public void DefaultsToMetricsWhenUnset()
        => AsfLevel.Parse(null).Should().Be(AsfLevel.Metrics);

    [Fact]
    public void RejectsUnknownLevels()
        => new Action(() => AsfLevel.Parse("everything")).Should().Throw<ArgumentException>();

    [Fact]
    public void RanksLevelsForTokenScopeChecks()
    {
        AsfLevel.AtLeast(AsfLevel.Full, AsfLevel.Prompts).Should().BeTrue();
        AsfLevel.AtLeast(AsfLevel.Prompts, AsfLevel.Full).Should().BeFalse();
        AsfLevel.AtLeast("bogus", AsfLevel.Metrics).Should().BeFalse();
    }
}
