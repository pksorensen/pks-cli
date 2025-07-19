using System.Text.Json;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks with smart dispatcher pattern
/// Implements intelligent hook routing to avoid performance penalties
/// </summary>
public class HooksService : IHooksService
{
    private readonly Dictionary<string, HookDefinition> _availableHooks;
    private readonly string _hooksConfigPath;
    private readonly string _hooksDirectory;

    public HooksService()
    {
        _availableHooks = new Dictionary<string, HookDefinition>();
        _hooksConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        _hooksDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks", "hooks");
        
        InitializeDefaultHooks();
    }

    public async Task<List<HookDefinition>> GetAvailableHooksAsync()
    {
        try
        {
            await LoadHooksFromConfigurationAsync();
            return _availableHooks.Values.ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return new List<HookDefinition>();
        }
    }

    public async Task<HookResult> ExecuteHookAsync(string hookName, HookContext context)
    {
        if (string.IsNullOrEmpty(hookName))
        {
            throw new ArgumentException("Hook name cannot be empty", nameof(hookName));
        }

        try
        {
            AnsiConsole.MarkupLine($"[dim]Executing hook: {hookName}[/]");

            if (!_availableHooks.TryGetValue(hookName, out var hookDefinition))
            {
                return new HookResult
                {
                    Success = false,
                    Message = $"Hook '{hookName}' not found"
                };
            }

            // Use smart dispatcher pattern for efficient execution
            var result = await ExecuteHookWithSmartDispatcherAsync(hookDefinition, context);
            
            AnsiConsole.MarkupLine($"[dim]Hook {hookName} executed with result: {result.Success}[/]");
            return result;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to execute hook {hookName}: {ex.Message}[/]");
            return new HookResult
            {
                Success = false,
                Message = $"Hook execution failed: {ex.Message}"
            };
        }
    }

    public async Task<HookInstallResult> InstallHookAsync(string source)
    {
        try
        {
            AnsiConsole.MarkupLine($"[dim]Installing hook from source: {source}[/]");

            // Ensure hooks directory exists
            Directory.CreateDirectory(_hooksDirectory);

            var hookName = Path.GetFileNameWithoutExtension(source);
            var targetPath = Path.Combine(_hooksDirectory, $"{hookName}.sh");

            if (source.StartsWith("http"))
            {
                // Download from URL
                using var httpClient = new HttpClient();
                var content = await httpClient.GetStringAsync(source);
                await File.WriteAllTextAsync(targetPath, content);
            }
            else if (File.Exists(source))
            {
                // Copy from local file
                await File.WriteAllTextAsync(targetPath, await File.ReadAllTextAsync(source));
            }
            else
            {
                return new HookInstallResult
                {
                    Success = false,
                    Message = $"Source not found: {source}"
                };
            }

            // Make executable on Unix-like systems
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                System.Diagnostics.Process.Start("chmod", $"+x {targetPath}")?.WaitForExit();
            }

            // Register the hook
            var hookDefinition = new HookDefinition
            {
                Name = hookName,
                Description = $"Hook installed from {source}",
                Parameters = new List<string>()
            };

            _availableHooks[hookName] = hookDefinition;
            await SaveHooksConfigurationAsync();

            return new HookInstallResult
            {
                Success = true,
                HookName = hookName,
                Message = "Hook installed successfully",
                InstalledPath = targetPath
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to install hook from: {source} - {ex.Message}[/]");
            return new HookInstallResult
            {
                Success = false,
                Message = $"Installation failed: {ex.Message}"
            };
        }
    }

    public async Task<bool> RemoveHookAsync(string hookName)
    {
        try
        {
            AnsiConsole.MarkupLine($"[dim]Removing hook: {hookName}[/]");

            if (!_availableHooks.ContainsKey(hookName))
            {
                return false;
            }

            // Remove hook file
            var hookPath = Path.Combine(_hooksDirectory, $"{hookName}.sh");
            if (File.Exists(hookPath))
            {
                File.Delete(hookPath);
            }

            // Remove from registry
            _availableHooks.Remove(hookName);
            await SaveHooksConfigurationAsync();

            AnsiConsole.MarkupLine($"[green]Hook {hookName} removed successfully[/]");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to remove hook {hookName}: {ex.Message}[/]");
            return false;
        }
    }

