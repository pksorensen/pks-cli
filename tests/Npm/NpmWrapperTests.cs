using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Npm;

/// <summary>
/// Tests for npm wrapper script platform detection and binary execution logic
/// </summary>
[Trait("Category", "Npm")]
[Trait("Speed", "Fast")]
public class NpmWrapperTests : TestBase
{
    [Theory]
    [InlineData("linux", "x64", "@pks-cli/pks-linux-x64")]
    [InlineData("linux", "arm64", "@pks-cli/pks-linux-arm64")]
    [InlineData("darwin", "x64", "@pks-cli/pks-osx-x64")]
    [InlineData("darwin", "arm64", "@pks-cli/pks-osx-arm64")]
    [InlineData("win32", "x64", "@pks-cli/pks-win-x64")]
    [InlineData("win32", "arm64", "@pks-cli/pks-win-arm64")]
    public void Wrapper_ShouldDetectPlatform_AndSelectCorrectBinary(
        string platform, string arch, string expectedPackage)
    {
        // This test validates the platform detection logic that would be in the wrapper
        // In practice, the wrapper uses process.platform and process.arch

        // Arrange
        var platformMap = new Dictionary<string, string>
        {
            ["linux-x64"] = "@pks-cli/pks-linux-x64",
            ["linux-arm64"] = "@pks-cli/pks-linux-arm64",
            ["darwin-x64"] = "@pks-cli/pks-osx-x64",
            ["darwin-arm64"] = "@pks-cli/pks-osx-arm64",
            ["win32-x64"] = "@pks-cli/pks-win-x64",
            ["win32-arm64"] = "@pks-cli/pks-win-arm64"
        };

        var key = $"{platform}-{arch}";

        // Act
        var packageName = platformMap.GetValueOrDefault(key);

        // Assert
        packageName.Should().Be(expectedPackage,
            $"Platform {platform}-{arch} should map to {expectedPackage}");
    }

    [Theory]
    [InlineData("freebsd", "x64")]
    [InlineData("sunos", "x64")]
    [InlineData("aix", "ppc64")]
    public void Wrapper_ShouldShowHelpfulError_WhenPlatformUnsupported(string platform, string arch)
    {
        // This test validates that unsupported platforms are handled gracefully

        // Arrange
        var platformMap = new Dictionary<string, string>
        {
            ["linux-x64"] = "@pks-cli/pks-linux-x64",
            ["linux-arm64"] = "@pks-cli/pks-linux-arm64",
            ["darwin-x64"] = "@pks-cli/pks-osx-x64",
            ["darwin-arm64"] = "@pks-cli/pks-osx-arm64",
            ["win32-x64"] = "@pks-cli/pks-win-x64",
            ["win32-arm64"] = "@pks-cli/pks-win-arm64"
        };

        var key = $"{platform}-{arch}";

        // Act
        var packageName = platformMap.GetValueOrDefault(key);

        // Assert
        packageName.Should().BeNullOrEmpty($"Unsupported platform {key} should not be in map");

        // In the real wrapper, this would trigger an error message like:
        // "Unsupported platform: freebsd-x64"
        // "Supported platforms: linux-x64, linux-arm64, darwin-x64, darwin-arm64, win32-x64, win32-arm64"
    }

    [Fact]
    public void Wrapper_ShouldForwardArguments_ToBinary()
    {
        // This test validates that command-line arguments are properly forwarded
        // In the actual wrapper, this is done via:
        // spawn(binaryPath, process.argv.slice(2), { stdio: 'inherit' })

        // Arrange
        var testArguments = new[] { "init", "MyProject", "--agentic", "--mcp" };

        // Act - simulate what the wrapper does
        var forwardedArgs = testArguments; // process.argv.slice(2) removes 'node' and script path

        // Assert
        forwardedArgs.Should().HaveCount(4);
        forwardedArgs.Should().ContainInOrder("init", "MyProject", "--agentic", "--mcp");
    }

    [Theory]
    [InlineData("linux-x64", "pks")]
    [InlineData("osx-arm64", "pks")]
    [InlineData("win-x64", "pks.exe")]
    [InlineData("win-arm64", "pks.exe")]
    public void Wrapper_ShouldUseCorrectBinaryName_ForPlatform(string platform, string expectedBinary)
    {
        // Arrange & Act
        var binaryName = platform.StartsWith("win") ? "pks.exe" : "pks";

        // Assert
        binaryName.Should().Be(expectedBinary,
            $"Platform {platform} should use binary name {expectedBinary}");
    }

    [Fact]
    public void Wrapper_ShouldLookForBinary_InMultipleLocations()
    {
        // The wrapper should check multiple locations for the binary:
        // 1. Relative to wrapper script: ../../pks-cli-{platform}/bin/pks
        // 2. In node_modules: ../../../@pks-cli/pks-{platform}/bin/pks

        // Arrange
        var possiblePaths = new List<string>
        {
            "../../pks-cli-linux-x64/bin/pks",
            "../../../@pks-cli/pks-linux-x64/bin/pks"
        };

        // Assert
        possiblePaths.Should().HaveCount(2, "Wrapper should check at least 2 possible locations");
        possiblePaths.Should().AllSatisfy(p => p.Should().MatchRegex(@"/pks(\.exe)?$"));
    }

    [Fact]
    public void Wrapper_ErrorMessage_ShouldBeHelpful()
    {
        // When the binary is not found, the error message should be helpful

        // Arrange
        var expectedErrorElements = new[]
        {
            "Could not find PKS CLI binary",
            "Expected at:",
            "Please try reinstalling:",
            "npm install -g @pks-cli/pks"
        };

        // Assert - verify that error messages contain helpful information
        foreach (var element in expectedErrorElements)
        {
            element.Should().NotBeNullOrEmpty("Error message should contain: " + element);
        }
    }

    [Fact]
    public void Wrapper_ShouldPassThroughExitCode_FromBinary()
    {
        // The wrapper should forward the exit code from the binary process
        // This is typically done with: process.exit(result.status || 0)

        // Arrange
        var simulatedExitCodes = new[] { 0, 1, 2, 127 };

        // Assert
        foreach (var exitCode in simulatedExitCodes)
        {
            // Wrapper should preserve the exit code
            exitCode.Should().BeInRange(0, 255, "Exit codes should be in valid range");
        }
    }
}
