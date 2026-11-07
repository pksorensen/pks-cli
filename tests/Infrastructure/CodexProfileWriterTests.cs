using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Agent.Setup;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Guards <see cref="CodexProfileWriter"/> against a real <c>CODEX_HOME</c> on disk.
///
/// <para>
/// These run in a temporary directory via the <c>CODEX_HOME</c> environment variable, which is
/// the same lever the manual probes used. That variable is process-wide, hence the collection:
/// two of these running concurrently would each see the other's directory. They assert the two placement rules that are properties
/// of Codex rather than choices of ours — MCP servers can live in a profile layer, marketplaces
/// cannot — and that a profile carrying no provider never writes <c>model_provider</c>.
/// </para>
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
[Collection("CodexHome")]
public sealed class CodexProfileWriterTests : IDisposable
{
    private readonly string _home;
    private readonly string? _priorHome;

    public CodexProfileWriterTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "pks-codex-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);

        _priorHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", _home);

        // The ledger needs no isolation: every test here constructs its own
        // CodexProfileLedger and passes it in, and CodexProfileWriter never persists one —
        // only the commands call Save(). So ~/.pks-cli is never read or written.
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", _priorHome);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string ConfigPath => Path.Combine(_home, "config.toml");

    private static AspProfile ContextOnlyProfile() => new()
    {
        Id = "my-project",
        DisplayName = "My Project (poul)",
        Group = "poul",
        McpServers = [new AspMcpServer { Name = "agentics-project", Url = "https://agentics.dk/p/poul/my-project/mcp" }],
        Marketplaces = [new AspMarketplace { Name = "agentics-skills", Url = "https://github.com/pksorensen/skillz.git" }],
    };

    private static CodexApplyRequest Request(AspProfile profile, bool makeActive = false) => new(
        Origin: "https://agentics.dk",
        SourceId: "agentics",
        SourceDisplayName: "Agentics Factory",
        Issuer: "https://login.agentics.dk/realms/agentics",
        ClientId: "https://agentics.dk/oauth/clients/pks-cli.json",
        Profile: profile,
        MakeActive: makeActive);

    [Fact]
    public void Apply_PutsMcpServersInTheLayer_AndMarketplacesInConfigToml()
    {
        var ledger = new CodexProfileLedger();

        var result = CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        result.Namespace.Should().Be("agentics-my-project");

        var layer = File.ReadAllText(Path.Combine(_home, "agentics-my-project.config.toml"));
        layer.Should().Contain("[mcp_servers.agentics-project]");
        layer.Should().Contain("url = \"https://agentics.dk/p/poul/my-project/mcp\"");

        // `codex plugin` refuses -p, so a marketplace in the layer would never be read.
        layer.Should().NotContain("marketplaces");

        var config = File.ReadAllText(ConfigPath);
        config.Should().Contain("[marketplaces.agentics-skills]");
        config.Should().Contain("source = \"https://github.com/pksorensen/skillz.git\"");
        config.Should().NotContain("[mcp_servers.agentics-project]", "not active, so it stays out of the base");
    }

    [Fact]
    public void Apply_WithNoProvider_NeverWritesModelProvider()
    {
        // The failure this guards: `model_provider` naming a table that does not exist fails the
        // whole config load — every `codex` invocation, not one thread — with
        // "Model provider `x` not found". Measured on codex-cli 0.163.0.
        var ledger = new CodexProfileLedger();

        CodexProfileWriter.Apply(Request(ContextOnlyProfile(), makeActive: true), ledger);

        var layer = File.ReadAllText(Path.Combine(_home, "agentics-my-project.config.toml"));
        var config = File.ReadAllText(ConfigPath);

        layer.Should().NotContain("model_provider");
        config.Should().NotContain("model_provider");
        layer.Should().NotContain("model_providers.");
    }

    [Fact]
    public void Apply_WithAProvider_UsesEnvHttpHeaders_AndNamespacesTheVariable()
    {
        var profile = new AspProfile
        {
            Id = "home",
            Provider = new AspProvider
            {
                Name = "Context& Home",
                BaseUrl = "https://home.example/gateway/openai/v1",
                WireApi = "responses",
                Auth = new AspProviderAuth { Kind = "header", Header = "Ocp-Apim-Subscription-Key", Env = "HOME_GATEWAY_KEY" },
            },
            Model = new AspModel { Default = "gpt-5.6-sol" },
        };

        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(profile) with { SourceId = "ctxhome" }, ledger);

        var layer = File.ReadAllText(Path.Combine(_home, "ctxhome-home.config.toml"));

        // env_http_headers, not env_key: APIM matches a subscription key in a named header and
        // cannot match one behind a "Bearer " prefix, which is what env_key emits.
        layer.Should().Contain("env_http_headers");
        layer.Should().NotContain("env_key");
        layer.Should().Contain("Ocp-Apim-Subscription-Key");

