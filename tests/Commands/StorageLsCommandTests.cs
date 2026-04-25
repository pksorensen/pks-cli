using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Storage;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;

namespace PKS.CLI.Tests.Commands;

public class StorageLsCommandTests
{
    // ─────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────

    private static Mock<IFileShareProvider> CreateProviderMock(
        bool authenticated = true,
        StorageListResult? listResult = null)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns("azure-fileshare");
        mock.Setup(p => p.ProviderName).Returns("Azure File Share");
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        mock.Setup(p => p.ListDirectoryAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(listResult ?? DefaultListResult());
        return mock;
    }

    private static StorageListResult DefaultListResult() => new()
    {
        ShareName = "myshare",
        Path = "/",
        Items = new List<StorageListItem>
        {
            new() { Name = "configs",  Type = StorageItemType.Directory },
            new() { Name = "users",    Type = StorageItemType.Directory },
            new() { Name = "app.json", Type = StorageItemType.File, SizeBytes = 1024 }
        }
    };

    private static FileShareProviderRegistry CreateRegistry(params IFileShareProvider[] providers)
        => new FileShareProviderRegistry(providers);

    // ─────────────────────────────────────────────────────────────
    //  Tests
    // ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_Returns1_WhenNoProvidersAuthenticated()
    {
        var provider = CreateProviderMock(authenticated: false);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings());

        result.Should().Be(1);
        console.Output.Should().Contain("No authenticated storage providers");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_ListsRootDirectory()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings());

        result.Should().Be(0);
        console.Output.Should().Contain("configs");
        console.Output.Should().Contain("users");
        console.Output.Should().Contain("app.json");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_ShowsTruncatedWarning_WhenResultTruncated()
    {
        var truncatedResult = DefaultListResult();
        truncatedResult.Truncated = true;

        var provider = CreateProviderMock(authenticated: true, listResult: truncatedResult);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings());

        console.Output.ToLowerInvariant().Should().Contain("truncated");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_OutputsJson_WhenJsonFlagSet()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { Json = true });

        console.Output.Should().Contain("\"items\"");
        console.Output.Should().Contain("\"path\"");
        // Should be raw JSON — no Spectre markup angle brackets
        console.Output.Should().NotContain("[bold]");
        console.Output.Should().NotContain("[/]");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_PassesLimitToProvider()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { Limit = 10 });

        provider.Verify(p => p.ListDirectoryAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<StorageListRequest>(r => r.Limit == 10),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_PassesCountFlagToProvider()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { IncludeCount = true });

        provider.Verify(p => p.ListDirectoryAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<StorageListRequest>(r => r.IncludeCount == true),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_PassesDirsOnlyToProvider()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { DirsOnly = true });

        provider.Verify(p => p.ListDirectoryAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<StorageListRequest>(r => r.DirsOnly == true),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_ShowsItemCount_WhenCountIncluded()
    {
        var resultWithCount = new StorageListResult
        {
            ShareName = "myshare",
            Path = "/",
            Items = new List<StorageListItem>
            {
                new() { Name = "configs", Type = StorageItemType.Directory, ItemCount = 42 }
            }
        };

        var provider = CreateProviderMock(authenticated: true, listResult: resultWithCount);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { IncludeCount = true });

        console.Output.Should().Contain("42");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_PassesPathArgToProvider()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageLsCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        cmd.Execute(ctx, new StorageLsCommand.Settings { Path = "/users" });

        provider.Verify(p => p.ListDirectoryAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.Is<StorageListRequest>(r => r.Path == "/users"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
