using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace PKS.CLI.Tests.Build;

/// <summary>
/// Tests for self-contained build configuration and output validation
/// </summary>
[Trait("Category", "Build")]
[Trait("Speed", "Slow")]
public class SelfContainedBuildTests : TestBase
{
    private readonly string _solutionRoot;
    private readonly string _projectPath;

    public SelfContainedBuildTests()
    {
        _solutionRoot = FindSolutionRoot();
        _projectPath = Path.Combine(_solutionRoot, "src", "pks-cli.csproj");
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task SelfContainedBuild_ShouldProduceSingleExecutable_ForPlatform(string rid)
    {
        // Arrange
        var outputDir = CreateTempDirectory();
        var expectedBinary = GetExpectedBinaryName(rid);

        // Act
        var (exitCode, output) = await PublishSelfContained(rid, outputDir);

        // Assert
        exitCode.Should().Be(0, $"dotnet publish should succeed for {rid}. Output: {output}");

        var binaryPath = Path.Combine(outputDir, expectedBinary);
        File.Exists(binaryPath).Should().BeTrue($"Binary should exist at {binaryPath}");

        var fileInfo = new FileInfo(binaryPath);
        fileInfo.Length.Should().BeLessThan(100 * 1024 * 1024, "Binary should be less than 100MB");

        // Verify no PDB files in output
        var pdbFiles = Directory.GetFiles(outputDir, "*.pdb", SearchOption.AllDirectories);
        pdbFiles.Should().BeEmpty("Debug symbols should not be included in self-contained builds");
    }

    [Fact]
    public async Task SelfContainedBuild_ShouldEmbedTemplates_AsResources()
    {
        // Arrange
        var rid = GetCurrentRuntimeIdentifier();
        var outputDir = CreateTempDirectory();

        // Act
        var (exitCode, output) = await PublishSelfContained(rid, outputDir);

        // Assert
        exitCode.Should().Be(0, "dotnet publish should succeed");

        // Verify templates are not in separate files (they should be embedded)
        var templateDirs = Directory.GetDirectories(outputDir, "templates", SearchOption.AllDirectories);
        templateDirs.Should().BeEmpty("Templates should be embedded in binary, not as separate files");

        // Note: Runtime verification of template extraction would require running the binary
        // which is tested in integration tests
    }

    [Fact]
    public async Task SelfContainedBuild_ShouldNotIncludeDebugSymbols()
    {
        // Arrange
        var rid = GetCurrentRuntimeIdentifier();
        var outputDir = CreateTempDirectory();

        // Act
        var (exitCode, _) = await PublishSelfContained(rid, outputDir);

        // Assert
        exitCode.Should().Be(0);

        var pdbFiles = Directory.GetFiles(outputDir, "*.pdb", SearchOption.AllDirectories);
        pdbFiles.Should().BeEmpty("No .pdb files should be in release output");
    }

    [Theory(Skip = "Requires self-contained binary to be built first")]
    [InlineData("linux-x64", "pks")]
    [InlineData("win-x64", "pks.exe")]
    public async Task SelfContainedExecutable_ShouldRunAndShowVersion(string rid, string executable)
    {
        // This test validates that the self-contained binary actually works
        // Skipped by default as it requires the binary to be built first
        // and may not work on all platforms (e.g., running linux binary on windows)

        // Arrange
        var outputDir = CreateTempDirectory();
        await PublishSelfContained(rid, outputDir);
        var binaryPath = Path.Combine(outputDir, executable);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !executable.EndsWith(".exe"))
        {
            // Make executable on Unix platforms
            var chmod = Process.Start("chmod", $"+x {binaryPath}");
            await chmod!.WaitForExitAsync();
        }

        // Act
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var output = await process!.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Assert
        process.ExitCode.Should().Be(0, "Binary should run successfully");
        output.Should().Contain("pks-cli", "Version output should contain package name");
    }

    #region Helper Methods

    private async Task<(int exitCode, string output)> PublishSelfContained(string rid, string outputDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{_projectPath}\" " +
                       $"--configuration Release " +
                       $"--runtime {rid} " +
                       $"--self-contained true " +
                       $"--output \"{outputDir}\" " +
                       $"-p:PublishSingleFile=true " +
                       $"-p:PublishTrimmed=false " +
                       $"-p:PublishSelfContained=true " +
                       $"-p:EmbedTemplates=true " +
                       $"-p:Version=1.0.0-test",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _solutionRoot
        };

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var output = await process!.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var fullOutput = output + "\n" + error;
        return (process.ExitCode, fullOutput);
    }

    private string GetExpectedBinaryName(string rid)
    {
        return rid.StartsWith("win") ? "pks.exe" : "pks";
    }

    private string GetCurrentRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "win-arm64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "linux-x64" : "linux-arm64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "osx-x64" : "osx-arm64";

        throw new PlatformNotSupportedException("Current platform not supported");
    }

    private string FindSolutionRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (current != null && !File.Exists(Path.Combine(current, "pks-cli.sln")))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        if (current == null)
            throw new InvalidOperationException("Could not find solution root");

        return current;
    }

    private string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pks-cli-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    #endregion

    public override void Dispose()
    {
        // Cleanup temp directories created during tests
        // Note: In practice, temp directories are cleaned by OS
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
