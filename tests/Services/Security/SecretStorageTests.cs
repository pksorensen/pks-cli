using FluentAssertions;
using PKS.Infrastructure;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

/// <summary>
/// The guarantee these tests exist to hold: a credential written through pks has no readable path
/// back out through the configuration surface, and an existing plaintext <c>settings.json</c>
/// upgrades itself without anyone re-authenticating anything.
/// </summary>
public class SecretKeysTests
{
    // The real inventory, gathered from the services that own each key. Both lists are the point:
    // a miss on the left leaks a credential, a false positive on the right silently breaks a feature.
    [Theory]
    [InlineData("github.auth.token")]
    [InlineData("github.auth.token.pksorensen")]
    [InlineData("github.proj-123.token")]
    [InlineData("github.token")]
    [InlineData("msgraph.auth.token")]
    [InlineData("foundry.auth.credentials")]
    [InlineData("azure.auth.credentials")]
    [InlineData("ado.auth.credentials")]
    [InlineData("scaleway.auth.credentials")]
    [InlineData("tailscale.auth.credentials")]
    [InlineData("moonshot.auth.credentials")]
    [InlineData("openrouter.auth.credentials")]
    [InlineData("nvidia.auth.credentials")]
    [InlineData("fileshare.azure.credentials")]
    [InlineData("google:api_key")]
    [InlineData("jira:api_token")]
    [InlineData("jira:access_token")]
    [InlineData("jira:refresh_token")]
    [InlineData("agent.models.gpt-5.apiKey")]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsSecret_ClassifiesEveryStoredCredential(string key) =>
        SecretKeys.IsSecret(key).Should().BeTrue($"'{key}' holds credential material");

    [Theory]
    [InlineData("cli.first-time-warning-acknowledged")]
    [InlineData("telemetry.enabled")]
    [InlineData("hooks:quality:lint_command")]
    [InlineData("ado.git.repos")]
    [InlineData("msgraph.auth.config")]
    [InlineData("jira:base_url")]
    [InlineData("jira:auth_method")]
    [InlineData("jira:email")]
    [InlineData("jira:cloud_id")]
    [InlineData("jira:saved_filters")]
    [InlineData("jira:ac_field_id")]
    [InlineData("google:registered_at")]
    [InlineData("loganalytics.workspace_id")]
    [InlineData("appinsights.subscription_id")]
    [InlineData("agent.models.gpt-5.provider")]
    [InlineData("agent.models.gpt-5.endpoint")]
    [InlineData("update.channel")]
    [InlineData("cluster.endpoint")]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IsSecret_LeavesOrdinarySettingsReadable(string key) =>
        SecretKeys.IsSecret(key).Should().BeFalse($"'{key}' is ordinary configuration and must stay readable");
}

