using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Coolify;

/// <summary>
/// Tests for the CoolifyStatusCommand which displays health status
/// of all registered Coolify instances, their projects, and resources.
/// Written TDD-first before the command implementation.
/// </summary>
public class CoolifyStatusCommandTests
{
    private readonly TestConsole _console;
    private readonly Mock<ICoolifyConfigurationService> _mockConfigService;
    private readonly Mock<ICoolifyApiService> _mockApiService;

    public CoolifyStatusCommandTests()
    {
        _console = new TestConsole();
        _mockConfigService = new Mock<ICoolifyConfigurationService>();
        _mockApiService = new Mock<ICoolifyApiService>();
    }

    #region No Instances

    [Fact]
    public void StatusCommand_WhenNoInstances_ShowsWarning()
    {
        // Arrange
        _mockConfigService
            .Setup(x => x.ListInstancesAsync())
            .ReturnsAsync(new List<CoolifyInstance>());

        var command = new PKS.Commands.Coolify.CoolifyStatusCommand(
            _mockConfigService.Object,
            _mockApiService.Object,
            _console);

        var context = new CommandContextBuilder().Build();

        // Act
        var result = command.Execute(context, new PKS.Commands.Coolify.CoolifySettings());

        // Assert
        result.Should().Be(0);

        var output = _console.Output;
        output.Should().Contain("No Coolify instances registered");
        output.Should().Contain("pks coolify register");

        // Should never call the API service
        _mockApiService.Verify(
            x => x.TestConnectionAsync(It.IsAny<CoolifyInstance>()),
            Times.Never);
        _mockApiService.Verify(
            x => x.GetProjectsWithResourcesAsync(It.IsAny<CoolifyInstance>()),
            Times.Never);
    }

    #endregion

    #region Connection Failure

