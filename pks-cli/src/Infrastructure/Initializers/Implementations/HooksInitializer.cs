using System.Text.Json;
using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Initializer for setting up Claude Code hooks system with smart dispatcher pattern
/// </summary>
public class HooksInitializer : TemplateInitializer
{
    public override string Id => "hooks";
    public override string Name => "Hooks System";
    public override string Description => "Sets up project hooks for automation and workflow integration with Claude Code";
    public override int Order => 60; // Run after basic project setup but before documentation

    protected override string TemplateDirectory => "hooks";

    public override async Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Run if hooks option is explicitly enabled
        return context.GetOption("hooks", false);
    }

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        var result = await base.ExecuteInternalAsync(context);
        
        if (!result.Success)
        {
            return result;
        }

        try
        {
            // Create hooks-specific directories and configuration
            await CreateHooksDirectoryAsync(context, result);
            await CreateHooksConfigurationAsync(context, result);
            await CreateDefaultHooksAsync(context, result);
            await CreateHooksReadmeAsync(context, result);

            // Add agentic hooks if agentic features are enabled
            if (context.GetOption("agentic", false))
            {
                await CreateAgenticHooksAsync(context, result);
            }

            result.Details = $"Hooks system configured successfully with {GetCreatedHooksCount(result)} hooks";
            return result;
        }
        catch (Exception ex)
        {
            return InitializationResult.CreateFailure($"Failed to setup hooks system: {ex.Message}", ex.ToString());
        }
    }

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new List<InitializerOption>
        {
            new InitializerOption
            {
                Name = "hooks",
                Description = "Enable hooks system for project automation and Claude Code integration",
                DefaultValue = false
            }
        };
    }

    private async Task CreateHooksDirectoryAsync(InitializationContext context, InitializationResult result)
    {
        var hooksDirectory = Path.Combine(context.TargetDirectory, "hooks");
        
        if (Directory.Exists(hooksDirectory) && !context.Force)
        {
            result.Warnings.Add("Hooks directory already exists");
            return;
        }

        EnsureDirectoryExists(hooksDirectory);
        result.AffectedFiles.Add(hooksDirectory + "/");
        result.Details = "Created hooks directory";
    }

    private async Task CreateHooksConfigurationAsync(InitializationContext context, InitializationResult result)
    {
        var configPath = Path.Combine(context.TargetDirectory, ".hooks.json");
        
        var config = new
        {
            hooks = new
            {
                PreToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "./hooks/smart-dispatcher.sh"
                            }
                        }
                    }
                },
                PostToolUse = new[]
                {
                    new
                    {
                        matcher = "Bash",
                        hooks = new[]
                        {
                            new
                            {
                                type = "command",
                                command = "./hooks/post-execution-validator.sh"
                            }
                        }
                    }
                }
            },
            smartDispatcher = new
            {
                enabled = true,
                performanceMode = true,
                skipPatterns = new[] { "ls", "pwd", "cd", "echo", "cat", "grep" },
                executePatterns = new[] { "build", "deploy", "test", "npm", "dotnet", "docker" }
            },
            agentic = context.GetOption("agentic", false) ? new
            {
                aiIntegration = true,
                codeReview = true,
                testGeneration = true
            } : null
        };

        var configJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await WriteFileAsync(configPath, configJson, context);
        
        result.AffectedFiles.Add(configPath);
        result.Details = "Created hooks configuration";
    }

    private async Task CreateDefaultHooksAsync(InitializationContext context, InitializationResult result)
    {
        var hooksDirectory = Path.Combine(context.TargetDirectory, "hooks");
        var template = context.Template.ToLowerInvariant();

        // Create smart dispatcher script
        await CreateSmartDispatcherScript(hooksDirectory, context, result);
        
        // Create template-specific hooks
        switch (template)
        {
            case "api":
                await CreateApiHooks(hooksDirectory, context, result);
                break;
            case "web":
                await CreateWebHooks(hooksDirectory, context, result);
                break;
            case "console":
                await CreateConsoleHooks(hooksDirectory, context, result);
                break;
            default:
                await CreateGenericHooks(hooksDirectory, context, result);
                break;
        }
    }

    private async Task CreateSmartDispatcherScript(string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        var scriptPath = Path.Combine(hooksDirectory, "smart-dispatcher.sh");
        var scriptContent = @"#!/bin/bash
# PKS CLI Smart Hook Dispatcher
# Generated by PKS CLI for {{ProjectName}}

# Read JSON input from stdin
json_input=$(cat)

# Extract command from JSON input
command=$(echo ""$json_input"" | jq -r '.command // empty')

# Exit early if no command
if [ -z ""$command"" ]; then
    exit 0
fi

# Smart routing based on command patterns
if echo ""$command"" | grep -q ""build""; then
    echo ""🔨 Running build validation for {{ProjectName}}...""
    ./hooks/pre-build.sh <<< ""$json_input""
    exit $?
fi

if echo ""$command"" | grep -q ""deploy""; then
    echo ""🚀 Running deployment validation for {{ProjectName}}...""
    ./hooks/pre-deploy.sh <<< ""$json_input""
    exit $?
fi

if echo ""$command"" | grep -q ""test""; then
    echo ""🧪 Running test validation for {{ProjectName}}...""
    ./hooks/pre-test.sh <<< ""$json_input""
    exit $?
fi

# Skip simple commands to avoid performance penalties
if echo ""$command"" | grep -E ""^(ls|pwd|cd|echo|cat|grep)""; then
    exit 0
fi

# Default: Continue with general validation
echo ""✓ Command validated by smart dispatcher""
exit 0
";

        var processedContent = ReplacePlaceholders(scriptContent, context);
        await WriteFileAsync(scriptPath, processedContent, context);
        result.AffectedFiles.Add(scriptPath);
    }

    private async Task CreateApiHooks(string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        var hooks = new Dictionary<string, string>
        {
            ["pre-build.sh"] = @"#!/bin/bash
echo ""🔨 Pre-build validation for {{ProjectName}} API""
# Add API-specific build validations here
exit 0",
            ["post-build.sh"] = @"#!/bin/bash
echo ""✅ Post-build tasks for {{ProjectName}} API""
# Add API-specific post-build tasks here
exit 0",
            ["pre-deploy.sh"] = @"#!/bin/bash
echo ""🚀 Pre-deployment validation for {{ProjectName}} API""
# Add API-specific deployment validations here
exit 0",
            ["post-deploy.sh"] = @"#!/bin/bash
echo ""🎉 Post-deployment tasks for {{ProjectName}} API""
# Add API-specific post-deployment tasks here
exit 0"
        };

        await CreateHookFiles(hooks, hooksDirectory, context, result);
    }

    private async Task CreateWebHooks(string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        var hooks = new Dictionary<string, string>
        {
            ["pre-build.sh"] = @"#!/bin/bash
echo ""🔨 Pre-build validation for {{ProjectName}} Web App""
# Add web-specific build validations here
exit 0",
            ["post-build.sh"] = @"#!/bin/bash
echo ""✅ Post-build tasks for {{ProjectName}} Web App""
# Add web-specific post-build tasks here
exit 0",
            ["pre-deploy.sh"] = @"#!/bin/bash
echo ""🚀 Pre-deployment validation for {{ProjectName}} Web App""
# Add web-specific deployment validations here
exit 0",
            ["post-deploy.sh"] = @"#!/bin/bash
echo ""🎉 Post-deployment tasks for {{ProjectName}} Web App""
# Add web-specific post-deployment tasks here
exit 0"
        };

        await CreateHookFiles(hooks, hooksDirectory, context, result);
    }

    private async Task CreateConsoleHooks(string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        var hooks = new Dictionary<string, string>
        {
            ["pre-build.sh"] = @"#!/bin/bash
echo ""🔨 Pre-build validation for {{ProjectName}} Console App""
# Add console-specific build validations here
exit 0",
            ["post-build.sh"] = @"#!/bin/bash
echo ""✅ Post-build tasks for {{ProjectName}} Console App""
# Add console-specific post-build tasks here
exit 0"
        };

        await CreateHookFiles(hooks, hooksDirectory, context, result);
    }

    private async Task CreateGenericHooks(string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        var hooks = new Dictionary<string, string>
        {
            ["pre-build.sh"] = @"#!/bin/bash
echo ""🔨 Pre-build validation for {{ProjectName}}""
# Add generic build validations here
exit 0",
            ["post-build.sh"] = @"#!/bin/bash
echo ""✅ Post-build tasks for {{ProjectName}}""
# Add generic post-build tasks here
exit 0"
        };

        await CreateHookFiles(hooks, hooksDirectory, context, result);
    }

    private async Task CreateAgenticHooksAsync(InitializationContext context, InitializationResult result)
    {
        var hooksDirectory = Path.Combine(context.TargetDirectory, "hooks");
        
        var agenticHooks = new Dictionary<string, string>
        {
            ["ai-code-review.sh"] = @"#!/bin/bash
echo ""🤖 AI Code Review for {{ProjectName}}""
# Integration with Claude Code for automated code review
# Add AI-powered code review logic here
exit 0",
            ["ai-test-generation.sh"] = @"#!/bin/bash
echo ""🧪 AI Test Generation for {{ProjectName}}""
# Integration with Claude Code for test generation
# Add AI-powered test generation logic here
exit 0",
            ["ai-documentation.sh"] = @"#!/bin/bash
echo ""📝 AI Documentation Generation for {{ProjectName}}""
# Integration with Claude Code for documentation generation
# Add AI-powered documentation logic here
exit 0"
        };

        await CreateHookFiles(agenticHooks, hooksDirectory, context, result);
        result.Details = "Created agentic AI integration hooks";
    }

    private async Task CreateHookFiles(Dictionary<string, string> hooks, string hooksDirectory, InitializationContext context, InitializationResult result)
    {
        foreach (var hook in hooks)
        {
            var hookPath = Path.Combine(hooksDirectory, hook.Key);
            var processedContent = ReplacePlaceholders(hook.Value, context);
            await WriteFileAsync(hookPath, processedContent, context);
            result.AffectedFiles.Add(hookPath);

            // Make executable on Unix-like systems
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                try
                {
                    System.Diagnostics.Process.Start("chmod", $"+x {hookPath}")?.WaitForExit();
                }
                catch
                {
                    // Ignore chmod errors in testing environments
                }
            }
        }
    }

    private async Task CreateHooksReadmeAsync(InitializationContext context, InitializationResult result)
    {
        var readmePath = Path.Combine(context.TargetDirectory, "hooks", "README.md");
        var readmeContent = @"# Hooks System

This directory contains hooks for {{ProjectName}} that integrate with Claude Code for intelligent automation.

## Available Hooks

### Smart Dispatcher
- **smart-dispatcher.sh**: Intelligent hook routing to avoid performance penalties
- Routes commands based on patterns to specific validation scripts
- Skips simple commands to maintain performance

### Build Hooks
- **pre-build.sh**: Runs before build operations
- **post-build.sh**: Runs after build completion

### Deployment Hooks  
- **pre-deploy.sh**: Runs before deployment operations
- **post-deploy.sh**: Runs after deployment completion

### AI Integration Hooks (if agentic features enabled)
- **ai-code-review.sh**: AI-powered code review automation
- **ai-test-generation.sh**: AI-powered test generation
- **ai-documentation.sh**: AI-powered documentation generation

## Usage

Hooks are automatically executed by Claude Code when configured in your `.claude/settings.json` file.

### Manual Execution
```bash
# Execute a specific hook
./hooks/pre-build.sh

# Execute with JSON context
echo '{""command"": ""dotnet build"", ""project"": ""{{ProjectName}}""}' | ./hooks/smart-dispatcher.sh
```

### Configuration

The hooks system uses smart dispatching to:
1. Route commands efficiently based on patterns
2. Skip unnecessary validations for simple commands
3. Execute targeted hooks for specific operations

## Performance

The smart dispatcher pattern ensures:
- Minimal overhead for simple commands
- Targeted execution for important operations
- Graceful handling of hook failures

For more information, see the PKS CLI documentation.
";

        var processedContent = ReplacePlaceholders(readmeContent, context);
        await WriteFileAsync(readmePath, processedContent, context);
        result.AffectedFiles.Add(readmePath);
        result.Details = "Created hooks documentation";
    }

    private int GetCreatedHooksCount(InitializationResult result)
    {
        return result.AffectedFiles.Count(f => f.EndsWith(".sh"));
    }

    protected override async Task PostProcessTemplateAsync(InitializationContext context, InitializationResult result)
    {
        // Make all shell scripts executable on Unix-like systems
        if (Environment.OSVersion.Platform == PlatformID.Unix)
        {
            var shellScripts = result.AffectedFiles.Where(f => f.EndsWith(".sh"));
            foreach (var script in shellScripts)
            {
                try
                {
                    System.Diagnostics.Process.Start("chmod", $"+x {script}")?.WaitForExit();
                }
                catch
                {
                    // Ignore chmod errors in testing environments
                }
            }
        }

        await base.PostProcessTemplateAsync(context, result);
    }
}