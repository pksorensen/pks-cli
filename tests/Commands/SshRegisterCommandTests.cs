using FluentAssertions;
using Moq;
using PKS.Commands.Ssh;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class SshRegisterCommandTests
{
    private readonly Mock<ISshTargetConfigurationService> _mockConfigService;
    private readonly TestConsole _testConsole;

    public SshRegisterCommandTests()
    {
        _mockConfigService = new Mock<ISshTargetConfigurationService>();
        _testConsole = new TestConsole();
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_ValidUserAtHost_RegistersTarget()
    {
        // Arrange
        var keyPath = Path.GetTempFileName(); // Creates a real temp file
        try
        {
            _mockConfigService
                .Setup(x => x.AddTargetAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new SshTarget
                {
                    Host = "projects.si14agents.com",
                    Username = "root",
                    Port = 22,
                    KeyPath = keyPath
                });

            var command = new SshRegisterCommand(_mockConfigService.Object, _testConsole);
            var settings = new SshRegisterCommand.Settings
            {
                Target = "root@projects.si14agents.com",
                KeyPath = keyPath,
                Port = 22
            };

            // Act
            var result = command.Execute(null!, settings);

            // Assert
            result.Should().Be(0);
            _mockConfigService.Verify(x => x.AddTargetAsync(
                "projects.si14agents.com", "root", 22, keyPath, null), Times.Once);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_MissingKeyFile_ReturnsError()
    {
        var command = new SshRegisterCommand(_mockConfigService.Object, _testConsole);
        var settings = new SshRegisterCommand.Settings
        {
            Target = "root@myhost.com",
            KeyPath = "/nonexistent/path/id_rsa",
            Port = 22
        };

        var result = command.Execute(null!, settings);

        result.Should().Be(1);
        _mockConfigService.Verify(x => x.AddTargetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_InvalidTargetFormat_ReturnsError()
    {
        var command = new SshRegisterCommand(_mockConfigService.Object, _testConsole);
        var settings = new SshRegisterCommand.Settings
        {
            Target = "just-a-host",
            KeyPath = "/some/key",
            Port = 22
        };

        var result = command.Execute(null!, settings);

        result.Should().Be(1);
        _mockConfigService.Verify(x => x.AddTargetAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_WithPortAndLabel_PassesToService()
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            _mockConfigService
                .Setup(x => x.AddTargetAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(),
                    It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new SshTarget
                {
                    Host = "myhost.com",
                    Username = "deploy",
                    Port = 2222,
                    KeyPath = keyPath,
                    Label = "staging"
                });

            var command = new SshRegisterCommand(_mockConfigService.Object, _testConsole);
            var settings = new SshRegisterCommand.Settings
            {
                Target = "deploy@myhost.com",
                KeyPath = keyPath,
                Port = 2222,
                Label = "staging"
            };

            var result = command.Execute(null!, settings);

            result.Should().Be(0);
            _mockConfigService.Verify(x => x.AddTargetAsync(
                "myhost.com", "deploy", 2222, keyPath, "staging"), Times.Once);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }
}
