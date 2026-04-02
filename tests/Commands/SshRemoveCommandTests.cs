using FluentAssertions;
using Moq;
using PKS.Commands.Ssh;
using PKS.Infrastructure.Services;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

public class SshRemoveCommandTests
{
    private readonly Mock<ISshTargetConfigurationService> _mockConfigService;
    private readonly TestConsole _testConsole;

    public SshRemoveCommandTests()
    {
        _mockConfigService = new Mock<ISshTargetConfigurationService>();
        _testConsole = new TestConsole();
    }

    [Fact]
    [Trait("Category", "Core")]
    public void Execute_NonExistentTarget_DisplaysError()
    {
        _mockConfigService
            .Setup(x => x.FindTargetAsync(It.IsAny<string>()))
            .ReturnsAsync((SshTarget?)null);

        var command = new SshRemoveCommand(_mockConfigService.Object, _testConsole);
        var settings = new SshRemoveCommand.Settings
        {
            Target = "nonexistent.host"
        };

        var result = command.Execute(null!, settings);

        result.Should().Be(1);
        _testConsole.Output.Should().Contain("not found");
    }
}
