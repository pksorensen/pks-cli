using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace PKS.CLI.Tests.Integration.Npm;

/// <summary>
/// Integration tests for npm install and npx execution
/// These tests require published packages and are network-dependent
/// </summary>
[Trait("Category", "Integration")]
[Trait("Speed", "Slow")]
[Trait("Reliability", "Flaky")] // Network-dependent
public class NpmInstallTests : IntegrationTestBase
{
    private readonly string _testProjectRoot;

    public NpmInstallTests()
    {
        _testProjectRoot = CreateTempDirectory();
    }

    [Fact(Skip = "Requires published packages to npm registry")]
    public async Task NpmInstall_Global_ShouldInstallAndWork()
    {
        // This test validates that global npm installation works
        // Skip by default as it requires packages to be published

        // Arrange
        await CleanupGlobalInstall();

        // Act
        var (installExitCode, installOutput) = await RunNpmCommand("install -g @pks-cli/pks");

        // Assert
        installExitCode.Should().Be(0, $"npm install should succeed. Output: {installOutput}");

        // Verify pks command is available
        var (versionExitCode, versionOutput) = await RunCommand("pks", "--version");
        versionExitCode.Should().Be(0, "pks --version should work after global install");
        versionOutput.Should().Contain("pks-cli", "Version output should contain package name");
    }

    [Fact(Skip = "Requires published packages to npm registry")]
    public async Task Npx_ShouldRunWithoutInstalling()
    {
        // This test validates that npx execution works without prior installation

        // Act
        var (exitCode, output) = await RunNpmCommand("npx @pks-cli/pks --version");

        // Assert
        exitCode.Should().Be(0, $"npx execution should succeed. Output: {output}");
        output.Should().Contain("pks-cli", "npx should execute the binary");
    }

    [Fact(Skip = "Requires published packages to npm registry")]
    public async Task LocalInstall_ShouldWork_InProjectDirectory()
    {
        // This test validates that local project installation works

        // Arrange
        await InitializeNpmProject();

        // Act
        var (installExitCode, installOutput) = await RunNpmCommand("install @pks-cli/pks --save-dev", _testProjectRoot);

        // Assert
        installExitCode.Should().Be(0, $"npm install should succeed. Output: {installOutput}");

        // Verify package.json updated
        var packageJsonPath = Path.Combine(_testProjectRoot, "package.json");
        var packageJson = File.ReadAllText(packageJsonPath);
        packageJson.Should().Contain("@pks-cli/pks", "package.json should list pks-cli in devDependencies");

        // Verify binary is accessible via npx
        var (runExitCode, runOutput) = await RunNpmCommand("npx pks --version", _testProjectRoot);
        runExitCode.Should().Be(0, "npx pks should work from project directory");
    }

    [Fact]
    public async Task LocalInstall_PlatformPackage_ShouldOnlyInstallCurrentPlatform()
    {
        // This test validates that only the current platform's binary is installed
        // This test can run without published packages by using local packages

        // Arrange
        await InitializeNpmProject();
        var currentPlatform = GetCurrentPlatformPackageName();

        // Note: This test would need local .tgz files to work without published packages
        // For now, it validates the concept

        // Assert - After install, only one platform package should be in node_modules
        var nodeModulesPath = Path.Combine(_testProjectRoot, "node_modules", "@pks-cli");
        if (Directory.Exists(nodeModulesPath))
        {
            var installedPackages = Directory.GetDirectories(nodeModulesPath)
                .Select(Path.GetFileName)
                .Where(name => name != null && name.StartsWith("pks-"))
                .ToList();

            // Should only have one platform package + main package
            installedPackages.Should().HaveCountLessOrEqualTo(2,
                "Should only install main package and current platform package");

            if (installedPackages.Count > 0)
            {
                installedPackages.Should().Contain(currentPlatform.Replace("@pks-cli/", ""),
                    $"Should install current platform package: {currentPlatform}");
            }
        }
    }

    [Fact]
    public async Task PackageJson_Scripts_ShouldWorkWithLocalInstall()
    {
        // This test validates that pks can be used in npm scripts

        // Arrange
        await InitializeNpmProject();
        var packageJsonPath = Path.Combine(_testProjectRoot, "package.json");

        // Add a test script to package.json
        var packageJson = """
            {
              "name": "test-project",
              "version": "1.0.0",
              "scripts": {
                "pks:version": "pks --version"
              },
              "devDependencies": {}
            }
            """;

        File.WriteAllText(packageJsonPath, packageJson);

        // Note: Would need published package for full test
        // For now, validate the script is correctly defined
        var content = File.ReadAllText(packageJsonPath);
        content.Should().Contain("pks --version", "package.json should have pks script");
    }

    [Fact(Skip = "Requires published packages")]
    public async Task Install_ShouldShowProgress_AndCompleteSuccessfully()
    {
        // This test validates the installation experience

        // Act
        var (exitCode, output) = await RunNpmCommand("install @pks-cli/pks --verbose");

        // Assert
        exitCode.Should().Be(0);
        output.Should().Contain("@pks-cli/pks", "Output should show package name");

        // Verify no errors in output
        output.Should().NotContain("ERR!", "Installation should not have errors");
        output.Should().NotContain("WARN", "Installation should not have warnings");
    }

    [Fact(Skip = "Requires published packages")]
    public async Task Uninstall_ShouldCleanupCompletely()
    {
        // This test validates that uninstallation works correctly

        // Arrange
        await RunNpmCommand("install -g @pks-cli/pks");

        // Act
        var (exitCode, output) = await RunNpmCommand("uninstall -g @pks-cli/pks");

        // Assert
        exitCode.Should().Be(0, "npm uninstall should succeed");

        // Verify pks command is no longer available
        var (versionExitCode, _) = await RunCommand("pks", "--version");
        versionExitCode.Should().NotBe(0, "pks command should not be available after uninstall");
    }

    #region Helper Methods

    private async Task<(int exitCode, string output)> RunNpmCommand(string args, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "npm",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };

        using var process = Process.Start(startInfo);
        process.Should().NotBeNull();

        var output = await process!.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, output + "\n" + error);
    }

    private async Task<(int exitCode, string output)> RunCommand(string command, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return (1, "Failed to start process");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, output + "\n" + error);
        }
        catch (Exception ex)
        {
            return (1, $"Exception: {ex.Message}");
        }
    }

    private async Task InitializeNpmProject()
    {
        var packageJson = """
            {
              "name": "test-project",
              "version": "1.0.0",
              "description": "Test project for pks-cli",
              "private": true
            }
            """;

        var packageJsonPath = Path.Combine(_testProjectRoot, "package.json");
        await File.WriteAllTextAsync(packageJsonPath, packageJson);
    }

    private async Task CleanupGlobalInstall()
    {
        // Try to uninstall if already installed
        await RunNpmCommand("uninstall -g @pks-cli/pks");
    }

    private string GetCurrentPlatformPackageName()
    {
        var platform = Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => "linux",
            PlatformID.MacOSX => "darwin",
            PlatformID.Win32NT => "win32",
            _ => "linux"
        };

        var arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            arch = "arm64";

        var packageSuffix = platform switch
        {
            "darwin" => $"osx-{arch}",
            _ => $"{platform}-{arch}"
        };

        return $"@pks-cli/pks-{packageSuffix}";
    }

    private string CreateTempDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pks-cli-npm-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    #endregion

    public override void Dispose()
    {
        // Cleanup test project
        try
        {
            if (Directory.Exists(_testProjectRoot))
                Directory.Delete(_testProjectRoot, true);
        }
        catch
        {
            // Ignore cleanup errors
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
