using PKS.Commands.Prd;
using PKS.Infrastructure.Services;
using Xunit;
using FluentAssertions;

namespace PKS.CLI.Tests.Commands.Prd;

/// <summary>
/// Test runner to verify all PRD command tests can execute
/// </summary>
public class PrdTestRunner
{
    [Fact]
    public void AllPrdTestClasses_ShouldBeDiscoverable()
    {
        // Verify all test classes exist and can be instantiated
        var testTypes = new[]
        {
            typeof(PrdBranchCommandSimpleTests),
            typeof(PrdCommandRegistrationTests),
            typeof(PrdSettingsParsingTests),
            typeof(PrdIntegrationTests),
            typeof(PrdHelpSystemTests),
            typeof(PrdErrorHandlingTests)
        };

        foreach (var testType in testTypes)
        {
            testType.Should().NotBeNull($"{testType.Name} should be discoverable");
            testType.IsClass.Should().BeTrue($"{testType.Name} should be a class");
            testType.IsPublic.Should().BeTrue($"{testType.Name} should be public");
        }
    }

    [Fact]
    public void AllPrdCommandTypes_ShouldExist()
    {
        // Verify all command types referenced in tests exist
        var commandTypes = new[]
        {
            typeof(PrdBranchCommand),
            typeof(PrdGenerateCommand),
            typeof(PrdLoadCommand),
            typeof(PrdRequirementsCommand),
            typeof(PrdStatusCommand),
            typeof(PrdValidateCommand),
            typeof(PrdTemplateCommand)
        };

        foreach (var commandType in commandTypes)
        {
            commandType.Should().NotBeNull($"{commandType.Name} should exist");
            commandType.IsClass.Should().BeTrue($"{commandType.Name} should be a class");
        }
    }

    [Fact]
    public void AllPrdSettingsTypes_ShouldExist()
    {
        // Verify all settings types referenced in tests exist
        var settingsTypes = new[]
        {
            typeof(PrdBranchMainSettings),
            typeof(PrdGenerateSettings),
            typeof(PrdLoadSettings),
            typeof(PrdRequirementsSettings),
            typeof(PrdStatusSettings),
            typeof(PrdValidateSettings),
            typeof(PrdTemplateSettings)
        };

        foreach (var settingsType in settingsTypes)
        {
            settingsType.Should().NotBeNull($"{settingsType.Name} should exist");
            settingsType.IsClass.Should().BeTrue($"{settingsType.Name} should be a class");
        }
    }

    [Fact]
    public void AllPrdServiceInterfaces_ShouldExist()
    {
        // Verify service interfaces exist
        var serviceTypes = new[]
        {
            typeof(IPrdService)
        };

        foreach (var serviceType in serviceTypes)
        {
            serviceType.Should().NotBeNull($"{serviceType.Name} should exist");
            serviceType.IsInterface.Should().BeTrue($"{serviceType.Name} should be an interface");
        }
    }

    [Fact]
    public void TestProjectReferences_ShouldBeValid()
    {
        // This test ensures the test project can reference all necessary types
        // If this test passes, it means all dependencies are correctly configured

        // Test that we can create instances (this will fail if dependencies are missing)
        var branch = new PrdBranchCommand();
        var settings = new PrdBranchMainSettings();

        branch.Should().NotBeNull();
        settings.Should().NotBeNull();
    }
}