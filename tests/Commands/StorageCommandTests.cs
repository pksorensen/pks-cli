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

public class StorageCommandTests
{
    private static Mock<IFileShareProvider> CreateProviderMock(
        string key = "azure-fileshare",
        string name = "Azure File Share",
        bool authenticated = true,
        List<StorageResource>? resources = null)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns(key);
        mock.Setup(p => p.ProviderName).Returns(name);
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(authenticated);
        mock.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources ?? new List<StorageResource>
            {
                new() { ProviderKey = key, ProviderName = name, AccountName = "mystorage", ResourceName = "myshare", Description = "100 GiB · SMB" }
            });
        mock.Setup(p => p.SyncAsync(It.IsAny<StorageSyncRequest>(), It.IsAny<Action<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncResult { FilesDownloaded = 3, BytesTransferred = 1024 });
        return mock;
    }

    private static FileShareProviderRegistry CreateRegistry(params IFileShareProvider[] providers)
        => new FileShareProviderRegistry(providers);

    // ═══════════════════════════════════════
    //  StorageListCommand
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "Storage")]
    public void List_ShowsMessage_WhenNoProvidersAuthenticated()
    {
        var provider = CreateProviderMock(authenticated: false);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageListCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "list", null);

        var result = cmd.Execute(ctx, new StorageSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("No authenticated storage providers found");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void List_ShowsResources_WhenProviderAuthenticated()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageListCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "list", null);

        var result = cmd.Execute(ctx, new StorageSettings());

        result.Should().Be(0);
        console.Output.Should().Contain("mystorage");
        console.Output.Should().Contain("myshare");
    }

    // ═══════════════════════════════════════
    //  StorageSyncCommand — write consent gate
    // ═══════════════════════════════════════

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_Returns1_WhenNoProvidersAuthenticated()
    {
        var provider = CreateProviderMock(authenticated: false);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            AccountName = "mystorage",
            ShareName = "myshare",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(1);
        console.Output.Should().Contain("No authenticated storage providers found");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_Download_RunsWithoutConfirm()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            AccountName = "mystorage",
            ShareName = "myshare",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(0);
        provider.Verify(p => p.SyncAsync(
            It.Is<StorageSyncRequest>(r => r.Direction == SyncDirection.Download),
            It.IsAny<Action<string>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_Upload_IsBlocked_WhenNonInteractive()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole(); // TestConsole is non-interactive
        var cmd = new StorageSyncCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Upload,
            AccountName = "mystorage",
            ShareName = "myshare",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(1);
        console.Output.Should().Contain("interactive confirmation");
        // SyncAsync must never be called for blocked write operations
        provider.Verify(p => p.SyncAsync(
            It.IsAny<StorageSyncRequest>(),
            It.IsAny<Action<string>>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_DryRun_DoesNotRequireConfirm()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Upload,
            AccountName = "mystorage",
            ShareName = "myshare",
            LocalPath = Path.GetTempPath(),
            DryRun = true
        });

        result.Should().Be(0);
        console.Output.ToLowerInvariant().Should().Contain("dry run");
        provider.Verify(p => p.SyncAsync(
            It.Is<StorageSyncRequest>(r => r.DryRun),
            It.IsAny<Action<string>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_ShowsSummaryTable_OnSuccess()
    {
        var provider = CreateProviderMock(authenticated: true);
        var registry = CreateRegistry(provider.Object);
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(registry, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            AccountName = "mystorage",
            ShareName = "myshare",
            LocalPath = Path.GetTempPath()
        });

        console.Output.Should().Contain("Files downloaded");
    }
}
