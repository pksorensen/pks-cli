using FluentAssertions;
using Moq;
using PKS.Commands.Storage;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// <c>pks storage</c> driven the way an agent drives it: no terminal, so no prompt can be shown
/// and no live progress bar is drawn. Every missing choice must fail with what to pass and what
/// exists, and a long sync must keep saying where it has got to.
/// </summary>
public class StorageWithoutTerminalTests
{
    private static StorageResource Resource(string account, string share) => new()
    {
        ProviderKey = "azure-fileshare",
        ProviderName = "Azure File Share",
        AccountName = account,
        ResourceName = share
    };

    private static Mock<IFileShareProvider> CreateProviderMock(
        List<StorageResource> resources,
        Func<StorageSyncRequest, Action<SyncProgressUpdate>, SyncResult>? sync = null)
    {
        var mock = new Mock<IFileShareProvider>();
        mock.Setup(p => p.ProviderKey).Returns("azure-fileshare");
        mock.Setup(p => p.ProviderName).Returns("Azure File Share");
        mock.Setup(p => p.IsAuthenticatedAsync()).ReturnsAsync(true);
        mock.Setup(p => p.ListResourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(resources);
        mock.Setup(p => p.SyncAsync(It.IsAny<StorageSyncRequest>(), It.IsAny<Action<SyncProgressUpdate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StorageSyncRequest r, Action<SyncProgressUpdate> progress, CancellationToken _) =>
                sync?.Invoke(r, progress) ?? new SyncResult());
        mock.Setup(p => p.ListDirectoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<StorageListRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageListResult { ShareName = "data", Path = "/" });
        return mock;
    }

    private static readonly List<StorageResource> FourShares =
    [
        Resource("alpha", "data"),
        Resource("alpha", "logs"),
        Resource("beta", "config"),
        Resource("gamma", "media"),
    ];

    private static int Sync(Mock<IFileShareProvider> provider, TestConsole console, StorageSyncCommand.Settings settings) =>
        new StorageSyncCommand(new FileShareProviderRegistry([provider.Object]), console)
            .Execute(new CommandContext(Mock.Of<IRemainingArguments>(), "sync", null), settings);

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_without_account_names_the_flag_and_the_accounts_instead_of_prompting()
    {
        var provider = CreateProviderMock(FourShares);
        var console = new TestConsole();

        var result = Sync(provider, console, new StorageSyncCommand.Settings { LocalPath = Path.GetTempPath(), DryRun = true });

        result.Should().Be(1);
        console.Output.Should().Contain("--account").And.Contain("alpha, beta, gamma");
        provider.Verify(p => p.SyncAsync(It.IsAny<StorageSyncRequest>(), It.IsAny<Action<SyncProgressUpdate>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_without_share_names_the_flag_and_the_accounts_shares()
    {
        var provider = CreateProviderMock(FourShares);
        var console = new TestConsole();

        var result = Sync(provider, console, new StorageSyncCommand.Settings { AccountName = "alpha", LocalPath = Path.GetTempPath() });

        result.Should().Be(1);
        console.Output.Should().Contain("--share").And.Contain("data, logs");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_without_local_path_says_to_pass_it_instead_of_prompting()
    {
        var provider = CreateProviderMock(FourShares);
        var console = new TestConsole();

        var result = Sync(provider, console, new StorageSyncCommand.Settings { AccountName = "beta", ShareName = "config" });

        result.Should().Be(1);
        console.Output.Should().Contain("first argument");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Ls_without_account_names_the_flag_and_the_accounts_instead_of_prompting()
    {
        var provider = CreateProviderMock(FourShares);
        var console = new TestConsole();

        var result = new StorageLsCommand(new FileShareProviderRegistry([provider.Object]), console)
            .Execute(new CommandContext(Mock.Of<IRemainingArguments>(), "ls", null), new StorageLsCommand.Settings { Path = "/meeting-types" });

        result.Should().Be(1);
        console.Output.Should().Contain("--account").And.Contain("alpha, beta, gamma");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_prints_progress_lines_while_it_runs_and_a_final_one()
    {
        StorageSyncCommand.ProgressLineInterval = TimeSpan.FromMilliseconds(20);
        var console = new TestConsole();
        var provider = CreateProviderMock(FourShares, (_, progress) =>
        {
            progress(new SyncProgressUpdate(0, 0, "Discovering..."));
            progress(new SyncProgressUpdate(1, 29, "a.json") { DirectoriesListed = 4385, DirectoriesPruned = 13242, BytesTransferred = 2048 });
            // Return only once a tick has printed, so the assertion does not race the timer.
            SpinWait.SpinUntil(() => console.Lines.Any(l => l.Contains("4385 dirs")), TimeSpan.FromSeconds(5));
            return new SyncResult { FilesDownloaded = 1, DirectoriesListed = 4385, DirectoriesPruned = 13242 };
        });

        var result = Sync(provider, console, new StorageSyncCommand.Settings { AccountName = "beta", ShareName = "config", LocalPath = Path.GetTempPath() });

        result.Should().Be(0);
        var progressLines = console.Lines.Where(l => l.Contains("4385 dirs")).ToList();
        progressLines.Should().HaveCountGreaterThan(1, "a line per tick plus the final one");
        progressLines[0].Should().Contain("(13242 pruned)").And.Contain("29 matched").And.Contain("1 downloaded").And.Contain("2.0 KB");
        console.Output.Should().Contain("Directories skipped by filters");
        console.Output.Should().NotContain("━", "a progress bar has nowhere to draw without a terminal");
    }

    [Fact]
    [Trait("Category", "Storage")]
    public void Sync_dry_run_lists_what_it_would_download()
    {
        var provider = CreateProviderMock(FourShares, (_, _) => new SyncResult
        {
            PlannedTransfers = [new("sites/main/site.json", 2048), new("staff/p1/profile.json", 100)]
        });
        var console = new TestConsole();

        var result = Sync(provider, console, new StorageSyncCommand.Settings
        {
            AccountName = "beta", ShareName = "config", LocalPath = Path.GetTempPath(), DryRun = true
        });

        result.Should().Be(0);
        console.Output.Should().Contain("Would download 2 file(s), 2.1 KB")
            .And.Contain("sites/main/site.json")
            .And.Contain("staff/p1/profile.json");
        console.Output.Should().Contain("Files to download");
    }
}
