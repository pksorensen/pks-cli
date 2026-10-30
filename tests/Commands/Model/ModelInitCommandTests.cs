using FluentAssertions;
using Moq;
using PKS.Commands.Model;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Model;

public class ModelInitCommandTests
{
    [Fact]
    [Trait("Category", "Model")]
    public void Catalog_HasSpeakerEres2Net_AsSingleFileWithChecksum()
    {
        var entry = ModelCatalog.Find("speaker-eres2net");

        entry.Should().NotBeNull();
        entry!.Kind.Should().Be(CatalogEntryKind.SingleFile);
        entry.Files.Should().ContainKey("model").WhoseValue.Should().EndWith(".onnx");
        entry.DownloadUrl.Should().EndWith("/" + entry.Files["model"]);
        entry.Sha256.Should().HaveLength(64);
        entry.Capabilities.Should().Contain("speaker-embedding");
    }

    [Fact]
    [Trait("Category", "Model")]
    public void Catalog_Parakeet_IsStillAnArchive()
    {
        ModelCatalog.Find("parakeet-v3")!.Kind.Should().Be(CatalogEntryKind.TarBz2);
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Init_SingleFile_DownloadsVerifiesAndRegisters_WithoutExtracting()
    {
        var entry = ModelCatalog.Find("speaker-eres2net")!;
        var root = Path.Combine(Path.GetTempPath(), "pks-model-test-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, entry.Name);
        try
        {
            var registry = new Mock<IModelRegistryService>();
            registry.Setup(r => r.GetAsync(entry.Name, It.IsAny<CancellationToken>())).ReturnsAsync((InstalledModel?)null);
            registry.Setup(r => r.GetInstallDirectory(entry.Name)).Returns(installDir);
            InstalledModel? registered = null;
            registry.Setup(r => r.RegisterAsync(It.IsAny<InstalledModel>(), It.IsAny<CancellationToken>()))
                .Callback<InstalledModel, CancellationToken>((m, _) => registered = m)
                .Returns(Task.CompletedTask);

            var download = new Mock<IModelDownloadService>();
            download.Setup(d => d.DownloadAsync(entry.DownloadUrl, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IProgress<long>?, CancellationToken>((_, dest, _, _) => File.WriteAllBytesAsync(dest, new byte[] { 1, 2, 3 }));
            download.Setup(d => d.ComputeSha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(entry.Sha256!);

            var console = new TestConsole().Interactive();
            console.Input.PushTextWithEnter("y");
            var cmd = new ModelInitCommand(registry.Object, download.Object, console);

            var rc = await cmd.ExecuteAsync(Context(entry.Name), new ModelSettings());

            rc.Should().Be(0, console.Output);
            File.Exists(Path.Combine(installDir, entry.Files["model"])).Should().BeTrue();
            File.Exists(Path.Combine(installDir, "manifest.json")).Should().BeTrue();
            registered.Should().NotBeNull();
            registered!.Files["model"].Should().Be(entry.Files["model"]);
            registered.Source!.Sha256.Should().Be(entry.Sha256);
            download.Verify(d => d.ExtractTarBz2Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
            Directory.Exists(installDir + ".staging").Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Init_SingleFile_ChecksumMismatch_FailsAndInstallsNothing()
    {
        var entry = ModelCatalog.Find("speaker-eres2net")!;
        var root = Path.Combine(Path.GetTempPath(), "pks-model-test-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, entry.Name);
        try
        {
            var registry = new Mock<IModelRegistryService>();
            registry.Setup(r => r.GetAsync(entry.Name, It.IsAny<CancellationToken>())).ReturnsAsync((InstalledModel?)null);
            registry.Setup(r => r.GetInstallDirectory(entry.Name)).Returns(installDir);

            var download = new Mock<IModelDownloadService>();
            download.Setup(d => d.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IProgress<long>?, CancellationToken>((_, dest, _, _) => File.WriteAllBytesAsync(dest, new byte[] { 9 }));
            download.Setup(d => d.ComputeSha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new string('0', 64));

            var console = new TestConsole().Interactive();
            console.Input.PushTextWithEnter("y");
            var cmd = new ModelInitCommand(registry.Object, download.Object, console);

            var rc = await cmd.ExecuteAsync(Context(entry.Name), new ModelSettings());

            rc.Should().Be(1);
            console.Output.Should().Contain("checksum");
            File.Exists(Path.Combine(installDir, entry.Files["model"])).Should().BeFalse();
            registry.Verify(r => r.RegisterAsync(It.IsAny<InstalledModel>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Init_WithYes_DoesNotPrompt()
    {
        var entry = ModelCatalog.Find("speaker-eres2net")!;
        var root = Path.Combine(Path.GetTempPath(), "pks-model-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new Mock<IModelRegistryService>();
            registry.Setup(r => r.GetAsync(entry.Name, It.IsAny<CancellationToken>())).ReturnsAsync((InstalledModel?)null);
            registry.Setup(r => r.GetInstallDirectory(entry.Name)).Returns(Path.Combine(root, entry.Name));
            var download = new Mock<IModelDownloadService>();
            download.Setup(d => d.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IProgress<long>?, CancellationToken>((_, dest, _, _) => File.WriteAllBytesAsync(dest, new byte[] { 1 }));
            download.Setup(d => d.ComputeSha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(entry.Sha256!);

            // Non-interactive console: a prompt would throw. --yes must not prompt.
            var console = new TestConsole();
            var cmd = new ModelInitCommand(registry.Object, download.Object, console);

            var rc = await cmd.ExecuteAsync(Context(entry.Name), new ModelSettings { Yes = true });

            rc.Should().Be(0, console.Output);
            console.Output.Should().NotContain("Proceed with download");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Model")]
    public async Task Update_SingleFile_ReinstallsWithoutExtracting()
    {
        var entry = ModelCatalog.Find("speaker-eres2net")!;
        var root = Path.Combine(Path.GetTempPath(), "pks-model-test-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(root, entry.Name);
        try
        {
            Directory.CreateDirectory(installDir);
            await File.WriteAllTextAsync(Path.Combine(installDir, "old.onnx"), "old");
            var registry = new Mock<IModelRegistryService>();
            registry.Setup(r => r.GetAsync(entry.Name, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new InstalledModel { Name = entry.Name, Version = "2000-01-01", InstallPath = installDir });
            registry.Setup(r => r.GetInstallDirectory(entry.Name)).Returns(installDir);
            var download = new Mock<IModelDownloadService>();
            download.Setup(d => d.DownloadAsync(entry.DownloadUrl, It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, IProgress<long>?, CancellationToken>((_, dest, _, _) => File.WriteAllBytesAsync(dest, new byte[] { 4, 5 }));
            download.Setup(d => d.ComputeSha256Async(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(entry.Sha256!);

            var console = new TestConsole();
            var cmd = new ModelUpdateCommand(registry.Object, download.Object, console);

            var rc = await cmd.ExecuteAsync(Context(entry.Name), new ModelSettings { Yes = true });

            rc.Should().Be(0, console.Output);
            File.Exists(Path.Combine(installDir, entry.Files["model"])).Should().BeTrue();
            File.Exists(Path.Combine(installDir, "old.onnx")).Should().BeFalse();
            download.Verify(d => d.ExtractTarBz2Async(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()), Times.Never);
            registry.Verify(r => r.RegisterAsync(It.Is<InstalledModel>(m => m.Version == entry.Version), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static CommandContext Context(string data) =>
        new(Mock.Of<IRemainingArguments>(), "init", data);
}
