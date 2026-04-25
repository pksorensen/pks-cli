using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Firecracker;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Services.Firecracker;

/// <summary>
/// Tests for FirecrackerRunnerConfigurationService covering load, save, and CRUD operations
/// on firecracker runner registrations. Uses temp directories for file isolation.
/// </summary>
public class FirecrackerRunnerConfigurationServiceTests : TestBase
{
    private readonly Mock<ILogger<FirecrackerRunnerConfigurationService>> _mockLogger;
    private readonly string _testDirectory;
    private readonly string _configFilePath;
    private readonly FirecrackerRunnerConfigurationService _service;

    public FirecrackerRunnerConfigurationServiceTests()
    {
        _mockLogger = new Mock<ILogger<FirecrackerRunnerConfigurationService>>();
        _testDirectory = CreateTempDirectory();
        _configFilePath = Path.Combine(_testDirectory, "firecracker-runners.json");
        _service = new FirecrackerRunnerConfigurationService(_mockLogger.Object, _configFilePath);
    }

    #region LoadAsync

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaultConfiguration()
    {
        // Act
        var config = await _service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Registrations.Should().BeEmpty();
        config.Defaults.Should().NotBeNull();
        config.LastModified.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenFileExists_ReturnsDeserializedConfiguration()
    {
        // Arrange
        var expected = new FirecrackerRunnerConfiguration
        {
            LastModified = DateTime.UtcNow,
            Defaults = new FirecrackerDefaults
            {
                DefaultVcpus = 4,
                DefaultMemMib = 4096,
                KernelPath = "/boot/vmlinux",
                BaseRootfsPath = "/images/rootfs.ext4",
                NetworkSubnet = "10.0.0.0/24",
                WorkDir = "/tmp/firecracker"
            },
            Registrations = new List<FirecrackerRunnerRegistration>
            {
                new()
                {
                    Id = "test-id-1",
                    Name = "test-runner",
                    Token = "ghp_abc123",
                    Owner = "pksorensen",
                    Project = "pks-cli",
                    Server = "https://api.github.com",
                    RegisteredAt = DateTime.UtcNow
                }
            }
        };

        var json = JsonSerializer.Serialize(expected, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(_configFilePath, json);

        // Act
        var config = await _service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Defaults.DefaultVcpus.Should().Be(4);
        config.Defaults.DefaultMemMib.Should().Be(4096);
        config.Registrations.Should().HaveCount(1);
        config.Registrations[0].Id.Should().Be("test-id-1");
        config.Registrations[0].Name.Should().Be("test-runner");
        config.Registrations[0].Token.Should().Be("ghp_abc123");
        config.Registrations[0].Owner.Should().Be("pksorensen");
        config.Registrations[0].Project.Should().Be("pks-cli");
        config.Registrations[0].Server.Should().Be("https://api.github.com");
    }

    [Fact]
    public async Task LoadAsync_WhenFileContainsInvalidJson_ReturnsDefaults()
    {
        // Arrange
        await File.WriteAllTextAsync(_configFilePath, "{ this is not valid json !!! }");

        // Act
        var config = await _service.LoadAsync();

        // Assert
        config.Should().NotBeNull();
        config.Registrations.Should().BeEmpty();
        config.Defaults.Should().NotBeNull();
        config.LastModified.Should().BeNull();
    }

    #endregion

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_WritesConfigurationToFile()
    {
        // Arrange
        var config = new FirecrackerRunnerConfiguration
        {
            Defaults = new FirecrackerDefaults
            {
                DefaultVcpus = 8,
                DefaultMemMib = 8192
            },
            Registrations = new List<FirecrackerRunnerRegistration>
            {
                new()
                {
                    Id = "save-test",
                    Name = "save-runner",
                    Token = "tok_save",
                    Owner = "testowner",
                    Project = "testrepo",
                    Server = "https://api.github.com"
                }
            }
        };

        // Act
        await _service.SaveAsync(config);

        // Assert
        File.Exists(_configFilePath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(_configFilePath);
        var loaded = JsonSerializer.Deserialize<FirecrackerRunnerConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        loaded.Should().NotBeNull();
        loaded!.Defaults.DefaultVcpus.Should().Be(8);
        loaded.Defaults.DefaultMemMib.Should().Be(8192);
        loaded.LastModified.Should().NotBeNull("SaveAsync should set LastModified");
        loaded.Registrations.Should().HaveCount(1);
        loaded.Registrations[0].Id.Should().Be("save-test");
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var nestedPath = Path.Combine(_testDirectory, "nested", "dir", "firecracker-runners.json");
        var service = new FirecrackerRunnerConfigurationService(_mockLogger.Object, nestedPath);
        var config = new FirecrackerRunnerConfiguration();

        // Act
        await service.SaveAsync(config);

        // Assert
        File.Exists(nestedPath).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_SetsLastModified()
    {
        // Arrange
        var config = new FirecrackerRunnerConfiguration();
        config.LastModified.Should().BeNull("LastModified should be null before saving");

        // Act
        await _service.SaveAsync(config);

        // Assert
        var loaded = await _service.LoadAsync();
        loaded.LastModified.Should().NotBeNull();
        loaded.LastModified.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    #endregion

    #region AddRegistrationAsync

    [Fact]
    public async Task AddRegistrationAsync_AddsNewRegistration_AndSaves()
    {
        // Arrange
        var registration = new FirecrackerRunnerRegistration
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "my-runner",
            Token = "ghp_test123",
            Owner = "pksorensen",
            Project = "pks-cli",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.AddRegistrationAsync(registration);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(registration.Id);
        result.Owner.Should().Be("pksorensen");
        result.Project.Should().Be("pks-cli");
        result.Name.Should().Be("my-runner");
        result.Token.Should().Be("ghp_test123");
        result.RegisteredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        // Verify persisted
        File.Exists(_configFilePath).Should().BeTrue();
        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(1);
        loaded.Registrations[0].Id.Should().Be(registration.Id);
    }

    [Fact]
    public async Task AddRegistrationAsync_UpsertsExistingByOwnerProject()
    {
        // Arrange
        var original = new FirecrackerRunnerRegistration
        {
            Id = "original-id",
            Name = "original-runner",
            Token = "ghp_original",
            Owner = "pksorensen",
            Project = "pks-cli",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow.AddDays(-1)
        };
        await _service.AddRegistrationAsync(original);

        var updated = new FirecrackerRunnerRegistration
        {
            Id = "updated-id",
            Name = "updated-runner",
            Token = "ghp_updated",
            Owner = "pksorensen",
            Project = "pks-cli",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        };

        // Act
        var result = await _service.AddRegistrationAsync(updated);

        // Assert
        result.Id.Should().Be("updated-id");

        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(1, "upsert should replace, not add a duplicate");
        loaded.Registrations[0].Id.Should().Be("updated-id");
        loaded.Registrations[0].Name.Should().Be("updated-runner");
        loaded.Registrations[0].Token.Should().Be("ghp_updated");
    }

    #endregion

    #region ListRegistrationsAsync

    [Fact]
    public async Task ListRegistrationsAsync_ReturnsAllRegistrations()
    {
        // Arrange
        await _service.AddRegistrationAsync(new FirecrackerRunnerRegistration
        {
            Id = "reg-1",
            Name = "runner-1",
            Token = "tok1",
            Owner = "owner1",
            Project = "project1",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        });
        await _service.AddRegistrationAsync(new FirecrackerRunnerRegistration
        {
            Id = "reg-2",
            Name = "runner-2",
            Token = "tok2",
            Owner = "owner2",
            Project = "project2",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        });
        await _service.AddRegistrationAsync(new FirecrackerRunnerRegistration
        {
            Id = "reg-3",
            Name = "runner-3",
            Token = "tok3",
            Owner = "owner3",
            Project = "project3",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        });

        // Act
        var registrations = await _service.ListRegistrationsAsync();

        // Assert
        registrations.Should().HaveCount(3);
        registrations.Select(r => r.Owner).Should().BeEquivalentTo("owner1", "owner2", "owner3");
    }

    #endregion

    #region GetRegistrationAsync

    [Fact]
    public async Task GetRegistrationAsync_ReturnsMatchingRegistration()
    {
        // Arrange
        var target = new FirecrackerRunnerRegistration
        {
            Id = "target-id",
            Name = "target-runner",
            Token = "tok_target",
            Owner = "targetowner",
            Project = "targetproject",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        };
        await _service.AddRegistrationAsync(target);
        await _service.AddRegistrationAsync(new FirecrackerRunnerRegistration
        {
            Id = "other-id",
            Name = "other-runner",
            Token = "tok_other",
            Owner = "other",
            Project = "other",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        });

        // Act
        var result = await _service.GetRegistrationAsync("target-id");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("target-id");
        result.Owner.Should().Be("targetowner");
        result.Project.Should().Be("targetproject");
    }

    [Fact]
    public async Task GetRegistrationAsync_WhenNotFound_ReturnsNull()
    {
        // Arrange
        await _service.AddRegistrationAsync(new FirecrackerRunnerRegistration
        {
            Id = "existing-id",
            Name = "runner",
            Token = "tok",
            Owner = "owner",
            Project = "project",
            Server = "https://api.github.com",
            RegisteredAt = DateTime.UtcNow
        });

        // Act
        var result = await _service.GetRegistrationAsync("does-not-exist");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in test disposal
        }

        base.Dispose();
    }
}
