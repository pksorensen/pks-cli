using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Tests for RunnerConfigurationService covering load, save, and CRUD operations
/// on runner registrations. Uses temp directories for file isolation.
/// </summary>
public class RunnerConfigurationServiceTests : TestBase
{
    private readonly Mock<ILogger<RunnerConfigurationService>> _mockLogger;
    private readonly string _testDirectory;
    private readonly string _configFilePath;
    private readonly RunnerConfigurationService _service;

    public RunnerConfigurationServiceTests()
    {
        _mockLogger = new Mock<ILogger<RunnerConfigurationService>>();
        _testDirectory = CreateTempDirectory();
        _configFilePath = Path.Combine(_testDirectory, "runners.json");
        _service = new RunnerConfigurationService(_mockLogger.Object, _configFilePath);
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
        config.PollingIntervalSeconds.Should().Be(30);
        config.MaxConcurrentJobs.Should().Be(1);
        config.LastModified.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_WhenFileExists_ReturnsDeserializedConfiguration()
    {
        // Arrange
        var expected = new RunnerConfiguration
        {
            PollingIntervalSeconds = 60,
            MaxConcurrentJobs = 4,
            LastModified = DateTime.UtcNow,
            Registrations = new List<RunnerRegistration>
            {
                new()
                {
                    Id = "test-id-1",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    Labels = "self-hosted,linux",
                    RegisteredAt = DateTime.UtcNow,
                    Enabled = true
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
        config.PollingIntervalSeconds.Should().Be(60);
        config.MaxConcurrentJobs.Should().Be(4);
        config.Registrations.Should().HaveCount(1);
        config.Registrations[0].Id.Should().Be("test-id-1");
        config.Registrations[0].Owner.Should().Be("pksorensen");
        config.Registrations[0].Repository.Should().Be("pks-cli");
        config.Registrations[0].Labels.Should().Be("self-hosted,linux");
    }

    #endregion

    #region SaveAsync

    [Fact]
    public async Task SaveAsync_WritesConfigurationToFile()
    {
        // Arrange
        var config = new RunnerConfiguration
        {
            PollingIntervalSeconds = 45,
            MaxConcurrentJobs = 2,
            Registrations = new List<RunnerRegistration>
            {
                new()
                {
                    Id = "save-test",
                    Owner = "testowner",
                    Repository = "testrepo",
                    Labels = "custom-label"
                }
            }
        };

        // Act
        await _service.SaveAsync(config);

        // Assert
        File.Exists(_configFilePath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(_configFilePath);
        var loaded = JsonSerializer.Deserialize<RunnerConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        loaded.Should().NotBeNull();
        loaded!.PollingIntervalSeconds.Should().Be(45);
        loaded.MaxConcurrentJobs.Should().Be(2);
        loaded.LastModified.Should().NotBeNull("SaveAsync should set LastModified");
        loaded.Registrations.Should().HaveCount(1);
        loaded.Registrations[0].Id.Should().Be("save-test");
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var nestedPath = Path.Combine(_testDirectory, "nested", "dir", "runners.json");
        var service = new RunnerConfigurationService(_mockLogger.Object, nestedPath);
        var config = new RunnerConfiguration();

        // Act
        await service.SaveAsync(config);

        // Assert
        File.Exists(nestedPath).Should().BeTrue();
    }

    #endregion

    #region AddRegistrationAsync

    [Fact]
    public async Task AddRegistrationAsync_AddsNewRegistration_AndSaves()
    {
        // Act
        var registration = await _service.AddRegistrationAsync("pksorensen", "pks-cli");

        // Assert
        registration.Should().NotBeNull();
        registration.Id.Should().NotBeNullOrEmpty();
        registration.Owner.Should().Be("pksorensen");
        registration.Repository.Should().Be("pks-cli");
        registration.Labels.Should().Be("devcontainer-runner");
        registration.Enabled.Should().BeTrue();
        registration.RegisteredAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        // Verify persisted
        File.Exists(_configFilePath).Should().BeTrue();
        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(1);
        loaded.Registrations[0].Id.Should().Be(registration.Id);
    }

    [Fact]
    public async Task AddRegistrationAsync_WithCustomLabels_UsesProvidedLabels()
    {
        // Act
        var registration = await _service.AddRegistrationAsync("owner", "repo", "self-hosted,linux,x64");

        // Assert
        registration.Labels.Should().Be("self-hosted,linux,x64");

        var loaded = await _service.LoadAsync();
        loaded.Registrations[0].Labels.Should().Be("self-hosted,linux,x64");
    }

    #endregion

    #region RemoveRegistrationAsync

    [Fact]
    public async Task RemoveRegistrationAsync_RemovesExistingRegistration_ReturnsTrue()
    {
        // Arrange
        var registration = await _service.AddRegistrationAsync("owner", "repo");

        // Act
        var result = await _service.RemoveRegistrationAsync(registration.Id);

        // Assert
        result.Should().BeTrue();

        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveRegistrationAsync_WhenNotFound_ReturnsFalse()
    {
        // Act
        var result = await _service.RemoveRegistrationAsync("non-existent-id");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region ListRegistrationsAsync

    [Fact]
    public async Task ListRegistrationsAsync_ReturnsAllRegistrations()
    {
        // Arrange
        await _service.AddRegistrationAsync("owner1", "repo1");
        await _service.AddRegistrationAsync("owner2", "repo2");
        await _service.AddRegistrationAsync("owner3", "repo3");

        // Act
        var registrations = await _service.ListRegistrationsAsync();

        // Assert
        registrations.Should().HaveCount(3);
        registrations.Select(r => r.Owner).Should().BeEquivalentTo("owner1", "owner2", "owner3");
    }

    #endregion

    #region PruneRegistrationsAsync

    [Fact]
    public async Task PruneRegistrations_RemovesDuplicatesKeepingNewest()
    {
        // Arrange – 3 registrations for the same repo with different dates
        var config = new RunnerConfiguration
        {
            Registrations = new List<RunnerRegistration>
            {
                new()
                {
                    Id = "oldest",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "newest",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "middle",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        await _service.SaveAsync(config);

        // Act
        var removed = await _service.PruneRegistrationsAsync();

        // Assert
        removed.Should().HaveCount(2);
        removed.Select(r => r.Id).Should().BeEquivalentTo("oldest", "middle");

        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(1);
        loaded.Registrations[0].Id.Should().Be("newest");
    }

    [Fact]
    public async Task PruneRegistrations_NoOp_WhenNoDuplicates()
    {
        // Arrange – 2 registrations for different repos
        var config = new RunnerConfiguration
        {
            Registrations = new List<RunnerRegistration>
            {
                new()
                {
                    Id = "reg-1",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "reg-2",
                    Owner = "pksorensen",
                    Repository = "other-repo",
                    RegisteredAt = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        await _service.SaveAsync(config);

        // Act
        var removed = await _service.PruneRegistrationsAsync();

        // Assert
        removed.Should().BeEmpty();

        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(2);
    }

    [Fact]
    public async Task PruneRegistrations_HandlesMultipleReposWithDuplicates()
    {
        // Arrange – duplicates across 2 different repos
        var config = new RunnerConfiguration
        {
            Registrations = new List<RunnerRegistration>
            {
                new()
                {
                    Id = "repo1-old",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "repo1-new",
                    Owner = "pksorensen",
                    Repository = "pks-cli",
                    RegisteredAt = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "repo2-old",
                    Owner = "PKSorensen",
                    Repository = "Other-Repo",
                    RegisteredAt = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "repo2-new",
                    Owner = "pksorensen",
                    Repository = "other-repo",
                    RegisteredAt = new DateTime(2025, 2, 15, 0, 0, 0, DateTimeKind.Utc)
                },
                new()
                {
                    Id = "unique-repo",
                    Owner = "someowner",
                    Repository = "unique",
                    RegisteredAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }
            }
        };
        await _service.SaveAsync(config);

        // Act
        var removed = await _service.PruneRegistrationsAsync();

        // Assert – one old entry removed per duplicated repo
        removed.Should().HaveCount(2);
        removed.Select(r => r.Id).Should().BeEquivalentTo("repo1-old", "repo2-old");

        var loaded = await _service.LoadAsync();
        loaded.Registrations.Should().HaveCount(3);
        loaded.Registrations.Select(r => r.Id).Should()
            .BeEquivalentTo("repo1-new", "repo2-new", "unique-repo");
    }

    [Fact]
    public async Task PruneRegistrations_EmptyRegistrations_ReturnsEmpty()
    {
        // Arrange – no registrations at all (fresh default config)

        // Act
        var removed = await _service.PruneRegistrationsAsync();

        // Assert
        removed.Should().BeEmpty();
    }

    #endregion

    #region GetRegistrationAsync

    [Fact]
    public async Task GetRegistrationAsync_ReturnsMatchingRegistration()
    {
        // Arrange
        var added = await _service.AddRegistrationAsync("targetowner", "targetrepo");
        await _service.AddRegistrationAsync("other", "other");

        // Act
        var result = await _service.GetRegistrationAsync(added.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(added.Id);
        result.Owner.Should().Be("targetowner");
        result.Repository.Should().Be("targetrepo");
    }

    [Fact]
    public async Task GetRegistrationAsync_WhenNotFound_ReturnsNull()
    {
        // Arrange
        await _service.AddRegistrationAsync("owner", "repo");

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
