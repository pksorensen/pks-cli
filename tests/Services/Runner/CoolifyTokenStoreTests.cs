using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for CoolifyTokenStore — in-memory dictionary mapping job IDs to Coolify app matches.
/// </summary>
public class CoolifyTokenStoreTests
{
    private static CoolifyAppMatch CreateAppMatch(
        string uuid = "uuid-1",
        string name = "my-app",
        string environment = "production",
        string instanceUrl = "https://coolify.example.com",
        string role = CoolifyInstanceRoles.Primary)
        => new CoolifyAppMatch
        {
            Uuid = uuid,
            Name = name,
            Fqdn = $"https://{name}.example.com",
            EnvironmentName = environment,
            WebhookUrl = $"{instanceUrl}/webhooks/{uuid}",
            InstanceUrl = instanceUrl,
            Token = "coolify-token-123",
            InstanceRole = role
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
    public void GetByJobIdAndApp_SelectsByName()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-landing", name: "commuteconnects-www", environment: "production"),
            CreateAppMatch(uuid: "uuid-carshare", name: "carshare", environment: "production")
        });

        var result = sut.GetByJobIdAndApp("job-1", "carshare");

        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-carshare");
    }

    [Fact]
    public void GetByJobIdAndApp_SelectsByUuid()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "k84g808ckcg8sw4kgoc4kks0", name: "carshare", environment: "production"),
            CreateAppMatch(uuid: "b2q298qwcs0os28gmkspa5jo", name: "commuteconnects-www", environment: "production")
        });

        var result = sut.GetByJobIdAndApp("job-1", "b2q298qwcs0os28gmkspa5jo");

        result.Should().NotBeNull();
        result!.Name.Should().Be("commuteconnects-www");
    }

    [Fact]
    public void GetByJobIdAndApp_CaseInsensitive()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[] { CreateAppMatch(uuid: "uuid-1", name: "CarShare") });

        sut.GetByJobIdAndApp("job-1", "carshare").Should().NotBeNull();
    }

    [Fact]
    public void GetByJobIdAndApp_UnknownApp_ReturnsNull_DoesNotFallBack()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-landing", name: "commuteconnects-www", environment: "production")
        });

        // The whole point: a workflow naming an app it cannot have must get nothing, not the
        // neighbour that happens to be registered. Falling back here is the 2026-09-20 outage.
        sut.GetByJobIdAndApp("job-1", "carshare").Should().BeNull();
    }

    [Fact]
    public void GetByJobIdAndApp_NarrowsByEnvironment()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-stg", name: "carshare", environment: "staging"),
            CreateAppMatch(uuid: "uuid-prod", name: "carshare", environment: "production")
        });

        sut.GetByJobIdAndApp("job-1", "carshare", "production")!.Uuid.Should().Be("uuid-prod");
        sut.GetByJobIdAndApp("job-1", "carshare", "staging")!.Uuid.Should().Be("uuid-stg");
    }

    [Fact]
    public void GetByJobIdAndApp_EnvironmentNarrowsButNeverSelects()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-landing", name: "commuteconnects-www", environment: "production"),
            CreateAppMatch(uuid: "uuid-carshare", name: "carshare", environment: "staging")
        });

        // The named app is in a different environment than the one requested. Answer with the app
        // that was named — never with a different app that merely sits in the right environment.
        var result = sut.GetByJobIdAndApp("job-1", "carshare", "production");

        result.Should().NotBeNull();
        result!.Uuid.Should().Be("uuid-carshare");
    }

    [Fact]
    public void GetByJobIdAndApp_EmptyApp_ReturnsNull()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[] { CreateAppMatch() });

        sut.GetByJobIdAndApp("job-1", "").Should().BeNull();
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

    // ---------------------------------------------------------------------------------------------
    // Ambiguity. An application that has been migrated exists on two instances for a while, under
    // the same name and in the same environment, and both are then equally good answers. The store
    // used to hand back whichever one came first, so the deploy went wherever the registration order
    // pointed — silently, and to a box nobody chose.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveByJobIdAndApp_SameAppOnTwoPrimaryInstances_IsAmbiguous()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-a", name: "site", environment: "production", instanceUrl: "https://a.example.com"),
            CreateAppMatch(uuid: "uuid-b", name: "site", environment: "production", instanceUrl: "https://b.example.com")
        });

        var result = sut.ResolveByJobIdAndApp("job-1", "site", "production");

        result.IsAmbiguous.Should().BeTrue();
        result.App.Should().BeNull();
        result.Candidates.Select(c => c.Uuid).Should().BeEquivalentTo(new[] { "uuid-a", "uuid-b" });
    }

    [Fact]
    public void GetByJobIdAndApp_WhenAmbiguous_ReturnsNull_RatherThanGuessing()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-a", name: "site", environment: "production", instanceUrl: "https://a.example.com"),
            CreateAppMatch(uuid: "uuid-b", name: "site", environment: "production", instanceUrl: "https://b.example.com")
        });

        sut.GetByJobIdAndApp("job-1", "site", "production").Should().BeNull();
    }

    [Fact]
    public void ResolveByJobIdAndApp_StandbyLosesTheTie()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-old", name: "site", environment: "production",
                instanceUrl: "https://old.example.com", role: CoolifyInstanceRoles.Standby),
            CreateAppMatch(uuid: "uuid-new", name: "site", environment: "production",
                instanceUrl: "https://new.example.com")
        });

        var result = sut.ResolveByJobIdAndApp("job-1", "site", "production");

        result.IsResolved.Should().BeTrue();
        result.App!.Uuid.Should().Be("uuid-new");
        // The skip is recorded, because a deploy that went to one of two boxes is exactly the event
        // that needs a trace afterwards.
        result.TieBreak.Should().NotBeNullOrEmpty();
        result.TieBreak.Should().Contain("uuid-old");
    }

    [Fact]
    public void ResolveByJobIdAndApp_SoleMatchOnStandby_StillResolves()
    {
        // A standby is outranked, never excluded — an application that has not been migrated yet
        // exists only on the old box, and must stay deployable.
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-old", name: "keycloak", environment: "production",
                instanceUrl: "https://old.example.com", role: CoolifyInstanceRoles.Standby)
        });

        var result = sut.ResolveByJobIdAndApp("job-1", "keycloak", "production");

        result.IsResolved.Should().BeTrue();
        result.App!.Uuid.Should().Be("uuid-old");
        result.TieBreak.Should().BeNull();
    }

    [Fact]
    public void ResolveByJobIdAndApp_TwoStandbysAndNoPrimary_IsAmbiguous()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-a", name: "site", environment: "production",
                instanceUrl: "https://a.example.com", role: CoolifyInstanceRoles.Standby),
            CreateAppMatch(uuid: "uuid-b", name: "site", environment: "production",
                instanceUrl: "https://b.example.com", role: CoolifyInstanceRoles.Standby)
        });

        sut.ResolveByJobIdAndApp("job-1", "site", "production").IsAmbiguous.Should().BeTrue();
    }

    [Fact]
    public void ResolveByJobIdAndApp_AbsentRoleCountsAsPrimary()
    {
        // coolify.json files written before the role existed have no such field, and must keep
        // resolving the way they did — as primaries, so a standby still loses to them.
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            new CoolifyAppMatch { Uuid = "uuid-plain", Name = "site", EnvironmentName = "production" },
            CreateAppMatch(uuid: "uuid-standby", name: "site", environment: "production",
                instanceUrl: "https://old.example.com", role: CoolifyInstanceRoles.Standby)
        });

        var result = sut.ResolveByJobIdAndApp("job-1", "site", "production");

        result.IsResolved.Should().BeTrue();
        result.App!.Uuid.Should().Be("uuid-plain");
    }

    [Fact]
    public void ResolveByJobIdAndApp_UnknownApp_IsNotFound_NotAmbiguous()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[] { CreateAppMatch(name: "site") });

        var result = sut.ResolveByJobIdAndApp("job-1", "something-else");

        result.Outcome.Should().Be(CoolifyResolutionOutcome.NotFound);
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void ResolveByJobIdAndEnvironment_SameEnvironmentOnTwoInstances_IsAmbiguous()
    {
        // The environment branch carried the same defect one step over: FirstOrDefault across apps
        // that all sit in the requested environment.
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-a", name: "site", environment: "production", instanceUrl: "https://a.example.com"),
            CreateAppMatch(uuid: "uuid-b", name: "site", environment: "production", instanceUrl: "https://b.example.com")
        });

        var result = sut.ResolveByJobIdAndEnvironment("job-1", "production");

        result.IsAmbiguous.Should().BeTrue();
        result.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void ResolveByJobIdAndEnvironment_StandbyLosesTheTie()
    {
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-old", name: "site", environment: "production",
                instanceUrl: "https://old.example.com", role: CoolifyInstanceRoles.Standby),
            CreateAppMatch(uuid: "uuid-new", name: "site", environment: "production",
                instanceUrl: "https://new.example.com")
        });

        sut.ResolveByJobIdAndEnvironment("job-1", "production").App!.Uuid.Should().Be("uuid-new");
    }

    [Fact]
    public void ResolveByJobId_SeveralApps_IsAmbiguous()
    {
        // The bare fallback — neither app nor environment named — used to answer list[0].
        var sut = new CoolifyTokenStore();
        sut.RegisterAll("job-1", new[]
        {
            CreateAppMatch(uuid: "uuid-a", name: "site"),
            CreateAppMatch(uuid: "uuid-b", name: "api")
        });

        sut.ResolveByJobId("job-1").IsAmbiguous.Should().BeTrue();
    }

    [Fact]
    public void ResolveByJobId_SingleApp_Resolves()
    {
        var sut = new CoolifyTokenStore();
        sut.Register("job-1", CreateAppMatch(uuid: "uuid-only"));

        sut.ResolveByJobId("job-1").App!.Uuid.Should().Be("uuid-only");
    }

    [Fact]
    public void ResolveByJobId_UnknownJob_IsNotFound()
    {
        new CoolifyTokenStore().ResolveByJobId("nope").Outcome.Should().Be(CoolifyResolutionOutcome.NotFound);
    }
}