    [Fact]
    public void StatusCommand_WhenConnectionFails_ShowsError()
    {
        // Arrange
        var instance = new CoolifyInstance
        {
            Id = "test-id-1",
            Url = "https://coolify.example.com",
            Token = "test-token",
            RegisteredAt = DateTime.UtcNow
        };

        _mockConfigService
            .Setup(x => x.ListInstancesAsync())
            .ReturnsAsync(new List<CoolifyInstance> { instance });

        _mockApiService
            .Setup(x => x.TestConnectionAsync(It.Is<CoolifyInstance>(i => i.Id == "test-id-1")))
            .ReturnsAsync(new CoolifyConnectionResult
            {
                Success = false,
                Error = "Connection refused"
            });

        var command = new PKS.Commands.Coolify.CoolifyStatusCommand(
            _mockConfigService.Object,
            _mockApiService.Object,
            _console);

        var context = new CommandContextBuilder().Build();

        // Act
        var result = command.Execute(context, new PKS.Commands.Coolify.CoolifySettings());

        // Assert
        result.Should().Be(0, "command should still succeed even if an instance is unreachable");

        var output = _console.Output;
        output.Should().Contain("coolify.example.com");
        output.Should().Contain("Connection refused");

        // Should NOT attempt to fetch projects for a failed connection
        _mockApiService.Verify(
            x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "test-id-1")),
            Times.Never);
    }

    #endregion

    #region Happy Path

    [Fact]
    public void StatusCommand_WhenConnected_ShowsProjectsAndResources()
    {
        // Arrange
        var instance = new CoolifyInstance
        {
            Id = "prod-1",
            Url = "https://coolify.prod.example.com",
            Token = "prod-token",
            RegisteredAt = DateTime.UtcNow
        };

        _mockConfigService
            .Setup(x => x.ListInstancesAsync())
            .ReturnsAsync(new List<CoolifyInstance> { instance });

        _mockApiService
            .Setup(x => x.TestConnectionAsync(It.Is<CoolifyInstance>(i => i.Id == "prod-1")))
            .ReturnsAsync(new CoolifyConnectionResult
            {
                Success = true,
                Version = "4.0.0"
            });

        var projects = new List<CoolifyProject>
        {
            new()
            {
                Id = 1,
                Name = "Frontend",
                Description = "Main frontend app",
                Uuid = "proj-uuid-1",
                Resources = new List<CoolifyResource>
                {
                    new()
                    {
                        Uuid = "res-1",
                        Name = "web-app",
                        Type = "application",
                        Status = "running",
                        Fqdn = "https://app.example.com"
                    },
                    new()
                    {
                        Uuid = "res-2",
                        Name = "api-service",
                        Type = "service",
                        Status = "running",
                        Fqdn = "https://api.example.com"
                    }
                }
            },
            new()
            {
                Id = 2,
                Name = "Backend",
                Description = "Backend services",
                Uuid = "proj-uuid-2",
                Resources = new List<CoolifyResource>
                {
                    new()
                    {
                        Uuid = "res-3",
                        Name = "postgres-db",
                        Type = "database",
                        Status = "running"
                    },
                    new()
                    {
                        Uuid = "res-4",
                        Name = "worker",
                        Type = "application",
                        Status = "stopped"
                    }
                }
            }
        };

        _mockApiService
            .Setup(x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "prod-1")))
            .ReturnsAsync(projects);

        var command = new PKS.Commands.Coolify.CoolifyStatusCommand(
            _mockConfigService.Object,
            _mockApiService.Object,
            _console);

        var context = new CommandContextBuilder().Build();

        // Act
        var result = command.Execute(context, new PKS.Commands.Coolify.CoolifySettings());

        // Assert
        result.Should().Be(0);

        var output = _console.Output;

        // Should show instance info and version
        output.Should().Contain("coolify.prod.example.com");
        output.Should().Contain("4.0.0");

        // Should show project names
        output.Should().Contain("Frontend");
        output.Should().Contain("Backend");

        // Should show resource names
        output.Should().Contain("web-app");
        output.Should().Contain("api-service");
        output.Should().Contain("postgres-db");
        output.Should().Contain("worker");

        // Should show resource statuses
        output.Should().Contain("running");
        output.Should().Contain("stopped");

        // Verify API calls were made correctly
        _mockApiService.Verify(
            x => x.TestConnectionAsync(It.Is<CoolifyInstance>(i => i.Id == "prod-1")),
            Times.Once);
        _mockApiService.Verify(
            x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "prod-1")),
            Times.Once);
    }

    #endregion

    #region Multiple Instances

    [Fact]
    public void StatusCommand_WithMultipleInstances_QueriesEachInstance()
    {
        // Arrange
        var instance1 = new CoolifyInstance
        {
            Id = "inst-1",
            Url = "https://coolify1.example.com",
            Token = "token-1",
            RegisteredAt = DateTime.UtcNow
        };
        var instance2 = new CoolifyInstance
        {
            Id = "inst-2",
            Url = "https://coolify2.example.com",
            Token = "token-2",
            RegisteredAt = DateTime.UtcNow
        };

        _mockConfigService
            .Setup(x => x.ListInstancesAsync())
            .ReturnsAsync(new List<CoolifyInstance> { instance1, instance2 });

        // First instance succeeds
        _mockApiService
            .Setup(x => x.TestConnectionAsync(It.Is<CoolifyInstance>(i => i.Id == "inst-1")))
            .ReturnsAsync(new CoolifyConnectionResult { Success = true, Version = "4.0.0" });
        _mockApiService
            .Setup(x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "inst-1")))
            .ReturnsAsync(new List<CoolifyProject>
            {
                new() { Id = 1, Name = "ProjectA", Uuid = "a", Resources = new List<CoolifyResource>() }
            });

        // Second instance fails
        _mockApiService
            .Setup(x => x.TestConnectionAsync(It.Is<CoolifyInstance>(i => i.Id == "inst-2")))
            .ReturnsAsync(new CoolifyConnectionResult { Success = false, Error = "Timeout" });

        var command = new PKS.Commands.Coolify.CoolifyStatusCommand(
            _mockConfigService.Object,
            _mockApiService.Object,
            _console);

        var context = new CommandContextBuilder().Build();

        // Act
        var result = command.Execute(context, new PKS.Commands.Coolify.CoolifySettings());

        // Assert
        result.Should().Be(0);

        var output = _console.Output;
        output.Should().Contain("coolify1.example.com");
        output.Should().Contain("coolify2.example.com");
        output.Should().Contain("ProjectA");
        output.Should().Contain("Timeout");

        // Verify both instances were queried
        _mockApiService.Verify(
            x => x.TestConnectionAsync(It.IsAny<CoolifyInstance>()),
            Times.Exactly(2));

        // Only the successful instance should have projects fetched
        _mockApiService.Verify(
            x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "inst-1")),
            Times.Once);
        _mockApiService.Verify(
            x => x.GetProjectsWithResourcesAsync(It.Is<CoolifyInstance>(i => i.Id == "inst-2")),
            Times.Never);
    }

    #endregion

    /// <summary>
    /// Helper to build a CommandContext for testing Spectre.Console commands.
    /// </summary>
    private class CommandContextBuilder
    {
        public Spectre.Console.Cli.CommandContext Build()
        {
            // Create a minimal CommandContext using reflection or the available constructor.
            // Spectre.Console.Cli.CommandContext requires IRemainingArguments and name.
            var remaining = new Mock<Spectre.Console.Cli.IRemainingArguments>();
            remaining.Setup(x => x.Parsed).Returns(new Mock<ILookup<string, string?>>().Object);
            remaining.Setup(x => x.Raw).Returns(new List<string>());

            return new Spectre.Console.Cli.CommandContext(remaining.Object, "status", null);
        }
    }
}