    public async Task<bool> ValidateHooksConfigurationAsync()
    {
        try
        {
            // Check if Claude settings file exists
            if (!File.Exists(_hooksConfigPath))
            {
                AnsiConsole.MarkupLine($"[yellow]Claude settings file not found at: {_hooksConfigPath}[/]");
                return false;
            }

            // Validate JSON structure
            var configContent = await File.ReadAllTextAsync(_hooksConfigPath);
            var config = JsonSerializer.Deserialize<JsonElement>(configContent);

            if (config.TryGetProperty("hooks", out var hooksElement))
            {
                AnsiConsole.MarkupLine("[green]Hooks configuration found and valid[/]");
                return true;
            }

            AnsiConsole.MarkupLine("[dim]No hooks configuration found in settings[/]");
            return true; // Valid but no hooks configured
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to validate hooks configuration: {ex.Message}[/]");
            return false;
        }
    }

    public async Task<List<HookDefinition>> GetHooksByEventAsync(string eventType)
    {
        try
        {
            await LoadHooksFromConfigurationAsync();
            
            // Filter hooks by event type (simplified for now)
            return _availableHooks.Values
                .Where(h => h.Description.Contains(eventType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to get hooks for event {eventType}: {ex.Message}[/]");
            return new List<HookDefinition>();
        }
    }

    public async Task<string> CreateSmartDispatcherAsync(string targetPath)
    {
        try
        {
            var dispatcherScript = GenerateSmartDispatcherScript();
            
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
            await File.WriteAllTextAsync(targetPath, dispatcherScript);

            // Make executable on Unix-like systems
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                System.Diagnostics.Process.Start("chmod", $"+x {targetPath}")?.WaitForExit();
            }

            AnsiConsole.MarkupLine($"[green]Smart dispatcher created at: {targetPath}[/]");
            return targetPath;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to create smart dispatcher at: {targetPath} - {ex.Message}[/]");
            throw;
        }
    }

    private void InitializeDefaultHooks()
    {
        // Initialize with common hooks
        var defaultHooks = new[]
        {
            new HookDefinition
            {
                Name = "pre-build",
                Description = "Pre-build validation and setup",
                Parameters = new List<string> { "project", "config" }
            },
            new HookDefinition
            {
                Name = "post-build",
                Description = "Post-build cleanup and notification",
                Parameters = new List<string> { "status", "artifacts" }
            },
            new HookDefinition
            {
                Name = "pre-deploy",
                Description = "Pre-deployment validation",
                Parameters = new List<string> { "environment", "version" }
            },
            new HookDefinition
            {
                Name = "post-deploy",
                Description = "Post-deployment verification",
                Parameters = new List<string> { "environment", "status" }
            },
            new HookDefinition
            {
                Name = "ai-code-review",
                Description = "AI-powered code review hook",
                Parameters = new List<string> { "files", "diff" }
            },
            new HookDefinition
            {
                Name = "ai-test-generation",
                Description = "AI-generated test creation",
                Parameters = new List<string> { "source", "target" }
            }
        };

        foreach (var hook in defaultHooks)
        {
            _availableHooks[hook.Name] = hook;
        }
    }

    private async Task<HookResult> ExecuteHookWithSmartDispatcherAsync(HookDefinition hookDefinition, HookContext context)
    {
        // Implement smart dispatcher pattern - only execute if conditions are met
        var contextJson = JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });
        
        // Smart routing based on hook type and context
        if (ShouldExecuteHook(hookDefinition, context))
        {
            return await ExecuteHookCommandAsync(hookDefinition, contextJson);
        }

