using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Claude;
using Xunit;

namespace PKS.CLI.Tests.Commands.Devcontainer;

/// <summary>
/// Tests for the BuildClaudeManagedSettingsMountAsync behaviour via a testable subclass.
/// </summary>
public class DevcontainerSpawnCommandManagedSettingsMountTests : TestBase
{
    private readonly Mock<IClaudeMarketplaceConfigurationService> _configServiceMock;
    private readonly Mock<IClaudeManagedSettingsRenderer> _rendererMock;

    public DevcontainerSpawnCommandManagedSettingsMountTests()
    {
        _configServiceMock = new Mock<IClaudeMarketplaceConfigurationService>();
        _rendererMock = new Mock<IClaudeManagedSettingsRenderer>();
    }

    private TestableDevcontainerSpawnCommand CreateCommand(ClaudeMarketplaceConfiguration config)
    {
        _configServiceMock.Setup(s => s.LoadAsync()).ReturnsAsync(config);
        return new TestableDevcontainerSpawnCommand(_configServiceMock.Object, _rendererMock.Object, TestConsole);
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildClaudeManagedSettingsMountAsync_NoMarketplaces_ReturnsNull()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration(); // empty
        var command = CreateCommand(config);
        var target = new SshTarget { Host = "host1", Username = "user1", Port = 22 };

        // Act
        var result = await command.ExposedBuildClaudeManagedSettingsMountAsync(target, "-o StrictHostKeyChecking=no", "scope-123");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildClaudeManagedSettingsMountAsync_OneMarketplace_ReturnsMountArg()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace>
            {
                new() { Id = "test", Source = new ClaudeMarketplaceSource { SourceType = "url", Url = "https://example.com/marketplace.json" } }
            }
        };
        _rendererMock.Setup(r => r.Render(It.IsAny<ClaudeMarketplaceConfiguration>()))
            .Returns("{\"extraKnownMarketplaces\":{}}");

        var command = CreateCommand(config);
        var target = new SshTarget { Host = "myhost.example.com", Username = "devuser", Port = 22 };

        // Act
        var result = await command.ExposedBuildClaudeManagedSettingsMountAsync(target, "-o StrictHostKeyChecking=no", "scope-abc");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("--mount");
        result.Should().Contain("type=bind");
        result.Should().Contain("/etc/claude-code");
        result.Should().Contain("scope-abc");
        // devcontainer up's --mount parser only accepts type/source/target/external —
        // ',readonly' is rejected. Read-only is enforced via chmod 0444 on the source file.
        result.Should().NotContain("readonly");
    }

    [Fact]
    [Trait("Category", "Claude")]
    public async Task BuildClaudeManagedSettingsMountAsync_DifferentScopeIds_DifferentPaths()
    {
        // Arrange
        var config = new ClaudeMarketplaceConfiguration
        {
            Marketplaces = new List<ClaudeMarketplace>
            {
                new() { Id = "test", Source = new ClaudeMarketplaceSource { SourceType = "url" } }
            }
        };
        _rendererMock.Setup(r => r.Render(It.IsAny<ClaudeMarketplaceConfiguration>()))
            .Returns("{}");

        var command = CreateCommand(config);
        var target = new SshTarget { Host = "myhost", Username = "user", Port = 22 };

        // Act
        var result1 = await command.ExposedBuildClaudeManagedSettingsMountAsync(target, "", "scope-aaa");
        var result2 = await command.ExposedBuildClaudeManagedSettingsMountAsync(target, "", "scope-bbb");

        // Assert
        result1.Should().Contain("scope-aaa");
        result2.Should().Contain("scope-bbb");
        result1.Should().NotBe(result2);
    }
}

/// <summary>
/// Testable subclass of DevcontainerSpawnCommand that exposes protected methods and
/// overrides SSH execution so no actual SSH connection is needed.
/// </summary>
internal class TestableDevcontainerSpawnCommand : PKS.Commands.Devcontainer.DevcontainerSpawnCommand
{
    public TestableDevcontainerSpawnCommand(
        IClaudeMarketplaceConfigurationService configService,
        IClaudeManagedSettingsRenderer renderer,
        Spectre.Console.IAnsiConsole console)
        : base(
            new Mock<PKS.Infrastructure.Services.IDevcontainerSpawnerService>().Object,
            new Mock<PKS.Infrastructure.Services.ISshTargetConfigurationService>().Object,
            new Mock<PKS.Infrastructure.Services.INuGetTemplateDiscoveryService>().Object,
            new Mock<PKS.Infrastructure.Services.IAzureVmMetadataService>().Object,
            new Mock<PKS.Infrastructure.Services.IAzureAuthService>().Object,
            new Mock<PKS.Infrastructure.Services.IAzureVmService>().Object,
            configService,
            renderer,
            console)
    {
    }

    /// <summary>
    /// Exposes BuildClaudeManagedSettingsMountAsync for testing.
    /// Overrides SSH write to a no-op (no actual SSH connections).
    /// </summary>
    public Task<string?> ExposedBuildClaudeManagedSettingsMountAsync(
        SshTarget target, string sshArgs, string scopeId)
        => BuildClaudeManagedSettingsMountAsync(target, sshArgs, scopeId,
            sshSetupOverride: (sid, _) => Task.FromResult($"/home/{target.Username}/.pks-cli/managed-settings/{sid}"));
}