        // Suffixed with the source, so two platforms publishing the same base name cannot collide.
        layer.Should().Contain("HOME_GATEWAY_KEY_CTXHOME");
        layer.Should().Contain("model_provider = \"ctxhome-home\"");
        layer.Should().Contain("[model_providers.ctxhome-home]");
        layer.Should().Contain("wire_api = \"responses\"");
    }

    [Fact]
    public void Apply_NeverWritesACredentialValue()
    {
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile(), makeActive: true), ledger);

        var written = Directory.GetFiles(_home, "*.toml", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(_home, "*.json", SearchOption.AllDirectories))
            .Select(File.ReadAllText);

        // Nothing secret-shaped reaches disk. Only variable NAMES are ever written.
        foreach (var content in written)
        {
            content.Should().NotContain("gwk_");
            content.Should().NotContain("pct_");
        }
    }

    [Fact]
    public void Apply_PreservesAForeignConfig_AndIsIdempotent()
    {
        const string foreign = """
            model_reasoning_effort = "high"

            [desktop]
            theme = "dark"

            [mcp_servers.github]
            command = "npx"
            args = ["-y", "@modelcontextprotocol/server-github"]
            """;

        File.WriteAllText(ConfigPath, foreign);

        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        var first = File.ReadAllText(ConfigPath);

        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        var second = File.ReadAllText(ConfigPath);

        second.Should().Be(first, "a re-run must not duplicate tables or accumulate blank lines");
        first.Should().Contain("[desktop]").And.Contain("theme = \"dark\"");
        first.Should().Contain("[mcp_servers.github]");
        first.Should().Contain("model_reasoning_effort = \"high\"");
    }

    [Fact]
    public void UseThenNone_MovesTablesInAndOut_ButLeavesMarketplacesWhereTheyWork()
    {
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        var (ok, _) = CodexProfileWriter.Use("agentics-my-project", ledger);
        ok.Should().BeTrue();
        ledger.Active.Should().Be("agentics-my-project");

        var active = File.ReadAllText(ConfigPath);
        active.Should().Contain("[mcp_servers.agentics-project]", "a bare `codex` should now see it");
        active.Should().Contain("[marketplaces.agentics-skills]");

        var (clearedOk, _) = CodexProfileWriter.Use(null, ledger);
        clearedOk.Should().BeTrue();
        ledger.Active.Should().BeNull();

        var cleared = File.ReadAllText(ConfigPath);
        cleared.Should().NotContain("[mcp_servers.agentics-project]");

        // Marketplaces cannot live in a layer file at all, so clearing the active profile must
        // not take them away — that would silently uninstall the reader's plugins.
        cleared.Should().Contain("[marketplaces.agentics-skills]");
    }

    [Fact]
    public void Remove_TakesTheLayerFileAndTheTables_AndForgetsTheProfile()
    {
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile(), makeActive: true), ledger);

        var layerPath = Path.Combine(_home, "agentics-my-project.config.toml");
        File.Exists(layerPath).Should().BeTrue();

        CodexProfileWriter.Remove("agentics-my-project", ledger).Should().BeTrue();

        File.Exists(layerPath).Should().BeFalse();
        var config = File.ReadAllText(ConfigPath);
        config.Should().NotContain("[mcp_servers.agentics-project]");
        config.Should().NotContain("[marketplaces.agentics-skills]");
        ledger.AllProfiles().Should().BeEmpty();
        ledger.Active.Should().BeNull();
    }

    [Fact]
    public void Apply_RepairsALegacyProfileSelectorThatWouldBreakEveryProfile()
    {
        // `profile = "x"` is a hard config-load error on 0.163.0 AND makes -p fail for every
        // profile, so finding one and leaving it would hand the reader nothing that works.
        File.WriteAllText(ConfigPath, "profile = \"old\"\n\n[desktop]\ntheme = \"dark\"\n");

        var ledger = new CodexProfileLedger();
        var result = CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        var config = File.ReadAllText(ConfigPath);
        config.Should().NotContain("profile = \"old\"");
        config.Should().Contain("[desktop]");
        result.Notes.Should().Contain(n => n.Contains("legacy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_SkipsAMarketplaceWithNoUrl_RatherThanWritingABrokenOne()
    {
        // Codex loads marketplaces eagerly and ONE bad entry is fatal to every `codex plugin`
        // command, including those for the marketplaces that are fine.
        var profile = ContextOnlyProfile();
        profile.Marketplaces = [new AspMarketplace { Name = "builtin", Url = null }];

        var ledger = new CodexProfileLedger();
        var result = CodexProfileWriter.Apply(Request(profile), ledger);

        // And with nothing left to put there, config.toml is not created at all — a file we have
        // no content for is a file we should not touch.
        var config = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;
        config.Should().NotContain("[marketplaces.builtin]");
        result.Notes.Should().Contain(n => n.Contains("builtin", StringComparison.Ordinal));

        // The profile itself still landed: only the unusable marketplace was dropped.
        File.Exists(Path.Combine(_home, "agentics-my-project.config.toml")).Should().BeTrue();
    }

    [Fact]
    public void Apply_OnTheActiveProfile_KeepsItActive()
    {
        // The regression: `connect` is the update path, and it passes MakeActive only when
        // --activate was given. So a reader who ran `pks codex use X` and then re-connected
        // would find a bare `codex` silently stripped of X's servers — and `connect --all`
        // would do it on every run. A re-run must not move somebody's choice out from under them.
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        CodexProfileWriter.Use("agentics-my-project", ledger);

        var afterUse = File.ReadAllText(ConfigPath);
        afterUse.Should().Contain("[mcp_servers.agentics-project]");

        // Re-run exactly as `connect` does it: MakeActive false.
        CodexProfileWriter.Apply(Request(ContextOnlyProfile(), makeActive: false), ledger);

        ledger.Active.Should().Be("agentics-my-project", "the reader chose it; a refresh is not a reset");
        File.ReadAllText(ConfigPath).Should().Contain("[mcp_servers.agentics-project]");
    }

    [Fact]
    public void Apply_OnAnInactiveProfile_StillLeavesItInactive()
    {
        // The other half: keeping the active one active must not make every refresh active.
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        ledger.Active.Should().BeNull();
        File.ReadAllText(ConfigPath).Should().NotContain("[mcp_servers.agentics-project]");
    }

    [Fact]
    public void TwoOriginsClaimingTheSameSourceId_DoNotOverwriteEachOther()
    {
        // The collision is by construction, not bad luck: a platform's local instance and its
        // production instance run the SAME code, so they publish the same source id and the same
        // profile ids. agentics.dk does exactly this — SOURCE_ID is a constant. Trusting the
        // claimed id means the second connect overwrites the first's layer file, which is the
        // one failure namespacing exists to prevent.
        var ledger = new CodexProfileLedger();

        var prod = CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        var local = CodexProfileWriter.Apply(
            Request(ContextOnlyProfile()) with { Origin = "http://localhost:3000" }, ledger);

        prod.Namespace.Should().Be("agentics-my-project", "the first one keeps the clean name");
        local.Namespace.Should().NotBe(prod.Namespace);
        local.Notes.Should().Contain(n => n.Contains("already uses", StringComparison.Ordinal));

        // Both layer files must exist, or one platform has silently lost its configuration.
        File.Exists(Path.Combine(_home, $"{prod.Namespace}.config.toml")).Should().BeTrue();
        File.Exists(Path.Combine(_home, $"{local.Namespace}.config.toml")).Should().BeTrue();

        ledger.AllProfiles().Should().HaveCount(2);
        ledger.AllProfiles().Select(r => r.Profile.Namespace).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AnAssignedSourceId_SurvivesEveryLaterConnect()
    {
        // A slug that moved between runs would orphan every table written under the old one,
        // so it is pinned in the ledger at first connect and read back afterwards.
        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);
        var local = Request(ContextOnlyProfile()) with { Origin = "http://localhost:3000" };

        var first = CodexProfileWriter.Apply(local, ledger).Namespace;
        var second = CodexProfileWriter.Apply(local, ledger).Namespace;

        second.Should().Be(first);
        ledger.AllProfiles().Should().HaveCount(2, "a re-run must not add a third row");
    }

    [Fact]
    public void Apply_RepairsADanglingModelProvider_LeftByAnotherAuthor()
    {
        // Another author's leftover, not ours: someone uninstalled a provider, or an older
        // `pks codex init` block was removed and its key outlived it. Either way the config
        // fails to load ENTIRELY — "Model provider `azure` not found" on every invocation —
        // so the only useful behaviour is to repair it and say so.
        File.WriteAllText(ConfigPath, "model_provider = \"azure\"\nmodel = \"gpt-5\"\n\n[desktop]\ntheme = \"dark\"\n");

        var ledger = new CodexProfileLedger();
        var result = CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        var config = File.ReadAllText(ConfigPath);
        config.Should().NotContain("model_provider");
        config.Should().Contain("[desktop]", "repairing one key must not disturb anything else");
        config.Should().Contain("model = \"gpt-5\"", "only the dangling key goes");
        result.Notes.Should().Contain(n => n.Contains("model_provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_LeavesAModelProviderThatDoesResolve()
    {
        // The other half: a reader with a working provider of their own keeps it.
        File.WriteAllText(ConfigPath,
            "model_provider = \"mine\"\n\n[model_providers.mine]\nname = \"Mine\"\nbase_url = \"https://example.test/v1\"\n");

        var ledger = new CodexProfileLedger();
        var result = CodexProfileWriter.Apply(Request(ContextOnlyProfile()), ledger);

        File.ReadAllText(ConfigPath).Should().Contain("model_provider = \"mine\"");
        result.Notes.Should().NotContain(n => n.Contains("Removed `model_provider", StringComparison.Ordinal));
    }
}
