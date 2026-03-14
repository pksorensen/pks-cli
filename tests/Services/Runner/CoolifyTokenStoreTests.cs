using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for CoolifyTokenStore — in-memory dictionary mapping job IDs to Coolify app matches.
/// </summary>
public class CoolifyTokenStoreTests
{
    private static CoolifyAppMatch CreateAppMatch(string uuid = "uuid-1", string name = "my-app", string environment = "production")
        => new CoolifyAppMatch
        {
            Uuid = uuid,
            Name = name,
            Fqdn = $"https://{name}.example.com",
            EnvironmentName = environment,
            WebhookUrl = $"https://coolify.example.com/webhooks/{uuid}",
            InstanceUrl = "https://coolify.example.com",
            Token = "coolify-token-123"
        };

    [Fact]
    public void Register_And_GetByJobId_ReturnsMatch()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var appMatch = CreateAppMatch();

        // Act
        sut.Register("job-42", appMatch);
        var result = sut.GetByJobId("job-42");

        // Assert
        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-1");
        result.Name.Should().Be("my-app");
        result.EnvironmentName.Should().Be("production");
    }

    [Fact]
    public void Register_And_GetByAppUuid_ReturnsMatch()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var appMatch = CreateAppMatch(uuid: "uuid-abc");

        // Act
        sut.Register("job-1", appMatch);
        var result = sut.GetByAppUuid("uuid-abc");

        // Assert
        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-abc");
        result.Name.Should().Be("my-app");
    }

    [Fact]
    public void GetByJobId_Unknown_ReturnsNull()
    {
        // Arrange
        var sut = new CoolifyTokenStore();

        // Act
        var result = sut.GetByJobId("nonexistent-job");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetByAppUuid_Unknown_ReturnsNull()
    {
        // Arrange
        var sut = new CoolifyTokenStore();

        // Act
        var result = sut.GetByAppUuid("nonexistent-uuid");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var appMatch = CreateAppMatch();
        sut.Register("job-42", appMatch);

        // Act
        sut.Remove("job-42");

        // Assert
        sut.GetByJobId("job-42").Should().BeNull();
        sut.GetByAppUuid("uuid-1").Should().BeNull();
    }

    [Fact]
    public void Remove_Unknown_DoesNotThrow()
    {
        // Arrange
        var sut = new CoolifyTokenStore();

        // Act
        var act = () => sut.Remove("nonexistent-job");

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Register_MultipleEntries_AllRetrievable()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var match1 = CreateAppMatch(uuid: "uuid-1", name: "app-one", environment: "production");
        var match2 = CreateAppMatch(uuid: "uuid-2", name: "app-two", environment: "staging");
        var match3 = CreateAppMatch(uuid: "uuid-3", name: "app-three", environment: "development");

        // Act
        sut.Register("job-1", match1);
        sut.Register("job-2", match2);
        sut.Register("job-3", match3);

        // Assert
        sut.GetByJobId("job-1").Should().NotBeNull();
        sut.GetByJobId("job-1")!.Uuid.Should().Be("uuid-1");

        sut.GetByJobId("job-2").Should().NotBeNull();
        sut.GetByJobId("job-2")!.Uuid.Should().Be("uuid-2");

        sut.GetByJobId("job-3").Should().NotBeNull();
        sut.GetByJobId("job-3")!.Uuid.Should().Be("uuid-3");

        sut.GetByAppUuid("uuid-1").Should().NotBeNull();
        sut.GetByAppUuid("uuid-2").Should().NotBeNull();
        sut.GetByAppUuid("uuid-3").Should().NotBeNull();
    }

    [Fact]
    public void RegisterAll_And_GetByJobIdAndEnvironment_ReturnsCorrectMatch()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var apps = new[]
        {
            CreateAppMatch(uuid: "uuid-prod", name: "app", environment: "production"),
            CreateAppMatch(uuid: "uuid-stg", name: "app", environment: "staging"),
            CreateAppMatch(uuid: "uuid-dev", name: "app", environment: "development")
        };

        // Act
        sut.RegisterAll("job-1", apps);
        var result = sut.GetByJobIdAndEnvironment("job-1", "staging");

        // Assert
        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-stg");
        result.EnvironmentName.Should().Be("staging");
    }

    [Fact]
    public void GetByJobIdAndEnvironment_UnknownEnvironment_ReturnsNull()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var apps = new[]
        {
            CreateAppMatch(uuid: "uuid-prod", name: "app", environment: "production"),
            CreateAppMatch(uuid: "uuid-stg", name: "app", environment: "staging")
        };
        sut.RegisterAll("job-1", apps);

        // Act — request an environment that doesn't exist
        var result = sut.GetByJobIdAndEnvironment("job-1", "preview");

        // Assert — strict match, no fallback to prevent accidental deployments
        result.Should().BeNull();
    }

    [Fact]
    public void GetByJobIdAndEnvironment_CaseInsensitive()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var apps = new[]
        {
            CreateAppMatch(uuid: "uuid-stg", name: "app", environment: "Staging")
        };
        sut.RegisterAll("job-1", apps);

        // Act
        var result = sut.GetByJobIdAndEnvironment("job-1", "staging");

        // Assert
        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-stg");
    }

    [Fact]
    public void GetAllByJobId_ReturnsAllApps()
    {
        // Arrange
        var sut = new CoolifyTokenStore();
        var apps = new[]
        {
            CreateAppMatch(uuid: "uuid-1", name: "app", environment: "production"),
            CreateAppMatch(uuid: "uuid-2", name: "app", environment: "staging"),
            CreateAppMatch(uuid: "uuid-3", name: "app", environment: "development")
        };
        sut.RegisterAll("job-1", apps);

        // Act
        var result = sut.GetAllByJobId("job-1");

        // Assert
        result.Should().HaveCount(3);
        result.Select(a => a.Uuid).Should().BeEquivalentTo(new[] { "uuid-1", "uuid-2", "uuid-3" });
    }

    [Fact]
    public void GetAllByJobId_Unknown_ReturnsEmptyList()
    {
        // Arrange
        var sut = new CoolifyTokenStore();

        // Act
        var result = sut.GetAllByJobId("nonexistent-job");

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }
}
