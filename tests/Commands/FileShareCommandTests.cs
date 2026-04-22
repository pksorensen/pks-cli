using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.FileShares;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;

namespace PKS.CLI.Tests.Commands;

public class FileShareCommandTests
{
    private static Mock<IFileShareProvider> CreateProviderMock(
        string key = "azure-fileshare",
        string name = "Azure File Share",
        bool authenticated = false)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns(key);
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        mock.Setup(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageResource>());
        return mock;
    }

    private static FileShareProviderRegistry CreateRegistry(params IFileShareProvider[] providers)
        => new FileShareProviderRegistry(providers);

    // ═══════════════════════════════════════
    //  FileShareStatusCommand
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ShowsNoProviders_WhenRegistryEmpty()
    {
        var console = new TestConsole();
        var registry = CreateRegistry();
        var cmd = new FileShareStatusCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "status", null);

        var result = cmd.Execute(ctx, new FileShareSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("No file share providers registered");
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Status_ShowsAuthenticatedProvider()
    {
        var console = new TestConsole();
        var provider = CreateProviderMock(authenticated: true);
        provider.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageResource>
            {
                new() { ProviderKey = "azure-fileshare", ProviderName = "Azure File Share", AccountName = "mystorage", ResourceName = "myshare" }
            });

        var registry = CreateRegistry(provider.Object);
        var console2 = new TestConsole();
        var cmd = new FileShareStatusCommand(registry, console2);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "status", null);

        var result = cmd.Execute(ctx, new FileShareSettings());

        result.Should().Be(0);
        console2.Output.Should().Contain("Azure File Share");
    }

    // ═══════════════════════════════════════
    //  FileShareInitCommand
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_ShortCircuits_WhenAlreadyAuthenticated()
    {
        var console = new TestConsole();
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var cmd = new FileShareInitCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);

        var result = cmd.Execute(ctx, new FileShareInitCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("Already authenticated");
        provider.Verify(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_WithForce_ReauthenticatesEvenIfAuthenticated()
    {
        var console = new TestConsole();
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var cmd = new FileShareInitCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);

        var result = cmd.Execute(ctx, new FileShareInitCommand.Settings { Force = true });

        result.Should().Be(0);
        provider.Verify(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_Returns1_WhenAuthenticationFails()
    {
        var console = new TestConsole();
        var provider = CreateProviderMock(authenticated: false);
        provider.Setup(p => p.AuthenticateAsync(It.IsAny<IAnsiConsole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var registry = CreateRegistry(provider.Object);
        var cmd = new FileShareInitCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);

        var result = cmd.Execute(ctx, new FileShareInitCommand.Settings());

        result.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "FileShare")]
    public void Init_Returns1_WhenNoProviders()
    {
        var console = new TestConsole();
        var registry = CreateRegistry();
        var cmd = new FileShareInitCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "init", null);

        var result = cmd.Execute(ctx, new FileShareInitCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().Contain("No file share providers");
    }
}
