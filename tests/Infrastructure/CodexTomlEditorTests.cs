using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Codex;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Guards <see cref="CodexTomlEditor"/>, the by-name editor for <c>~/.codex/config.toml</c>.
///
/// <para>
/// <b>These tests stand in for Codex Desktop.</b> A devcontainer cannot start it, so the failure
/// they exist to catch cannot be caught by hand here: a writer that rebuilt the file deleted
/// <c>[desktop]</c> and every <c>[plugins.*]</c> table, the app silently refused to launch, and
/// both <c>codex doctor</c> and <c>codex app-server</c> reported success. It took a reinstall to
/// find. So the assertions are deliberately about foreign content surviving byte for byte, not
/// about our own tables being written correctly.
/// </para>
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class CodexTomlEditorTests
{
    /// <summary>
    /// A realistic co-owned config: the reader's own keys, Codex Desktop's table, a trusted
    /// project, a marketplace and a plugin written by <c>codex plugin</c>, the reader's own MCP
    /// server, and one stale table of ours from an earlier connect.
    /// </summary>
    private const string Fixture = """
        # my codex config, hands off
        model = "gpt-5.6-sol"
        model_reasoning_effort = "high"

        [desktop]
        theme = "dark"
        window_width = 1280

        [projects."/workspaces/example"]
        trust_level = "trusted"

        [marketplaces.official]
        last_updated = "2026-08-21T07:29:36Z"
        source_type = "git"
        source = "https://skills.example/official.git"

        [plugins."tsql-analyzer@official"]
        enabled = true

        [mcp_servers.github]
        command = "npx"
        args = ["-y", "@modelcontextprotocol/server-github"]

        [mcp_servers.agentics-project]
        url = "https://stale.example/p/old/mcp"
        """;

    private static void AssertForeignContentIntact(string result)
    {
        result.Should().Contain("# my codex config, hands off");
        result.Should().Contain("[desktop]").And.Contain("theme = \"dark\"").And.Contain("window_width = 1280");
        result.Should().Contain("[projects.\"/workspaces/example\"]").And.Contain("trust_level = \"trusted\"");
        result.Should().Contain("[marketplaces.official]").And.Contain("source = \"https://skills.example/official.git\"");
        result.Should().Contain("[plugins.\"tsql-analyzer@official\"]");
        result.Should().Contain("[mcp_servers.github]").And.Contain("@modelcontextprotocol/server-github");
    }

    [Fact]
    public void Apply_ReplacesOwnedTable_AndLeavesEveryForeignTableIntact()
    {
        var afterRemoval = CodexTomlEditor.RemoveTable(Fixture, "mcp_servers.agentics-project");
        var result = CodexTomlEditor.AppendTable(afterRemoval, """
            [mcp_servers.agentics-project]
            url = "https://agentics.dk/p/poul/my-project/mcp"
            """);

        AssertForeignContentIntact(result);
        result.Should().NotContain("https://stale.example/p/old/mcp");
        result.Should().Contain("url = \"https://agentics.dk/p/poul/my-project/mcp\"");

        // Exactly one of ours, not two.
        CountOccurrences(result, "[mcp_servers.agentics-project]").Should().Be(1);
    }

    [Fact]
    public void Apply_Twice_IsAByteForByteNoOp()
    {
        static string Apply(string input)
        {
            var next = CodexTomlEditor.RemoveTable(input, "mcp_servers.agentics-project");
            next = CodexTomlEditor.AppendTable(next, """
                [mcp_servers.agentics-project]
                url = "https://agentics.dk/p/poul/my-project/mcp"
                """);

            return CodexTomlEditor.SetTopLevelKey(next, "model_reasoning_effort", "\"medium\"");
        }

        var once = Apply(Fixture);
        var twice = Apply(once);

        twice.Should().Be(once, "a re-run must not accumulate blank lines or duplicate tables");
    }

    [Fact]
    public void RemoveTable_TakesSubTablesWithIt_ButNotASiblingWithASharedPrefix()
    {
        const string toml = """
            [mcp_servers.agentics-project]
            url = "https://example.test/mcp"

            [mcp_servers.agentics-project.env]
            TOKEN = "x"

            [mcp_servers.agentics-project-two]
            url = "https://other.test/mcp"
            """;

        var result = CodexTomlEditor.RemoveTable(toml, "mcp_servers.agentics-project");

        result.Should().NotContain("[mcp_servers.agentics-project]");
        result.Should().NotContain("[mcp_servers.agentics-project.env]");
        result.Should().NotContain("TOKEN");
        result.Should().Contain("[mcp_servers.agentics-project-two]", "a prefix is not a parent");
        result.Should().Contain("https://other.test/mcp");
    }

    [Fact]
    public void SetTopLevelKey_ReplacesInPlace_KeepingPosition()
    {
        var result = CodexTomlEditor.SetTopLevelKey(Fixture, "model", "\"gpt-5.6-terra\"");

        result.Should().Contain("model = \"gpt-5.6-terra\"");
        result.Should().NotContain("gpt-5.6-sol");
        AssertForeignContentIntact(result);

        // Still above the first table, and still the first key line.
        result.IndexOf("model = \"gpt-5.6-terra\"", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("[desktop]", StringComparison.Ordinal));
    }

    [Fact]
    public void SetTopLevelKey_WhenAbsent_InsertsAtTheTop_NotAfterATable()
    {
        const string toml = """
            [desktop]
            theme = "dark"
            """;

        var result = CodexTomlEditor.SetTopLevelKey(toml, "model", "\"gpt-5.6-sol\"");

        // The whole point: a bare key after a table header would become [desktop].model.
        result.IndexOf("model = \"gpt-5.6-sol\"", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("[desktop]", StringComparison.Ordinal));
        result.Should().Contain("theme = \"dark\"");
    }

    [Fact]
    public void SetTopLevelKey_DoesNotMatchASameNamedKeyInsideATable()
    {
        const string toml = """
            [model_providers.other]
            model = "not-the-top-level-one"
            """;

        var result = CodexTomlEditor.SetTopLevelKey(toml, "model", "\"mine\"");

        result.Should().Contain("model = \"not-the-top-level-one\"", "a key inside a table is not top-level");
        CountOccurrences(result, "model = ").Should().Be(2);
        result.IndexOf("model = \"mine\"", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("[model_providers.other]", StringComparison.Ordinal));
    }

    [Fact]
    public void GetTopLevelKey_ReadsOnlyTheTopLevelOne()
    {
        CodexTomlEditor.GetTopLevelKey(Fixture, "model").Should().Be("\"gpt-5.6-sol\"");
        CodexTomlEditor.GetTopLevelKey(Fixture, "theme").Should().BeNull("theme belongs to [desktop]");
        CodexTomlEditor.GetTopLevelKey(Fixture, "nope").Should().BeNull();
    }

    // ---- the two repairs ----

    [Fact]
    public void RemoveLegacyProfileSelector_StripsBothFormsThatBrickTheConfig()
    {
        // Measured on codex-cli 0.163.0: either of these is a hard config-load error, and their
        // presence also makes `-p` fail for EVERY profile, not just the one named.
        const string toml = """
            model = "gpt-5.6-sol"
            profile = "chosen"

            [profiles.chosen]
            model = "something"

            [desktop]
            theme = "dark"
            """;

        var result = CodexTomlEditor.RemoveLegacyProfileSelector(toml, out var removed);

        removed.Should().BeTrue();
        result.Should().NotContain("profile = \"chosen\"");
        result.Should().NotContain("[profiles.chosen]");
        result.Should().Contain("model = \"gpt-5.6-sol\"");
        result.Should().Contain("[desktop]").And.Contain("theme = \"dark\"");
    }

    [Fact]
    public void RemoveLegacyProfileSelector_OnACleanFile_ReportsNoChange()
    {
        var result = CodexTomlEditor.RemoveLegacyProfileSelector(Fixture, out var removed);

        removed.Should().BeFalse();
        result.Should().Be(Fixture);
    }

    [Fact]
    public void RemoveLegacyManagedBlock_DropsOurOldBlock_ButRescuesTablesCodexMovedIntoIt()
    {
        // Codex rewrites the TOML structurally and moves our end marker BELOW tables it appends
        // later, so foreign tables end up inside the region. They are not ours to delete.
        var toml = $"""
            model = "gpt-5.6-sol"

            {CodexCliConfig.BeginMarker}
            {CodexCliConfig.BuildProxyProviderBlock(8788)}

            [marketplaces.official]
            source = "https://skills.example/official.git"

            [plugins."tsql-analyzer@official"]
            enabled = true
            {CodexCliConfig.EndMarker}

            [desktop]
            theme = "dark"
            """;

        var result = CodexTomlEditor.RemoveLegacyManagedBlock(toml, out var removed);

        removed.Should().BeTrue();
        result.Should().NotContain(CodexCliConfig.BeginMarker);
        result.Should().NotContain(CodexCliConfig.EndMarker);
        result.Should().NotContain($"[model_providers.{CodexCliConfig.ProviderName}]");
        result.Should().NotContain("127.0.0.1:8788");

        result.Should().Contain("model = \"gpt-5.6-sol\"");
        result.Should().Contain("[marketplaces.official]", "Codex moved this in; it is not ours");
        result.Should().Contain("source = \"https://skills.example/official.git\"");
        result.Should().Contain("[plugins.\"tsql-analyzer@official\"]");
        result.Should().Contain("[desktop]").And.Contain("theme = \"dark\"");
    }

    [Fact]
    public void RemoveLegacyManagedBlock_WhenThereIsNone_ChangesNothing()
    {
        var result = CodexTomlEditor.RemoveLegacyManagedBlock(Fixture, out var removed);

        removed.Should().BeFalse();
        result.Should().Be(Fixture);
    }

    [Fact]
    public void HasTable_MatchesQuotedAndDottedForms()
    {
        CodexTomlEditor.HasTable(Fixture, "mcp_servers.github").Should().BeTrue();
        CodexTomlEditor.HasTable(Fixture, "plugins.tsql-analyzer@official").Should().BeTrue("the fixture quotes it");
        CodexTomlEditor.HasTable(Fixture, "desktop").Should().BeTrue();
        CodexTomlEditor.HasTable(Fixture, "mcp_servers.absent").Should().BeFalse();
    }

    [Fact]
    public void AppendTable_IntoEmpty_DoesNotLeadWithBlankLines()
    {
        var result = CodexTomlEditor.AppendTable(null, "[mcp_servers.x]\nurl = \"https://x.test/mcp\"");

        result.Should().Be("[mcp_servers.x]\nurl = \"https://x.test/mcp\"\n");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
