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
        mock.Setup(p => p.SyncAsync(It.IsAny<StorageSyncRequest>(), It.IsAny<Action<SyncProgressUpdate>>(), It.IsAny<CancellationToken>()))
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

        var result = cmd.Execute(ctx, new StorageListCommand.Settings());

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

        var result = cmd.Execute(ctx, new StorageListCommand.Settings());

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
            It.IsAny<Action<SyncProgressUpdate>>(),
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
            It.IsAny<Action<SyncProgressUpdate>>(),
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
            It.IsAny<Action<SyncProgressUpdate>>(),
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

    // ═══════════════════════════════════════
    //  Several storage accounts behind one provider
    // ═══════════════════════════════════════

    private static List<StorageResource> TwoAccountsSameShare() => new()
    {
        Resource("beta", "data"),
        Resource("alpha", "data"),
        Resource("beta", "logs")
    };

    private static StorageResource Resource(string account, string share) => new()
    {
        ProviderKey = "azure-fileshare",
        ProviderName = "Azure File Share",
        AccountName = account,
        ResourceName = share,
        Description = "100 GiB · SMB"
    };

    [Fact]
    [Trait("Category", "Storage")]
    public void List_ShowsEveryAccount_OrderedByAccountThenShare()
    {
        var provider = CreateProviderMock(resources: TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageListCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "list", null);

        var result = cmd.Execute(ctx, new StorageListCommand.Settings());

        result.Should().Be(0);
        var output = console.Output;
        output.Should().Contain("alpha").And.Contain("beta").And.Contain("logs");
        output.IndexOf("alpha", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("beta", StringComparison.Ordinal));
        // Within beta: "data" before "logs".
        var betaStart = output.IndexOf("beta", StringComparison.Ordinal);
        output.IndexOf("data", betaStart, StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("logs", betaStart, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void List_FiltersToOneAccount_WhenAccountGiven()
    {
        var provider = CreateProviderMock(resources: TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageListCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "list", null);

        var result = cmd.Execute(ctx, new StorageListCommand.Settings { AccountName = "alpha" });

        result.Should().Be(0);
        console.Output.Should().Contain("alpha");
        console.Output.Should().NotContain("beta");
        console.Output.Should().NotContain("logs");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_UsesAccountFlag_WhenTheSameShareExistsInTwoAccounts()
    {
        var provider = CreateProviderMock(resources: TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            AccountName = "alpha",
            ShareName = "data",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(0);
        provider.Verify(p => p.SyncAsync(
            It.Is<StorageSyncRequest>(r => r.AccountName == "alpha" && r.ResourceName == "data"),
            It.IsAny<Action<SyncProgressUpdate>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_ResolvesTheShareWithinTheGivenAccount_WhenOnlyAccountGiven()
    {
        // alpha holds exactly one share, so --account alpha needs no share prompt even though the
        // provider as a whole has three shares.
        var provider = CreateProviderMock(resources: TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            AccountName = "alpha",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(0);
        provider.Verify(p => p.SyncAsync(
            It.Is<StorageSyncRequest>(r => r.AccountName == "alpha" && r.ResourceName == "data"),
            It.IsAny<Action<SyncProgressUpdate>>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_Returns1_AndNamesTheAccounts_WhenShareIsAmbiguous_NonInteractive()
    {
        var provider = CreateProviderMock(resources: TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageSyncCommand(CreateRegistry(provider.Object), console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null);

        var result = cmd.Execute(ctx, new StorageSyncCommand.Settings
        {
            Direction = SyncDirection.Download,
            ShareName = "data",
            LocalPath = Path.GetTempPath()
        });

        result.Should().Be(1);
        console.Output.Should().Contain("--account");
        console.Output.Should().Contain("alpha").And.Contain("beta");
        provider.Verify(p => p.SyncAsync(It.IsAny<StorageSyncRequest>(), It.IsAny<Action<SyncProgressUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ═══════════════════════════════════════
    //  StorageRmCommand — consent resource string
    // ═══════════════════════════════════════

    private static (Mock<IFileShareProvider> Provider, Mock<PKS.Infrastructure.Services.Security.IActionGuard> Guard) CreateRmMocks(List<StorageResource>? resources = null)
    {
        var provider = CreateProviderMock(resources: resources);
        provider.Setup(p => p.EnumerateFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StorageFileRef> { new("old/report.csv", 512) });
        provider.Setup(p => p.DeleteFilesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageDeleteResult { FilesDeleted = 1, BytesDeleted = 512 });
        var guard = new Mock<PKS.Infrastructure.Services.Security.IActionGuard>();
        guard.Setup(g => g.RequireAsync(It.IsAny<PKS.Infrastructure.Services.Security.ActionRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return (provider, guard);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Rm_ConsentResource_IsProviderKeyAccountSlashShare()
    {
        var (provider, guard) = CreateRmMocks();
        var console = new TestConsole();
        var cmd = new StorageRmCommand(CreateRegistry(provider.Object), guard.Object, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "rm", null);

        var result = cmd.Execute(ctx, new StorageRmCommand.Settings
        {
            Path = "old/report.csv",
            AccountName = "acct",
            ShareName = "share",
            Yes = true
        });

        result.Should().Be(0);
        guard.Verify(g => g.RequireAsync(
            It.Is<PKS.Infrastructure.Services.Security.ActionRequest>(r => r.Resource == "azure-fileshare:acct/share"),
            It.IsAny<CancellationToken>()),
            Times.Once);
        provider.Verify(p => p.DeleteFilesAsync("acct", "share", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Rm_UsesAccountFlag_WhenTheSameShareExistsInTwoAccounts()
    {
        var (provider, guard) = CreateRmMocks(TwoAccountsSameShare());
        var console = new TestConsole();
        var cmd = new StorageRmCommand(CreateRegistry(provider.Object), guard.Object, console);
        var ctx = new CommandContext(Mock.Of<IRemainingArguments>(), "rm", null);

        var result = cmd.Execute(ctx, new StorageRmCommand.Settings
        {
            Path = "old/report.csv",
            AccountName = "alpha",
            Yes = true
        });

        result.Should().Be(0);
        provider.Verify(p => p.EnumerateFilesAsync("alpha", "data", "old/report.csv", false, It.IsAny<CancellationToken>()), Times.Once);
        guard.Verify(g => g.RequireAsync(
            It.Is<PKS.Infrastructure.Services.Security.ActionRequest>(r => r.Resource == "azure-fileshare:alpha/data"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
