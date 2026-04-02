using FluentAssertions;
using Moq;
using PKS.Commands.Ssh;
using PKS.Infrastructure.Services;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class SshListCommandTests
{
    private readonly Mock<ISshTargetConfigurationService> _mockConfigService;
    private readonly TestConsole _testConsole;

    public SshListCommandTests()
    {
        _mockConfigService = new Mock<ISshTargetConfigurationService>();
        _testConsole = new TestConsole();
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_NoTargets_DisplaysEmptyMessage()
    {
        _mockConfigService
            .Setup(x => x.ListTargetsAsync())
            .ReturnsAsync(new List<SshTarget>());

        var command = new SshListCommand(_mockConfigService.Object, _testConsole);
        var settings = new SshSettings();

        var result = command.Execute(null!, settings);

        result.Should().Be(0);
        _testConsole.Output.Should().Contain("No SSH targets registered");
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_WithTargets_DisplaysTable()
    {
        _mockConfigService
            .Setup(x => x.ListTargetsAsync())
            .ReturnsAsync(new List<SshTarget>
            {
                new SshTarget
                {
                    Host = "server1.com",
                    Username = "root",
                    Port = 22,
                    KeyPath = "/home/user/.ssh/id_rsa",
                    Label = "prod"
                },
                new SshTarget
                {
                    Host = "server2.com",
                    Username = "deploy",
                    Port = 2222,
                    KeyPath = "/home/user/.ssh/id_ed25519"
                }
            });

        var command = new SshListCommand(_mockConfigService.Object, _testConsole);
        var settings = new SshSettings();

        var result = command.Execute(null!, settings);

        result.Should().Be(0);
        _testConsole.Output.Should().Contain("server1.com");
        _testConsole.Output.Should().Contain("server2.com");
        _testConsole.Output.Should().Contain("2 target(s) registered");
    }
}
