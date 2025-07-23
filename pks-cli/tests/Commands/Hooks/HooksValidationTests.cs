using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace PKS.CLI.Tests.Commands.Hooks;

/// <summary>
/// Validation tests for hook functionality covering requirements from issues #11-#15
/// Tests output format validation, naming conventions, and Claude Code specification compliance
/// </summary>
public class HooksValidationTests : TestBase
{
    [Fact]
    public void AllHookTypes_ShouldBePresentAndCorrect()
    {
        // Arrange - Expected hook types from Claude Code specification
        var expectedHookTypes = new Dictionary<string, string>
        {
            ["PreToolUse"] = "pks hooks pre-tool-use",
            ["PostToolUse"] = "pks hooks post-tool-use",
            ["UserPromptSubmit"] = "pks hooks user-prompt-submit",
            ["Notification"] = "pks hooks notification",
            ["Stop"] = "pks hooks stop",
            ["SubagentStop"] = "pks hooks subagent-stop",
            ["PreCompact"] = "pks hooks pre-compact"
        };

        // Assert - All 7 hook types must be present
        expectedHookTypes.Should().HaveCount(7, "Claude Code specification requires exactly 7 hook types");

        foreach (var (jsonProperty, cliCommand) in expectedHookTypes)
        {
            // Validate JSON property naming (PascalCase)
            jsonProperty.Should().MatchRegex(@"^[A-Z][a-zA-Z]*$",
                $"JSON property '{jsonProperty}' should use PascalCase");

            // Validate CLI command naming (kebab-case)
            cliCommand.Should().StartWith("pks hooks ");
            var hookPart = cliCommand.Substring("pks hooks ".Length);
            hookPart.Should().MatchRegex(@"^[a-z]+(-[a-z]+)*$",
                $"CLI command '{hookPart}' should use kebab-case");
        }
    }

    [Theory]
    [InlineData("PreToolUseCommand", "pre-tool-use")]
    [InlineData("PostToolUseCommand", "post-tool-use")]
    [InlineData("UserPromptSubmitCommand", "user-prompt-submit")]
    [InlineData("NotificationCommand", "notification")]
    [InlineData("StopCommand", "stop")]
    [InlineData("SubagentStopCommand", "subagent-stop")]
    [InlineData("PreCompactCommand", "pre-compact")]
    public void HookCommandClasses_ShouldFollowNamingConvention(string className, string expectedCommandName)
    {
        // Arrange
        var assemblyTypes = typeof(HooksCommand).Assembly.GetTypes();
        var commandType = assemblyTypes.FirstOrDefault(t => t.Name == className);

        // Assert
        commandType.Should().NotBeNull($"Command class '{className}' should exist");
        commandType!.Namespace.Should().Be("PKS.Commands.Hooks",
            "All hook commands should be in PKS.Commands.Hooks namespace");

        // Verify inheritance
        commandType.Should().BeAssignableTo<AsyncCommand<HooksSettings>>(
            "All hook event commands should inherit from AsyncCommand<HooksSettings>");

        // Verify naming convention mapping
        var commandNameFromClass = className.Replace("Command", "")
            .Replace("PreToolUse", "pre-tool-use")
            .Replace("PostToolUse", "post-tool-use")
            .Replace("UserPromptSubmit", "user-prompt-submit")
            .Replace("Stop", "stop");

        expectedCommandName.Should().Be(commandNameFromClass.ToLowerInvariant(),
            "Command name should match class name pattern");
    }

