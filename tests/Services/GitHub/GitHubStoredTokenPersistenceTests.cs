using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.GitHub;

/// <summary>
/// The round trip of the stored GitHub token, which is the one place where quarantining a credential
/// can break users silently rather than loudly.
///
/// Two failure modes, both invisible at compile time: serializing the DTO with ordinary options writes
/// <c>"***"</c> to the store and destroys everyone's login on the next save, and swapping in
/// persistence options that lose the service's snake_case naming policy makes every token already on
/// disk deserialize to empty — logging out every existing user, which is exactly what the migration
/// promised would not happen.
/// </summary>
public class GitHubStoredTokenPersistenceTests
{
    private const string StorageKey = "github.auth.token";

    private static (GitHubAuthenticationService Service, Mock<IConfigurationService> Config, FakeSecretResolver Secrets)
        Build(string? storedJson = null)
    {
        var config = new Mock<IConfigurationService>();
        var secrets = FakeSecretResolver.Empty;
        if (storedJson is not null) secrets.With(StorageKey, storedJson);

        var service = new GitHubAuthenticationService(
            new HttpClient(),
            config.Object,
            NullLogger<GitHubAuthenticationService>.Instance,
            secrets);

        return (service, config, secrets);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task GetStoredTokenAsync_ReadsTheFormatAlreadyOnDisk()
    {
        // Written by every pks-cli before the quarantine: snake_case names, plaintext tokens. Nobody
        // re-authenticates because of this change, so this shape has to keep resolving.
        var (service, _, _) = Build(
            """
            {"access_token":"gho_live_token","refresh_token":"ghr_live_refresh","scopes":["repo"],
             "created_at":"2026-08-01T10:00:00Z","is_valid":true,"last_validated":"2026-08-01T10:00:00Z"}
            """);

        var stored = await service.GetStoredTokenAsync();

        stored.Should().NotBeNull();
        stored!.AccessToken.Reveal().Should().Be("gho_live_token");
        stored.RefreshToken.Reveal().Should().Be("ghr_live_refresh");
        stored.IsValid.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task StoreTokenAsync_WritesTheRealTokenUnderTheSameNames()
    {
        var (service, config, _) = Build();
        string? written = null;
        config.Setup(c => c.SetAsync(StorageKey, It.IsAny<string>(), true, false))
            .Callback<string, string, bool, bool>((_, value, _, _) => written = value)
            .Returns(Task.CompletedTask);

        await service.StoreTokenAsync(new GitHubStoredToken
        {
            AccessToken = SecretValue.From("gho_fresh"),
            RefreshToken = SecretValue.From("ghr_fresh"),
            Scopes = ["repo"],
            CreatedAt = DateTime.UtcNow,
            IsValid = true,
            LastValidated = DateTime.UtcNow
        });

        written.Should().NotBeNull();
        // The credential itself, not the mask — a stored "***" reads back as absent and logs the user out.
        written.Should().Contain("gho_fresh").And.Contain("ghr_fresh").And.NotContain("***");
        // And under the names the old binaries wrote, so a downgrade during the rollout still reads it.
        written.Should().Contain("\"access_token\"").And.Contain("\"refresh_token\"");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void SerializingTheTokenAnywhereElseMasksIt()
    {
        // The safety net behind the two tests above: a support bundle, a log line or an ssh payload
        // that serializes this DTO with ordinary options gets nothing usable.
        var json = JsonSerializer.Serialize(new GitHubStoredToken
        {
            AccessToken = SecretValue.From("gho_secret"),
            RefreshToken = SecretValue.From("ghr_secret")
        });

        json.Should().NotContain("gho_secret").And.NotContain("ghr_secret");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task DescribeStoredTokenKindAsync_ClassifiesWithoutYieldingTheToken()
    {
        // `github status --verbose` used to do AccessToken.StartsWith("ghp_") itself. SecretValue has
        // no prefix test on purpose — a command that can ask "does it start with X" is an oracle — so
        // the classification lives in the service and only the label comes back.
        var (pat, _, _) = Build("""{"access_token":"ghp_classic","is_valid":true}""");
        (await pat.DescribeStoredTokenKindAsync()).Should().Be("PAT (ghp_)");

        var (oauth, _, _) = Build("""{"access_token":"gho_device_flow","is_valid":true}""");
        (await oauth.DescribeStoredTokenKindAsync()).Should().Be("OAuth (gho_)");

        var (none, _, _) = Build();
        (await none.DescribeStoredTokenKindAsync()).Should().BeNull();
    }
}
