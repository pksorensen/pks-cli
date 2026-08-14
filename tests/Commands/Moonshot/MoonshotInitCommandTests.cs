using FluentAssertions;
using Moq;
using PKS.Commands.Moonshot;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Moonshot;

public class MoonshotInitCommandTests
{
    [Fact]
    public async Task ExecuteAsync_validates_guards_and_stores_the_secret_key()
    {
        var moonshot = new Mock<IMoonshotService>();
        moonshot.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        moonshot.Setup(x => x.ValidateApiKeyAsync("secret-value", default)).ReturnsAsync(true);
        var guard = new Mock<IActionGuard>();
        guard.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), default)).Returns(Task.CompletedTask);
        var console = new TestConsole();
        console.Input.PushTextWithEnter("secret-value");
        var command = new MoonshotInitCommand(moonshot.Object, guard.Object, console);

        var exitCode = await command.ExecuteAsync(null!, new MoonshotInitCommand.Settings());

        exitCode.Should().Be(0);
        guard.Verify(x => x.RequireAsync(
            It.Is<ActionRequest>(request => request.ActionId == ActionIds.CloudAuthWrite),
            default));
        moonshot.Verify(x => x.StoreCredentialsAsync(
            It.Is<MoonshotStoredCredentials>(credentials => credentials.ApiKey == SecretValue.From("secret-value"))));
        console.Output.Should().NotContain("secret-value");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_store_a_rejected_key()
    {
        var moonshot = new Mock<IMoonshotService>();
        moonshot.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);
        moonshot.Setup(x => x.ValidateApiKeyAsync("bad-key", default)).ReturnsAsync(false);
        var console = new TestConsole();
        console.Input.PushTextWithEnter("bad-key");
        var command = new MoonshotInitCommand(moonshot.Object, Mock.Of<IActionGuard>(), console);

        var exitCode = await command.ExecuteAsync(null!, new MoonshotInitCommand.Settings());

        exitCode.Should().Be(1);
        moonshot.Verify(x => x.StoreCredentialsAsync(It.IsAny<MoonshotStoredCredentials>()), Times.Never);
    }
}