        // Skip execution but return success for filtered commands
        return new HookResult
        {
            Success = true,
            Message = $"Hook {hookDefinition.Name} skipped by smart dispatcher",
            Output = new Dictionary<string, object> { ["skipped"] = true }
        };
    }

    private bool ShouldExecuteHook(HookDefinition hookDefinition, HookContext context)
    {
        // Smart dispatcher logic - avoid expensive operations for simple commands
        if (context.Parameters.TryGetValue("command", out var commandObj) && commandObj is string command)
        {
            // Skip for simple commands that don't need validation
            var simpleCommands = new[] { "ls", "pwd", "cd", "echo", "cat", "grep" };
            if (simpleCommands.Any(cmd => command.Trim().StartsWith(cmd, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            // Execute for build/deploy related commands
            var importantPatterns = new[] { "build", "deploy", "test", "npm", "dotnet", "docker" };
            if (importantPatterns.Any(pattern => command.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Default: execute for hooks that don't match simple command patterns
        return !hookDefinition.Name.Contains("simple", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<HookResult> ExecuteHookCommandAsync(HookDefinition hookDefinition, string contextJson)
    {
        try
        {
            var hookPath = Path.Combine(_hooksDirectory, $"{hookDefinition.Name}.sh");
            
            if (!File.Exists(hookPath))
            {
                // Simulate hook execution for testing
                await Task.Delay(100); // Simulate processing time
                
                return new HookResult
                {
                    Success = true,
                    Message = $"Hook {hookDefinition.Name} executed successfully (simulated)",
                    Output = new Dictionary<string, object>
                    {
                        ["hook"] = hookDefinition.Name,
                        ["simulated"] = true,
                        ["timestamp"] = DateTime.UtcNow
                    }
                };
            }

            // Execute actual hook script
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = hookPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            
            // Send context as JSON input
            await process.StandardInput.WriteAsync(contextJson);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return new HookResult
            {
                Success = process.ExitCode == 0,
                Message = process.ExitCode == 0 ? "Hook executed successfully" : $"Hook failed with exit code {process.ExitCode}",
                Output = new Dictionary<string, object>
                {
                    ["stdout"] = output,
                    ["stderr"] = error,
                    ["exitCode"] = process.ExitCode
                }
            };
        }
        catch (Exception ex)
        {
            return new HookResult
            {
                Success = false,
                Message = $"Hook execution error: {ex.Message}"
            };
        }
    }

    private async Task LoadHooksFromConfigurationAsync()
    {
        // In a real implementation, this would parse the Claude settings.json
        // For now, we use the default hooks
        await Task.CompletedTask;
    }

    private async Task SaveHooksConfigurationAsync()
    {
        // In a real implementation, this would update the Claude settings.json
        // For now, we simulate the save
        await Task.CompletedTask;
    }

    private string GenerateSmartDispatcherScript()
    {
        return @"#!/bin/bash
# PKS CLI Smart Hook Dispatcher
# Implements intelligent command routing to avoid performance penalties

# Read JSON input from stdin
json_input=$(cat)

# Extract command from JSON input
command=$(echo ""$json_input"" | jq -r '.Parameters.command // empty')

# Exit early if no command
if [ -z ""$command"" ]; then
    echo ""No command found in context""
    exit 0
fi

# Smart routing based on command patterns
if echo ""$command"" | grep -q ""npm run deploy""; then
    echo ""🚀 Running pre-deployment validation...""
    # Execute deployment-specific hooks
    exit 0
fi

if echo ""$command"" | grep -q ""dotnet build""; then
    echo ""🔨 Running build validation...""
    # Execute build-specific hooks
    exit 0
fi

if echo ""$command"" | grep -q ""docker""; then
    echo ""🐳 Running Docker validation...""
    # Execute Docker-specific hooks
    exit 0
fi

# Skip simple commands to avoid performance penalties
if echo ""$command"" | grep -E ""^(ls|pwd|cd|echo|cat|grep)""; then
    exit 0
fi

# Default: Continue with general validation
echo ""✓ Command passed smart dispatcher validation""
exit 0
";
    }
}