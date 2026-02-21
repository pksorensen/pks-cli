using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Npm;

/// <summary>
/// Tests for npm package structure and configuration
/// </summary>
[Trait("Category", "Npm")]
[Trait("Speed", "Fast")]
public class NpmPackageStructureTests : TestBase
{
    private readonly string _solutionRoot;
    private readonly string _npmRoot;

    public NpmPackageStructureTests()
    {
        _solutionRoot = FindSolutionRoot();
        _npmRoot = Path.Combine(_solutionRoot, "npm");
    }

    [Fact]
    public void MainPackage_ShouldHaveCorrectStructure()
    {
        // Arrange
        var mainPackagePath = Path.Combine(_npmRoot, "pks-cli");
        var packageJsonPath = Path.Combine(mainPackagePath, "package.json");

        // Assert
        Directory.Exists(mainPackagePath).Should().BeTrue("Main package directory should exist");
        File.Exists(packageJsonPath).Should().BeTrue("package.json should exist");

        // Parse and validate package.json
        var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        var root = packageJson.RootElement;

        // Validate basic fields
        root.GetProperty("name").GetString().Should().Be("@pks-cli/pks");
        root.GetProperty("description").GetString().Should().NotBeNullOrEmpty();

        // Validate bin field
        root.TryGetProperty("bin", out var bin).Should().BeTrue("package.json should have bin field");
        bin.TryGetProperty("pks", out var pksPath).Should().BeTrue();
        pksPath.GetString().Should().Be("./bin/pks.js");

        // Validate optionalDependencies
        root.TryGetProperty("optionalDependencies", out var optDeps).Should().BeTrue(
            "package.json should have optionalDependencies");

        var expectedPlatforms = new[]
        {
            "@pks-cli/pks-linux-x64",
            "@pks-cli/pks-linux-arm64",
            "@pks-cli/pks-osx-x64",
            "@pks-cli/pks-osx-arm64",
            "@pks-cli/pks-win-x64",
            "@pks-cli/pks-win-arm64"
        };

        foreach (var platform in expectedPlatforms)
        {
            optDeps.TryGetProperty(platform, out _).Should().BeTrue(
                $"optionalDependencies should include {platform}");
        }

        // Validate scripts
        root.TryGetProperty("scripts", out var scripts).Should().BeTrue();
        scripts.TryGetProperty("postinstall", out _).Should().BeTrue(
            "package.json should have postinstall script");
    }

    [Fact]
    public void MainPackage_BinScript_ShouldExist()
    {
        // Arrange
        var binScriptPath = Path.Combine(_npmRoot, "pks-cli", "bin", "pks.js");

        // Assert
        File.Exists(binScriptPath).Should().BeTrue("bin/pks.js should exist");

        var scriptContent = File.ReadAllText(binScriptPath);
        scriptContent.Should().Contain("#!/usr/bin/env node", "Script should have Node.js shebang");
        scriptContent.Should().Contain("platformMap", "Script should have platform detection");
    }

    [Fact]
    public void MainPackage_PostinstallScript_ShouldExist()
    {
        // Arrange
        var postinstallPath = Path.Combine(_npmRoot, "pks-cli", "postinstall.js");

        // Assert
        File.Exists(postinstallPath).Should().BeTrue("postinstall.js should exist");
    }

    [Theory]
    [InlineData("linux-x64", "linux", "x64")]
    [InlineData("linux-arm64", "linux", "arm64")]
    [InlineData("osx-x64", "darwin", "x64")]
    [InlineData("osx-arm64", "darwin", "arm64")]
    [InlineData("win-x64", "win32", "x64")]
    [InlineData("win-arm64", "win32", "arm64")]
    public void PlatformPackage_ShouldHaveCorrectStructure(string platform, string expectedOs, string expectedCpu)
    {
        // Arrange
        var platformPackagePath = Path.Combine(_npmRoot, $"pks-cli-{platform}");
        var packageJsonPath = Path.Combine(platformPackagePath, "package.json");

        // Assert - package.json exists and has correct structure
        File.Exists(packageJsonPath).Should().BeTrue($"package.json should exist for {platform}");

        var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        var root = packageJson.RootElement;

        // Validate name
        root.GetProperty("name").GetString().Should().Be($"@pks-cli/pks-{platform}");

        // Validate os constraint
        root.TryGetProperty("os", out var os).Should().BeTrue("package.json should have os field");
        var osArray = os.EnumerateArray().Select(e => e.GetString()).ToList();
        osArray.Should().Contain(expectedOs, $"os should include {expectedOs}");

        // Validate cpu constraint
        root.TryGetProperty("cpu", out var cpu).Should().BeTrue("package.json should have cpu field");
        var cpuArray = cpu.EnumerateArray().Select(e => e.GetString()).ToList();
        cpuArray.Should().Contain(expectedCpu, $"cpu should include {expectedCpu}");

        // Validate bin field
        root.TryGetProperty("bin", out var bin).Should().BeTrue();
        bin.TryGetProperty("pks", out _).Should().BeTrue();
    }

    [Fact]
    public void PackageVersions_ShouldBeSynchronized()
    {
        // Arrange
        var mainPackageJson = Path.Combine(_npmRoot, "pks-cli", "package.json");
        var mainVersion = GetPackageVersion(mainPackageJson);

        mainVersion.Should().NotBeNullOrEmpty("Main package should have a version");

        // Get all platform package versions
        var platformPackages = new[]
        {
            "pks-cli-linux-x64", "pks-cli-linux-arm64",
            "pks-cli-osx-x64", "pks-cli-osx-arm64",
            "pks-cli-win-x64", "pks-cli-win-arm64"
        };

        foreach (var platform in platformPackages)
        {
            var platformPackageJson = Path.Combine(_npmRoot, platform, "package.json");
            if (File.Exists(platformPackageJson))
            {
                var platformVersion = GetPackageVersion(platformPackageJson);
                platformVersion.Should().Be(mainVersion,
                    $"{platform} version should match main package version");
            }
        }

        // Verify optionalDependencies versions match
        var mainPkg = JsonDocument.Parse(File.ReadAllText(mainPackageJson));
        if (mainPkg.RootElement.TryGetProperty("optionalDependencies", out var optDeps))
        {
            foreach (var platform in platformPackages)
            {
                var packageName = $"@pks-cli/{platform}";
                if (optDeps.TryGetProperty(packageName, out var depVersion))
                {
                    depVersion.GetString().Should().Be(mainVersion,
                        $"optionalDependency version for {packageName} should match main version");
                }
            }
        }
    }

    [Fact]
    public void AllPlatformPackages_ShouldHaveReadme()
    {
        // Arrange
        var platformPackages = new[]
        {
            "pks-cli-linux-x64", "pks-cli-linux-arm64",
            "pks-cli-osx-x64", "pks-cli-osx-arm64",
            "pks-cli-win-x64", "pks-cli-win-arm64"
        };

        foreach (var platform in platformPackages)
        {
            var readmePath = Path.Combine(_npmRoot, platform, "README.md");
            if (Directory.Exists(Path.Combine(_npmRoot, platform)))
            {
                File.Exists(readmePath).Should().BeTrue($"{platform} should have README.md");
            }
        }
    }

    #region Helper Methods

    private string GetPackageVersion(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
            return string.Empty;

        var packageJson = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
        return packageJson.RootElement.GetProperty("version").GetString() ?? string.Empty;
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

    #endregion
}