public class SecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pks-secretstore-{Guid.NewGuid():n}");

    public SecretStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RoundTrips_AValue_AndNeverStoresItInTheClear()
    {
        var store = new SecretStore(_dir);
        await store.SetAsync("github.auth.token", "gho_supersecret_value");

        (await store.RevealAsync("github.auth.token")).Should().Be("gho_supersecret_value");

        var onDisk = await File.ReadAllTextAsync(Path.Combine(_dir, "secrets.json"));
        onDisk.Should().NotContain("gho_supersecret_value", "the store file is what a stray `cat` would print");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Describe_ProvesPresenceAndEquality_WithoutRevealing()
    {
        var store = new SecretStore(_dir);
        await store.SetAsync("a.token", "same-value");
        await store.SetAsync("b.token", "same-value");
        await store.SetAsync("c.token", "other-value");

        var a = await store.DescribeAsync("a.token");
        var b = await store.DescribeAsync("b.token");
        var c = await store.DescribeAsync("c.token");

        a.Should().NotBeNull();
        a!.Fingerprint.Should().Be(b!.Fingerprint, "identical credentials must be recognisable as identical");
        a.Fingerprint.Should().NotBe(c!.Fingerprint);
        a.Fingerprint.Should().NotContain("same-value");

        (await store.DescribeAsync("missing.token")).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Fingerprint_IsMachineLocal_SoItIsNotAnOfflineOracle()
    {
        var otherDir = Path.Combine(_dir, "other-home");
        Directory.CreateDirectory(otherDir);

        var here = new SecretStore(_dir);
        var there = new SecretStore(otherDir);
        await here.SetAsync("x.token", "guessable");
        await there.SetAsync("x.token", "guessable");

        // Different KEKs ⇒ different digests. Someone holding the file cannot confirm a guess
        // without also holding this machine's KEK.
        (await here.DescribeAsync("x.token"))!.Fingerprint
            .Should().NotBe((await there.DescribeAsync("x.token"))!.Fingerprint);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Delete_RemovesTheValue()
    {
        var store = new SecretStore(_dir);
        await store.SetAsync("gone.token", "v");

        (await store.DeleteAsync("gone.token")).Should().BeTrue();
        (await store.HasAsync("gone.token")).Should().BeFalse();
        (await store.RevealAsync("gone.token")).Should().BeNull();
        (await store.DeleteAsync("gone.token")).Should().BeFalse();
    }
}

public class ConfigurationSecretIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pks-config-secrets-{Guid.NewGuid():n}");
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public ConfigurationSecretIsolationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ConfigurationService MakeService() => new(SettingsPath, new SecretStore(_dir));

    /// <summary>settings.json is only written when there is a non-secret setting to write, so a
    /// missing file is the strongest possible pass, not an error.</summary>
    private async Task<string> ReadSettingsOrEmptyAsync() =>
        File.Exists(SettingsPath) ? await File.ReadAllTextAsync(SettingsPath) : string.Empty;

    private async Task WriteLegacySettingsAsync(Dictionary<string, string> settings) =>
        await File.WriteAllTextAsync(SettingsPath,
            System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task SecretsAreNotReadableThroughTheConfigurationSurface()
    {
        var config = MakeService();
        await config.SetAsync("github.auth.token", "gho_secret", global: true);

        (await config.GetAsync("github.auth.token")).Should().BeNull("there is no read path, masked or otherwise");
        (await config.GetAllAsync()).Should().NotContainKey("github.auth.token");
        (await config.HasSecretAsync("github.auth.token")).Should().BeTrue("presence is still answerable");

        (await ReadSettingsOrEmptyAsync()).Should().NotContain("gho_secret");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task OrdinarySettingsStillWork()
    {
        var config = MakeService();
        await config.SetAsync("ado.git.repos", "repo-a,repo-b", global: true);

        (await config.GetAsync("ado.git.repos")).Should().Be("repo-a,repo-b");
        (await config.GetAllAsync()).Should().ContainKey("ado.git.repos");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ExistingPlaintextSettings_MigrateOnFirstLoad_WithoutReAuthenticating()
    {
        await WriteLegacySettingsAsync(new Dictionary<string, string>
        {
            ["github.auth.token"] = "{\"AccessToken\":\"gho_existing\"}",
            ["foundry.auth.credentials"] = "{\"RefreshToken\":\"rt_existing\"}",
            ["jira:api_token"] = "jira_existing",
            ["ado.git.repos"] = "repo-a",
        });

        var config = MakeService();
        var secrets = new SecretStore(_dir);

        // The credentials survive — the whole point of migrating rather than dropping them.
        (await secrets.RevealAsync("github.auth.token")).Should().Be("{\"AccessToken\":\"gho_existing\"}");
        (await secrets.RevealAsync("foundry.auth.credentials")).Should().Be("{\"RefreshToken\":\"rt_existing\"}");
        (await secrets.RevealAsync("jira:api_token")).Should().Be("jira_existing");

        // …and they are gone from the file that keeps getting dumped into transcripts.
        var onDisk = await ReadSettingsOrEmptyAsync();
        onDisk.Should().NotContain("gho_existing");
        onDisk.Should().NotContain("rt_existing");
        onDisk.Should().NotContain("jira_existing");

        (await config.GetAsync("ado.git.repos")).Should().Be("repo-a", "ordinary settings are untouched");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PlaintextWrittenBackByAnOlderBuild_IsSweptUpOnTheNextLoad()
    {
        // The rollout window: a globally installed pks migrates, then `dotnet dnx pks-cli` at an
        // older version writes the key back in the clear.
        MakeService();
        await WriteLegacySettingsAsync(new Dictionary<string, string>
        {
            ["scaleway.auth.credentials"] = "{\"SecretKey\":\"leaked_again\"}",
        });

        MakeService();

        (await ReadSettingsOrEmptyAsync()).Should().NotContain("leaked_again");
        (await new SecretStore(_dir).RevealAsync("scaleway.auth.credentials"))
            .Should().Be("{\"SecretKey\":\"leaked_again\"}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task TheOldEncryptSentinel_IsDroppedRatherThanMigrated()
    {
        // `encrypt: true` used to persist this literal instead of the value, so the credential is
        // already destroyed. Migrating it would enshrine garbage as if it were a working token.
        await WriteLegacySettingsAsync(new Dictionary<string, string>
        {
            ["auth.token"] = "***encrypted***",
            ["github.proj-1.token"] = "***encrypted***",
        });

        MakeService();

        var secrets = new SecretStore(_dir);
        (await secrets.HasAsync("auth.token")).Should().BeFalse();
        (await secrets.HasAsync("github.proj-1.token")).Should().BeFalse();
        (await ReadSettingsOrEmptyAsync()).Should().NotContain("***encrypted***");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task EncryptTrue_NowActuallyEncrypts()
    {
        var config = MakeService();
        await config.SetAsync("github.proj-9.token", "ghp_real_token", global: true, encrypt: true);

        (await new SecretStore(_dir).RevealAsync("github.proj-9.token")).Should().Be("ghp_real_token");
        (await ReadSettingsOrEmptyAsync()).Should().NotContain("ghp_real_token");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Delete_ClearsTheStoredCredential()
    {
        var config = MakeService();
        await config.SetAsync("tailscale.auth.credentials", "{\"AuthKey\":\"tskey\"}", global: true);

        await config.DeleteAsync("tailscale.auth.credentials");

        (await config.HasSecretAsync("tailscale.auth.credentials")).Should().BeFalse();
        (await new SecretStore(_dir).RevealAsync("tailscale.auth.credentials")).Should().BeNull();
    }
}

public class SecretSeedingServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pks-secret-seed-{Guid.NewGuid():n}");

    public SecretSeedingServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task SeedsOnlyTheNamedKey_IntoTheTargetHome()
    {
        var sourceDir = Path.Combine(_dir, "source");
        var targetHome = Path.Combine(_dir, "runner-home");
        Directory.CreateDirectory(sourceDir);

        var source = new SecretStore(sourceDir);
        await source.SetAsync("foundry.auth.credentials", "{\"RefreshToken\":\"rt\"}");
        await source.SetAsync("github.auth.token", "gho_must_not_travel");

        (await new SecretSeedingService(source).SeedIntoHomeAsync("foundry.auth.credentials", targetHome))
            .Should().BeTrue();

        var seeded = new SecretStore(Path.Combine(targetHome, ".pks-cli"));
        (await seeded.RevealAsync("foundry.auth.credentials")).Should().Be("{\"RefreshToken\":\"rt\"}");

        // The isolated HOME exists so the operator's other credentials stay out of it.
        (await seeded.HasAsync("github.auth.token")).Should().BeFalse();

        var onDisk = await File.ReadAllTextAsync(Path.Combine(targetHome, ".pks-cli", "secrets.json"));
        onDisk.Should().NotContain("RefreshToken");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task MissingCredential_IsReportedNotThrown()
    {
        var sourceDir = Path.Combine(_dir, "empty-source");
        Directory.CreateDirectory(sourceDir);

        (await new SecretSeedingService(new SecretStore(sourceDir))
            .SeedIntoHomeAsync("foundry.auth.credentials", Path.Combine(_dir, "home"))).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task LegacyPlaintextCredential_IsMigratedThenSeeded()
    {
        // The upgrade case, and the one that would silently break the AppHost: the credential is
        // still sitting in plaintext in settings.json and the encrypted store is empty. Seeding has
        // to migrate first, or it reports "nothing to seed" for a credential that is right there and
        // the ALP runner comes up with no Foundry session.
        var sourceDir = Path.Combine(_dir, "legacy");
        var targetHome = Path.Combine(_dir, "legacy-runner-home");
        Directory.CreateDirectory(sourceDir);

        var store = new SecretStore(sourceDir);
        var settingsPath = Path.Combine(sourceDir, "settings.json");
        var config = new ConfigurationService(settingsPath, store);

        // Written after the service exists, so this exercises the seeding service's own migration
        // call rather than the one the constructor happens to do — which is also the real rollout
        // window, where an older `dotnet dnx pks-cli` writes plaintext back underneath us.
        await File.WriteAllTextAsync(settingsPath, System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, string> { ["foundry.auth.credentials"] = "{\"RefreshToken\":\"legacy_rt\"}" }));

        (await new SecretSeedingService(store, config)
            .SeedIntoHomeAsync("foundry.auth.credentials", targetHome)).Should().BeTrue();

        (await new SecretStore(Path.Combine(targetHome, ".pks-cli")).RevealAsync("foundry.auth.credentials"))
            .Should().Be("{\"RefreshToken\":\"legacy_rt\"}");

        // And the plaintext original is gone from the file that keeps getting dumped into transcripts.
        (await File.ReadAllTextAsync(settingsPath)).Should().NotContain("legacy_rt");
    }
}
