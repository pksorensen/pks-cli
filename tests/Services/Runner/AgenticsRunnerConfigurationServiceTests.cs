using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Tests for <see cref="AgenticsRunnerConfigurationService"/> covering the Phase 3 hardening of
/// docs/remote-runner-targets-plan.md: backward-compat round-tripping of an old
/// agentics-runners.json with no "profile" key once <see cref="RunnerProfile"/> is introduced, and
/// the corrupt-JSON recovery path (rename to a timestamped .bak file instead of silently discarding
/// every registration).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerConfigurationServiceTests : TestBase
{
    private readonly Mock<ILogger<AgenticsRunnerConfigurationService>> _mockLogger;
    private readonly string _testDirectory;
    private readonly string _configFilePath;
    private readonly AgenticsRunnerConfigurationService _service;

    public AgenticsRunnerConfigurationServiceTests()
    {
        _mockLogger = new Mock<ILogger<AgenticsRunnerConfigurationService>>();
        _testDirectory = CreateTempDirectory();
        _configFilePath = Path.Combine(_testDirectory, "agentics-runners.json");
        _service = new AgenticsRunnerConfigurationService(_mockLogger.Object, _configFilePath);
    }

    [Fact]
    public async Task LoadAsync_OldFileWithNoProfileKey_RoundTripsCleanly_AndGainsProfileOnSave()
    {
        // Arrange -- a file written before RunnerProfile existed: no "profile" property at all on
        // the registration, matching every agentics-runners.json on disk before Phase 3 shipped.
        var legacyJson = """
        {
          "registrations": [
            {
              "id": "runner-1",
              "name": "old-runner",
              "token": "tok-abc",
              "owner": "acme",
              "project": "widgets",
              "server": "https://agentics.test",
              "registeredAt": "2026-01-01T00:00:00Z"
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(_configFilePath, legacyJson);

        // Act
        var loaded = await _service.LoadAsync();

        // Assert -- loads cleanly, profile is absent (null), everything else intact.
        loaded.Registrations.Should().HaveCount(1);
        var registration = loaded.Registrations[0];
        registration.Id.Should().Be("runner-1");
        registration.Name.Should().Be("old-runner");
        registration.Token.Should().Be("tok-abc");
        registration.Owner.Should().Be("acme");
        registration.Project.Should().Be("widgets");
        registration.Profile.Should().BeNull("an old file with no \"profile\" key means never-configured, not an empty profile");

        // Act -- now set a profile (simulating the interactive configure flow) and save + reload.
        registration.Profile = new RunnerProfile
        {
            Capabilities = new List<string> { "chat-llm:v1" },
            ChatModels = new List<string> { "gpt-5.5" },
            DefaultChatModel = "gpt-5.5",
            ConfiguredAt = DateTime.UtcNow,
        };
        await _service.AddRegistrationAsync(registration);

        var reloaded = await _service.LoadAsync();
        reloaded.Registrations.Should().HaveCount(1);
        reloaded.Registrations[0].Profile.Should().NotBeNull();
        reloaded.Registrations[0].Profile!.ChatModels.Should().BeEquivalentTo(new[] { "gpt-5.5" });
    }

    [Fact]
    public async Task SaveAsync_RegistrationWithNullProfile_OmitsProfileKeyFromDisk()
    {
        // Arrange -- WhenWritingNull means a null Profile must be entirely absent from the
        // serialized JSON, not written as "profile": null.
        var registration = new AgenticsRunnerRegistration
        {
            Id = "runner-2",
            Name = "unconfigured-runner",
            Token = "tok",
            Owner = "acme",
            Project = "widgets",
            Server = "https://agentics.test",
            RegisteredAt = DateTime.UtcNow,
            Profile = null,
        };

        // Act
        await _service.AddRegistrationAsync(registration);

        // Assert
        var raw = await File.ReadAllTextAsync(_configFilePath);
        raw.Should().NotContain("profile", "a null profile must be entirely absent, not written as \"profile\": null");
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_MovesFileToTimestampedBackup_AndReturnsDefaults_WithoutThrowing()
    {
        // Arrange -- unparseable JSON (truncated / hand-edited / disk corruption).
        await File.WriteAllTextAsync(_configFilePath, "{ this is not valid json ");

        // Act
        Func<Task<AgenticsRunnerConfiguration>> act = () => _service.LoadAsync();

        // Assert -- must not throw, must return a fresh empty default.
        var config = await act.Should().NotThrowAsync();
        config.Subject.Registrations.Should().BeEmpty();

        // The corrupt file must have been preserved under a .bak-<timestamp> name rather than
        // silently left in place to be clobbered by the next SaveAsync -- and the original path no
        // longer holds the corrupt content.
        var directory = Path.GetDirectoryName(_configFilePath)!;
        var backups = Directory.GetFiles(directory, "agentics-runners.json.bak-*");
        backups.Should().ContainSingle("the corrupt file should be renamed exactly once, not duplicated or left in place");

        var backupContent = await File.ReadAllTextAsync(backups[0]);
        backupContent.Should().Be("{ this is not valid json ");

        File.Exists(_configFilePath).Should().BeFalse("the corrupt file was moved, not copied -- nothing should remain at the original path until the next SaveAsync");
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_SubsequentSaveWritesFreshFile_BackupUntouched()
    {
        // Arrange
        await File.WriteAllTextAsync(_configFilePath, "not json at all");
        await _service.LoadAsync(); // triggers the backup rename

        // Act -- register normally after the corrupt load; this must not throw or resurrect the
        // corrupt content.
        var registration = await _service.AddRegistrationAsync(new AgenticsRunnerRegistration
        {
            Id = "runner-3",
            Name = "fresh-after-corruption",
            Token = "tok",
            Owner = "acme",
            Project = "widgets",
            Server = "https://agentics.test",
            RegisteredAt = DateTime.UtcNow,
        });

        // Assert
        var reloaded = await _service.LoadAsync();
        reloaded.Registrations.Should().ContainSingle(r => r.Id == registration.Id);

        var directory = Path.GetDirectoryName(_configFilePath)!;
        Directory.GetFiles(directory, "agentics-runners.json.bak-*").Should().ContainSingle(
            "the earlier corrupt-load backup must still be there, untouched by the fresh save");
    }

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