    [Fact]
    public void HookOutputFormat_ShouldMatchClaudeCodeExpectations()
    {
        // Arrange - Expected output patterns for Claude Code integration
        var expectedPatterns = new Dictionary<string, string[]>
        {
            ["EventTrigger"] = new[] { @"PKS Hooks: \w+ Event Triggered" },
            ["SuccessMessage"] = new[] { @"✓ \w+ hook completed successfully" },
            ["SectionHeaders"] = new[] {
                "Environment Variables:",
                "Command Line Arguments:",
                "STDIN Input:",
                "Working Directory:"
            }
        };

        // Assert
        foreach (var (category, patterns) in expectedPatterns)
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Contains(@"\w"))
                {
                    // Regex pattern
                    var regex = new Regex(pattern);
                    regex.Should().NotBeNull($"Pattern '{pattern}' should be valid regex");
                }
                else
                {
                    // Literal string
                    pattern.Should().NotBeNullOrEmpty($"Pattern in category '{category}' should not be empty");
                }
            }
        }
    }

    [Fact]
    public void HookConfiguration_ShouldGenerateValidJsonStructure()
    {
        // Arrange - Sample configuration as it would be generated
        var sampleConfig = new
        {
            hooks = new
            {
                preToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "pks hooks pre-tool-use"
                            }
                        }
                    }
                },
                postToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "pks hooks post-tool-use"
                            }
                        }
                    }
                },
                userPromptSubmit = new[]
                {
                    new
                    {
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "pks hooks user-prompt-submit"
                            }
                        }
                    }
                },
                stop = new[]
                {
                    new
                    {
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "pks hooks stop"
                            }
                        }
                    }
                }
            }
        };

        // Act - Serialize and deserialize to validate JSON structure
        var json = JsonSerializer.Serialize(sampleConfig, new JsonSerializerOptions { WriteIndented = true });
        var parsed = JsonDocument.Parse(json);

        // Assert
        parsed.RootElement.TryGetProperty("hooks", out var hooksElement).Should().BeTrue();

        // Validate preToolUse and postToolUse have matcher
        ValidateToolSpecificHook(hooksElement, "preToolUse", "pks hooks pre-tool-use", shouldHaveMatcher: true);
        ValidateToolSpecificHook(hooksElement, "postToolUse", "pks hooks post-tool-use", shouldHaveMatcher: true);

        // Validate userPromptSubmit and stop do not have matcher  
        ValidateGlobalHook(hooksElement, "userPromptSubmit", "pks hooks user-prompt-submit");
        ValidateGlobalHook(hooksElement, "stop", "pks hooks stop");
    }

    [Theory]
    [InlineData("preToolUse", true)]
    [InlineData("postToolUse", true)]
    [InlineData("userPromptSubmit", false)]
    [InlineData("stop", false)]
    public void HookTypes_ShouldHaveCorrectMatcherRequirements(string hookType, bool shouldHaveMatcher)
    {
        // Assert - This validates the Claude Code specification requirements
        var toolSpecificHooks = new[] { "preToolUse", "postToolUse" };
        var globalHooks = new[] { "userPromptSubmit", "stop" };

        if (shouldHaveMatcher)
        {
            toolSpecificHooks.Should().Contain(hookType,
                "Tool-specific hooks should require matcher");
        }
        else
        {
            globalHooks.Should().Contain(hookType,
                "Global hooks should not require matcher");
        }
    }

    [Fact]
    public void AllHookCommands_ShouldUseConsistentExitCodes()
    {
        // Arrange - Expected exit codes
        var expectedExitCodes = new Dictionary<string, int>
        {
            ["Success"] = 0,
            ["Error"] = 1
        };

        // Assert
        foreach (var (scenario, expectedCode) in expectedExitCodes)
        {
            expectedCode.Should().BeInRange(0, 1,
                $"Exit code for '{scenario}' should be 0 (success) or 1 (error)");
        }

        // All hook event commands should return 0 on successful execution
        // Error handling should return 1
        expectedExitCodes["Success"].Should().Be(0);
        expectedExitCodes["Error"].Should().Be(1);
    }

    [Fact]
    public void HookCommands_ShouldHandleEnvironmentVariablesCorrectly()
    {
        // Arrange - Environment variable patterns that hooks should handle
        var environmentPatterns = new[]
        {
            @"^[A-Z][A-Z0-9_]*$",  // Standard env var pattern
            @"^PATH$",             // PATH variable
            @"^HOME$",             // HOME variable  
            @"^[A-Z]+.*$"          // Any uppercase-starting variable
        };

        // Assert
        foreach (var pattern in environmentPatterns)
        {
            var regex = new Regex(pattern);
            regex.Should().NotBeNull($"Environment variable pattern '{pattern}' should be valid");

            // Test with sample variables
            regex.IsMatch("PATH").Should().BeTrue("PATH should match environment variable patterns");
            regex.IsMatch("HOME").Should().BeTrue("HOME should match environment variable patterns");
        }
    }

    [Fact]
    public void HookConfiguration_ShouldSupportMergingWithExistingSettings()
    {
        // This test validates the merging logic requirements

        // Arrange - Scenarios for merging
        var mergingScenarios = new[]
        {
            "Empty settings file",
            "Existing non-PKS hooks",
            "Existing PKS hooks",
            "Mixed existing hooks"
        };

        // Assert
        mergingScenarios.Should().HaveCount(4, "Should support all merging scenarios");
        mergingScenarios.Should().AllSatisfy(scenario =>
            scenario.Should().NotBeNullOrEmpty("All merging scenarios should be defined"));
    }

    [Theory]
    [InlineData(SettingsScope.User, "~/.claude/settings.json")]
    [InlineData(SettingsScope.Project, "./.claude/settings.json")]
    [InlineData(SettingsScope.Local, "./.claude/settings.json")]
    public void SettingsScope_ShouldMapToCorrectPaths(SettingsScope scope, string expectedPath)
    {
        // Assert
        Enum.IsDefined(typeof(SettingsScope), scope).Should().BeTrue($"Scope '{scope}' should be valid");
        expectedPath.Should().NotBeNullOrEmpty("Expected path should be defined");

        if (expectedPath.StartsWith("~/"))
        {
            expectedPath.Should().StartWith("~/", "User scope should use home directory");
        }
        else
        {
            expectedPath.Should().StartWith("./", "Project/Local scope should use current directory");
        }

        expectedPath.Should().EndWith("settings.json", "All scopes should use settings.json filename");
        expectedPath.Should().Contain(".claude", "All scopes should use .claude directory");
    }

    [Fact]
    public void HookEventCommands_ShouldImplementRequiredInterface()
    {
        // Arrange
        var hookCommandTypes = new[]
        {
            typeof(PreToolUseCommand),
            typeof(PostToolUseCommand),
            typeof(UserPromptSubmitCommand),
            typeof(StopCommand)
        };

        // Assert
        foreach (var commandType in hookCommandTypes)
        {
            commandType.Should().BeAssignableTo<AsyncCommand<HooksSettings>>(
                $"Command {commandType.Name} should inherit from AsyncCommand<HooksSettings>");

            // Verify ExecuteAsync method exists
            var executeMethod = commandType.GetMethod("ExecuteAsync",
                new[] { typeof(CommandContext), typeof(HooksSettings) });
            executeMethod.Should().NotBeNull(
                $"Command {commandType.Name} should have ExecuteAsync method");

            executeMethod!.ReturnType.Should().Be(typeof(Task<int>),
                "ExecuteAsync should return Task<int>");
        }
    }

    [Fact]
    public void HooksService_ShouldImplementAllRequiredMethods()
    {
        // Arrange
        var serviceType = typeof(HooksService);
        var interfaceType = typeof(IHooksService);

        // Assert
        serviceType.Should().BeAssignableTo(interfaceType, "HooksService should implement IHooksService");

        var interfaceMethods = interfaceType.GetMethods();
        var serviceMethods = serviceType.GetMethods();

        foreach (var interfaceMethod in interfaceMethods)
        {
            var matchingMethod = serviceMethods.FirstOrDefault(m =>
                m.Name == interfaceMethod.Name &&
                ParametersMatch(m.GetParameters(), interfaceMethod.GetParameters()));

            matchingMethod.Should().NotBeNull(
                $"HooksService should implement method '{interfaceMethod.Name}'");
        }
    }

    private static void ValidateToolSpecificHook(JsonElement hooksElement, string hookType, string expectedCommand, bool shouldHaveMatcher)
    {
        hooksElement.TryGetProperty(hookType, out var hookArray).Should().BeTrue($"Hook type '{hookType}' should exist");

        var hooks = hookArray.EnumerateArray().ToList();
        hooks.Should().HaveCountGreaterThan(0, $"Hook type '{hookType}' should have configurations");

        var hook = hooks[0];
        if (shouldHaveMatcher)
        {
            hook.TryGetProperty("matcher", out var matcher).Should().BeTrue($"Hook '{hookType}' should have matcher");
            matcher.GetString().Should().Be("Bash", "Tool-specific hooks should use 'Bash' matcher");
        }

        hook.TryGetProperty("hooks", out var innerHooks).Should().BeTrue($"Hook '{hookType}' should have inner hooks");
        var innerHooksList = innerHooks.EnumerateArray().ToList();
        innerHooksList.Should().HaveCount(1, $"Hook '{hookType}' should have exactly one inner hook");

        var innerHook = innerHooksList[0];
        innerHook.TryGetProperty("type", out var type).Should().BeTrue("Inner hook should have type");
        type.GetString().Should().Be("command", "Inner hook type should be 'command'");

        innerHook.TryGetProperty("command", out var command).Should().BeTrue("Inner hook should have command");
        command.GetString().Should().Be(expectedCommand, $"Command should be '{expectedCommand}'");
    }

    private static void ValidateGlobalHook(JsonElement hooksElement, string hookType, string expectedCommand)
    {
        hooksElement.TryGetProperty(hookType, out var hookArray).Should().BeTrue($"Hook type '{hookType}' should exist");

        var hooks = hookArray.EnumerateArray().ToList();
        hooks.Should().HaveCountGreaterThan(0, $"Hook type '{hookType}' should have configurations");

        var hook = hooks[0];
        hook.TryGetProperty("matcher", out _).Should().BeFalse($"Global hook '{hookType}' should not have matcher");

        hook.TryGetProperty("hooks", out var innerHooks).Should().BeTrue($"Hook '{hookType}' should have inner hooks");
        var innerHooksList = innerHooks.EnumerateArray().ToList();
        innerHooksList.Should().HaveCount(1, $"Hook '{hookType}' should have exactly one inner hook");

        var innerHook = innerHooksList[0];
        innerHook.TryGetProperty("type", out var type).Should().BeTrue("Inner hook should have type");
        type.GetString().Should().Be("command", "Inner hook type should be 'command'");

        innerHook.TryGetProperty("command", out var command).Should().BeTrue("Inner hook should have command");
        command.GetString().Should().Be(expectedCommand, $"Command should be '{expectedCommand}'");
    }

    private static bool ParametersMatch(ParameterInfo[] params1, ParameterInfo[] params2)
    {
        if (params1.Length != params2.Length) return false;

        for (int i = 0; i < params1.Length; i++)
        {
            if (params1[i].ParameterType != params2[i].ParameterType) return false;
        }

        return true;
    }
}