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

    // ─────────────────────────────────────────────────────────────
    //  Several storage accounts behind one provider
    // ─────────────────────────────────────────────────────────────

    private static StorageResource Resource(string account, string share) => new()
    {
        ProviderKey = "azure-fileshare",
        ProviderName = "Azure File Share",
        AccountName = account,
        ResourceName = share
    };

    private static Mock<IFileShareProvider> CreateTwoAccountProviderMock()
    {
        var provider = CreateProviderMock(authenticated: true);
        provider.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageResource> { Resource("beta", "data"), Resource("alpha", "data"), Resource("beta", "logs") });
        return provider;
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_UsesAccountFlag_WhenTheSameShareExistsInTwoAccounts()
    {
        var provider = CreateTwoAccountProviderMock();
        var console = new TestConsole();
        var cmd = new StorageLsCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings { AccountName = "alpha", ShareName = "data" });

        result.Should().Be(0);
        provider.Verify(p => p.ListDirectoryAsync("alpha", "data", It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_ResolvesTheShareWithinTheGivenAccount_WhenOnlyAccountGiven()
    {
        var provider = CreateTwoAccountProviderMock();
        var console = new TestConsole();
        var cmd = new StorageLsCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings { AccountName = "alpha" });

        result.Should().Be(0);
        provider.Verify(p => p.ListDirectoryAsync("alpha", "data", It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_InfersTheAccount_WhenTheShareNameIsUniqueAcrossAccounts()
    {
        var provider = CreateTwoAccountProviderMock();
        var console = new TestConsole();
        var cmd = new StorageLsCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings { ShareName = "logs" });

        result.Should().Be(0);
        provider.Verify(p => p.ListDirectoryAsync("beta", "logs", It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_Returns1_AndNamesTheAccounts_WhenShareIsAmbiguous_NonInteractive()
    {
        var provider = CreateTwoAccountProviderMock();
        var console = new TestConsole();
        var cmd = new StorageLsCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null);

        var result = cmd.Execute(ctx, new StorageLsCommand.Settings { ShareName = "data" });

        result.Should().Be(1);
        console.Output.Should().Contain("--account");
        console.Output.Should().Contain("alpha").And.Contain("beta");
        provider.Verify(p => p.ListDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
