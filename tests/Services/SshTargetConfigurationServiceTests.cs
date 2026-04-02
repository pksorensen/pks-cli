using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// Tests for SshTargetConfigurationService covering file persistence,
/// target management, and search/lookup functionality.
/// </summary>
public class SshTargetConfigurationServiceTests : TestBase
{
    private readonly string _testDirectory;
    private readonly string _configPath;

    public SshTargetConfigurationServiceTests()
    {
        _testDirectory = CreateTempDirectory();
        _configPath = Path.Combine(_testDirectory, "ssh-targets.json");
    }

    private SshTargetConfigurationService CreateService() => new(_configPath);

    [Fact]
    [Trait("Category", "Core")]
    public async Task AddTargetAsync_ValidInput_AddsAndPersists()
    {
        // Arrange
        var service = CreateService();

        // Act
        var target = await service.AddTargetAsync("myhost.example.com", "deploy", 22, "~/.ssh/id_rsa", "prod-server");

        // Assert - verify returned target
        target.Should().NotBeNull();
        target.Host.Should().Be("myhost.example.com");
        target.Username.Should().Be("deploy");
        target.Port.Should().Be(22);
        target.KeyPath.Should().Be("~/.ssh/id_rsa");
        target.Label.Should().Be("prod-server");
        target.Id.Should().NotBeNullOrEmpty();

        // Verify persistence by creating a new service instance with the same path
        var service2 = CreateService();
        var targets = await service2.ListTargetsAsync();
        targets.Should().HaveCount(1);
        targets[0].Host.Should().Be("myhost.example.com");
        targets[0].Username.Should().Be("deploy");
        targets[0].Label.Should().Be("prod-server");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task AddTargetAsync_DuplicateHostUserPort_ReplacesExisting()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("server.example.com", "admin", 22, "~/.ssh/old_key", "old-label");

        // Act - add same host+user+port with different key and label
        var replacement = await service.AddTargetAsync("server.example.com", "admin", 22, "~/.ssh/new_key", "new-label");

        // Assert
        var targets = await service.ListTargetsAsync();
        targets.Should().HaveCount(1);
        targets[0].KeyPath.Should().Be("~/.ssh/new_key");
        targets[0].Label.Should().Be("new-label");
        targets[0].Id.Should().Be(replacement.Id);
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task RemoveTargetAsync_ExistingId_RemovesTarget()
    {
        // Arrange
        var service = CreateService();
        var target = await service.AddTargetAsync("host1.example.com", "user1", 22, "~/.ssh/id_rsa", null);

        // Act
        await service.RemoveTargetAsync(target.Id);

        // Assert
        var targets = await service.ListTargetsAsync();
        targets.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task RemoveTargetAsync_NonExistentId_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("host1.example.com", "user1", 22, "~/.ssh/id_rsa", null);

        // Act
        var act = () => service.RemoveTargetAsync("non-existent-id");

        // Assert
        await act.Should().NotThrowAsync();
        var targets = await service.ListTargetsAsync();
        targets.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ListTargetsAsync_Empty_ReturnsEmptyList()
    {
        // Arrange
        var service = CreateService();

        // Act
        var targets = await service.ListTargetsAsync();

        // Assert
        targets.Should().NotBeNull();
        targets.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task ListTargetsAsync_MultipleTargets_ReturnsAll()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("host1.example.com", "user1", 22, "~/.ssh/id_rsa", "server-1");
        await service.AddTargetAsync("host2.example.com", "user2", 2222, "~/.ssh/id_ed25519", "server-2");

        // Act
        var targets = await service.ListTargetsAsync();

        // Assert
        targets.Should().HaveCount(2);
        targets.Select(t => t.Host).Should().Contain("host1.example.com");
        targets.Select(t => t.Host).Should().Contain("host2.example.com");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task FindTargetAsync_ByHost_ReturnsMatch()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("findme.example.com", "deploy", 22, "~/.ssh/id_rsa", null);
        await service.AddTargetAsync("other.example.com", "admin", 22, "~/.ssh/id_rsa", null);

        // Act
        var result = await service.FindTargetAsync("findme.example.com");

        // Assert
        result.Should().NotBeNull();
        result!.Host.Should().Be("findme.example.com");
        result.Username.Should().Be("deploy");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task FindTargetAsync_ByLabel_ReturnsMatch()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("host1.example.com", "deploy", 22, "~/.ssh/id_rsa", "production");
        await service.AddTargetAsync("host2.example.com", "deploy", 22, "~/.ssh/id_rsa", "staging");

        // Act
        var result = await service.FindTargetAsync("staging");

        // Assert
        result.Should().NotBeNull();
        result!.Host.Should().Be("host2.example.com");
        result.Label.Should().Be("staging");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task FindTargetAsync_ByUserAtHost_ReturnsMatch()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("myserver.example.com", "deploy", 22, "~/.ssh/id_rsa", null);
        await service.AddTargetAsync("myserver.example.com", "root", 22, "~/.ssh/id_rsa", null);

        // Act
        var result = await service.FindTargetAsync("deploy@myserver.example.com");

        // Assert
        result.Should().NotBeNull();
        result!.Host.Should().Be("myserver.example.com");
        result.Username.Should().Be("deploy");
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task FindTargetAsync_NoMatch_ReturnsNull()
    {
        // Arrange
        var service = CreateService();
        await service.AddTargetAsync("host1.example.com", "user1", 22, "~/.ssh/id_rsa", "my-label");

        // Act
        var result = await service.FindTargetAsync("nonexistent.example.com");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task LoadAsync_CorruptFile_ReturnsEmptyConfig()
    {
        // Arrange - write garbage to the config file
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await File.WriteAllTextAsync(_configPath, "{{{{not valid json at all!!!!}}}}");
        var service = CreateService();

        // Act
        var config = await service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Targets.Should().NotBeNull();
        config.Targets.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Core")]
    public async Task LoadAsync_MissingFile_ReturnsEmptyConfig()
    {
        // Arrange - ensure the file does not exist
        var missingPath = Path.Combine(_testDirectory, "does-not-exist", "ssh-targets.json");
        var service = new SshTargetConfigurationService(missingPath);

        // Act
        var config = await service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Targets.Should().NotBeNull();
        config.Targets.Should().BeEmpty();
    }

    public override void Dispose()
    {
        // Clean up temp directory
        try
        {
            if (Directory.Exists(_testDirectory))
                Directory.Delete(_testDirectory, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.Dispose();
    }
}
