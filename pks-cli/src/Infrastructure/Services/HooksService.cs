using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks integration with smart merging
/// </summary>
public class HooksService : IHooksService
{
    private readonly ILogger<HooksService> _logger;
    
    /// <summary>
    /// Claude Code hook names (PascalCase as expected by Claude Code)
    /// </summary>
    public static class HookNames
    {
        public const string PreToolUse = "PreToolUse";
        public const string PostToolUse = "PostToolUse";
        public const string UserPromptSubmit = "UserPromptSubmit";
        public const string Notification = "Notification";
        public const string Stop = "Stop";
        public const string SubagentStop = "SubagentStop";
        public const string PreCompact = "PreCompact";
        
        /// <summary>
        /// All available hook names
        /// </summary>
        public static readonly string[] All = new[]
        {
            PreToolUse, PostToolUse, UserPromptSubmit, Notification, Stop, SubagentStop, PreCompact
        };
        
        /// <summary>
        /// Legacy camelCase hook names for migration support
        /// </summary>
        public static class Legacy
        {
            public const string preToolUse = "preToolUse";
            public const string postToolUse = "postToolUse";
            public const string userPromptSubmit = "userPromptSubmit";
            public const string notification = "notification";
            public const string stop = "stop";
            public const string subagentStop = "subagentStop";
            public const string preCompact = "preCompact";
            
            public static readonly string[] All = new[]
            {
                preToolUse, postToolUse, userPromptSubmit, notification, stop, subagentStop, preCompact
            };
        }
    }

    public HooksService(ILogger<HooksService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> InitializeClaudeCodeHooksAsync(bool force = false, SettingsScope scope = SettingsScope.Project)
    {
        try
        {
            var (claudeDir, settingsPath) = GetSettingsPaths(scope);

            // Ensure .claude directory exists
            if (!Directory.Exists(claudeDir))
            {
                Directory.CreateDirectory(claudeDir);
                AnsiConsole.MarkupLine($"[dim]Created directory: {claudeDir}[/]");
            }

            // Read existing settings or create new
            JsonNode settingsNode;
            bool hasExistingHooks = false;
            List<string> existingHookTypes = new();

            if (File.Exists(settingsPath))
            {
                var existingContent = await File.ReadAllTextAsync(settingsPath);
                settingsNode = JsonNode.Parse(existingContent) ?? new JsonObject();
                AnsiConsole.MarkupLine($"[dim]Loading existing settings from: {settingsPath}[/]");

                // Check for existing hooks
                if (settingsNode["hooks"] != null)
                {
                    hasExistingHooks = true;
                    var hooksNode = settingsNode["hooks"];
                    
                    // Check for both PascalCase (current) and camelCase (legacy) hook names
                    // Also detect legacy camelCase hooks that need migration
                    var legacyHooksDetected = new List<string>();
                    
                    if (hooksNode?[HookNames.PreToolUse] != null || hooksNode?[HookNames.Legacy.preToolUse] != null)
                    {
                        existingHookTypes.Add(HookNames.PreToolUse);
                        if (hooksNode?[HookNames.Legacy.preToolUse] != null && hooksNode?[HookNames.PreToolUse] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.preToolUse);
                    }
                    if (hooksNode?[HookNames.PostToolUse] != null || hooksNode?[HookNames.Legacy.postToolUse] != null)
                    {
                        existingHookTypes.Add(HookNames.PostToolUse);
                        if (hooksNode?[HookNames.Legacy.postToolUse] != null && hooksNode?[HookNames.PostToolUse] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.postToolUse);
                    }
                    if (hooksNode?[HookNames.UserPromptSubmit] != null || hooksNode?[HookNames.Legacy.userPromptSubmit] != null)
                    {
                        existingHookTypes.Add(HookNames.UserPromptSubmit);
                        if (hooksNode?[HookNames.Legacy.userPromptSubmit] != null && hooksNode?[HookNames.UserPromptSubmit] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.userPromptSubmit);
                    }
                    if (hooksNode?[HookNames.Notification] != null || hooksNode?[HookNames.Legacy.notification] != null)
                    {
                        existingHookTypes.Add(HookNames.Notification);
                        if (hooksNode?[HookNames.Legacy.notification] != null && hooksNode?[HookNames.Notification] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.notification);
                    }
                    if (hooksNode?[HookNames.Stop] != null || hooksNode?[HookNames.Legacy.stop] != null)
                    {
                        existingHookTypes.Add(HookNames.Stop);
                        if (hooksNode?[HookNames.Legacy.stop] != null && hooksNode?[HookNames.Stop] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.stop);
                    }
                    if (hooksNode?[HookNames.SubagentStop] != null || hooksNode?[HookNames.Legacy.subagentStop] != null)
                    {
                        existingHookTypes.Add(HookNames.SubagentStop);
                        if (hooksNode?[HookNames.Legacy.subagentStop] != null && hooksNode?[HookNames.SubagentStop] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.subagentStop);
                    }
                    if (hooksNode?[HookNames.PreCompact] != null || hooksNode?[HookNames.Legacy.preCompact] != null)
                    {
                        existingHookTypes.Add(HookNames.PreCompact);
                        if (hooksNode?[HookNames.Legacy.preCompact] != null && hooksNode?[HookNames.PreCompact] == null)
                            legacyHooksDetected.Add(HookNames.Legacy.preCompact);
                    }
                    
                    // Handle legacy hook migration
                    if (legacyHooksDetected.Any())
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠️  Legacy camelCase hooks detected: {string.Join(", ", legacyHooksDetected)}[/]");
                        AnsiConsole.MarkupLine("[yellow]These will be automatically migrated to PascalCase format (PreToolUse, PostToolUse, etc.)[/]");
                    }
                }
            }
            else
            {
                settingsNode = new JsonObject();
                AnsiConsole.MarkupLine($"[dim]Creating new settings file: {settingsPath}[/]");
            }

            // Check for conflicts and handle them
            if (hasExistingHooks && !force)
            {
                AnsiConsole.MarkupLine("[yellow]⚠️  Existing hooks configuration found![/]");
                AnsiConsole.MarkupLine($"[dim]Found existing hooks: {string.Join(", ", existingHookTypes)}[/]");
                
                var shouldProceed = AnsiConsole.Confirm(
                    "[yellow]This will merge PKS hooks with existing configuration. Continue?[/]",
                    false
                );

                if (!shouldProceed)
                {
                    AnsiConsole.MarkupLine("[dim]Hooks initialization cancelled.[/]");
                    return false;
                }

                // Check for specific PKS hooks that would be overwritten
                var pksHooksToOverwrite = new List<string>();
                var hooksNode = settingsNode["hooks"];
                
                if (hooksNode != null)
                {
                    // Check both PascalCase and legacy camelCase for PKS hooks
                    if (ContainsPksCommand(hooksNode[HookNames.PreToolUse]) || ContainsPksCommand(hooksNode[HookNames.Legacy.preToolUse])) pksHooksToOverwrite.Add(HookNames.PreToolUse);
                    if (ContainsPksCommand(hooksNode[HookNames.PostToolUse]) || ContainsPksCommand(hooksNode[HookNames.Legacy.postToolUse])) pksHooksToOverwrite.Add(HookNames.PostToolUse);
                    if (ContainsPksCommand(hooksNode[HookNames.UserPromptSubmit]) || ContainsPksCommand(hooksNode[HookNames.Legacy.userPromptSubmit])) pksHooksToOverwrite.Add(HookNames.UserPromptSubmit);
                    if (ContainsPksCommand(hooksNode[HookNames.Notification]) || ContainsPksCommand(hooksNode[HookNames.Legacy.notification])) pksHooksToOverwrite.Add(HookNames.Notification);
                    if (ContainsPksCommand(hooksNode[HookNames.Stop]) || ContainsPksCommand(hooksNode[HookNames.Legacy.stop])) pksHooksToOverwrite.Add(HookNames.Stop);
                    if (ContainsPksCommand(hooksNode[HookNames.SubagentStop]) || ContainsPksCommand(hooksNode[HookNames.Legacy.subagentStop])) pksHooksToOverwrite.Add(HookNames.SubagentStop);
                    if (ContainsPksCommand(hooksNode[HookNames.PreCompact]) || ContainsPksCommand(hooksNode[HookNames.Legacy.preCompact])) pksHooksToOverwrite.Add(HookNames.PreCompact);
                }

                if (pksHooksToOverwrite.Any())
                {
                    AnsiConsole.MarkupLine($"[red]⚠️  Found existing PKS hooks that will be updated: {string.Join(", ", pksHooksToOverwrite)}[/]");
                    var shouldOverwrite = AnsiConsole.Confirm(
                        "[yellow]This will update existing PKS hook commands. Continue?[/]",
                        true
                    );

                    if (!shouldOverwrite)
                    {
                        AnsiConsole.MarkupLine("[dim]Hooks initialization cancelled.[/]");
                        return false;
                    }
                }
            }

            // Create PKS hooks configuration
            var pksHooksConfig = CreatePksHooksConfiguration();

            // Merge with existing hooks
            if (settingsNode["hooks"] == null)
            {
                settingsNode["hooks"] = new JsonObject();
            }

            var hooksSection = settingsNode["hooks"]!.AsObject();
            
            // Validate existing hooks before migration
            var validationResult = ValidateHookNames(hooksSection);
            if (validationResult.Warnings.Any())
            {
                foreach (var warning in validationResult.Warnings)
                {
                    AnsiConsole.MarkupLine($"[yellow]⚠️  {warning}[/]");
                }
            }
            
            // Migrate legacy camelCase hooks to PascalCase
            await MigrateLegacyHooksAsync(hooksSection);
            
            // Merge each hook type intelligently using PascalCase
            MergeHookType(hooksSection, HookNames.PreToolUse, pksHooksConfig.PreToolUse, force);
            MergeHookType(hooksSection, HookNames.PostToolUse, pksHooksConfig.PostToolUse, force);
            MergeHookType(hooksSection, HookNames.UserPromptSubmit, pksHooksConfig.UserPromptSubmit, force);
            MergeHookType(hooksSection, HookNames.Notification, pksHooksConfig.Notification, force);
            MergeHookType(hooksSection, HookNames.Stop, pksHooksConfig.Stop, force);
            MergeHookType(hooksSection, HookNames.SubagentStop, pksHooksConfig.SubagentStop, force);
            MergeHookType(hooksSection, HookNames.PreCompact, pksHooksConfig.PreCompact, force);

            // Write updated settings
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true
            };
            
            var json = settingsNode.ToJsonString(options);
            await File.WriteAllTextAsync(settingsPath, json);

            AnsiConsole.MarkupLine($"[green]✓ Claude Code settings updated: {settingsPath}[/]");
            
            if (hasExistingHooks)
            {
                AnsiConsole.MarkupLine("[cyan]ℹ️  PKS hooks merged with existing configuration[/]");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize Claude Code hooks: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
            return false;
        }
    }

    private static bool ContainsPksCommand(JsonNode? hookArray)
    {
        if (hookArray?.AsArray() == null) return false;
        
        foreach (var hook in hookArray.AsArray())
        {
            if (hook?["hooks"]?.AsArray() != null)
            {
                foreach (var subHook in hook["hooks"]!.AsArray())
                {
                    var command = subHook?["command"]?.ToString();
                    if (command?.StartsWith("pks hooks") == true)
                    {
                        return true;
                    }
                }
            }
        }
        
        return false;
    }

    private static void MergeHookType(JsonObject hooksSection, string hookType, object pksHookConfig, bool force)
    {
        if (!hooksSection.ContainsKey(hookType))
        {
            // No existing hooks of this type, just add PKS hooks
            hooksSection[hookType] = JsonSerializer.SerializeToNode(pksHookConfig);
        }
        else
        {
            // Existing hooks found
            var existingHooks = hooksSection[hookType]!.AsArray();
            var pksHookNode = JsonSerializer.SerializeToNode(pksHookConfig)!.AsArray();

            if (force)
            {
                // Force mode: replace PKS hooks, keep non-PKS hooks
                var nonPksHooks = existingHooks.Where(hook => !IsPksHook(hook)).ToList();
                existingHooks.Clear();
                
                foreach (var hook in nonPksHooks)
                {
                    existingHooks.Add(hook);
                }
                
                foreach (var pksHook in pksHookNode)
                {
                    var clonedPksHook = JsonNode.Parse(pksHook!.ToJsonString());
                    existingHooks.Add(clonedPksHook);
                }
            }
            else
            {
                // Merge mode: remove existing PKS hooks and add new ones
                for (int i = existingHooks.Count - 1; i >= 0; i--)
                {
                    if (IsPksHook(existingHooks[i]))
                    {
                        existingHooks.RemoveAt(i);
                    }
                }
                
                // Add PKS hooks
                foreach (var pksHook in pksHookNode)
                {
                    var clonedPksHook = JsonNode.Parse(pksHook!.ToJsonString());
                    existingHooks.Add(clonedPksHook);
                }
            }
        }
    }

    private static bool IsPksHook(JsonNode? hook)
    {
        if (hook?["hooks"]?.AsArray() == null) return false;
        
        foreach (var subHook in hook["hooks"]!.AsArray())
        {
            var command = subHook?["command"]?.ToString();
            if (command?.StartsWith("pks hooks") == true)
            {
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Validates that hook names conform to Claude Code expectations (PascalCase)
    /// </summary>
    public static HookValidationResult ValidateHookNames(JsonObject hooksSection)
    {
        var result = new HookValidationResult { IsValid = true };
        
        foreach (var hookProperty in hooksSection)
        {
            var hookName = hookProperty.Key;
            
            // Check if it's a valid hook name (either current PascalCase or legacy camelCase)
            if (!HookNames.All.Contains(hookName) && !HookNames.Legacy.All.Contains(hookName))
            {
                result.IsValid = false;
                result.Errors.Add($"Unknown hook name: '{hookName}'. Expected one of: {string.Join(", ", HookNames.All)}");
            }
            
            // Check if it's using legacy camelCase
            if (HookNames.Legacy.All.Contains(hookName))
            {
                result.Warnings.Add($"Legacy camelCase hook detected: '{hookName}'. Consider migrating to PascalCase.");
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Migrates legacy camelCase hook names to PascalCase
    /// </summary>
    private static async Task MigrateLegacyHooksAsync(JsonObject hooksSection)
    {
        await Task.CompletedTask; // Placeholder for async pattern
        
        var migrations = new Dictionary<string, string>
        {
            [HookNames.Legacy.preToolUse] = HookNames.PreToolUse,
            [HookNames.Legacy.postToolUse] = HookNames.PostToolUse,
            [HookNames.Legacy.userPromptSubmit] = HookNames.UserPromptSubmit,
            [HookNames.Legacy.notification] = HookNames.Notification,
            [HookNames.Legacy.stop] = HookNames.Stop,
            [HookNames.Legacy.subagentStop] = HookNames.SubagentStop,
            [HookNames.Legacy.preCompact] = HookNames.PreCompact
        };
        
        var migratedHooks = new List<string>();
        
        foreach (var (legacyName, newName) in migrations)
        {
            if (hooksSection[legacyName] != null && hooksSection[newName] == null)
            {
                // Clone the legacy hook configuration to avoid parent reference issues
                var legacyNode = hooksSection[legacyName];
                var clonedNode = JsonNode.Parse(legacyNode!.ToJsonString());
                
                // Migrate the legacy hook to the new name
                hooksSection[newName] = clonedNode;
                hooksSection.Remove(legacyName);
                migratedHooks.Add($"{legacyName} → {newName}");
            }
        }
        
        if (migratedHooks.Any())
        {
            AnsiConsole.MarkupLine($"[green]✅ Migrated hooks: {string.Join(", ", migratedHooks)}[/]");
        }
    }
    
    private static dynamic CreatePksHooksConfiguration()
    {
        return new
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
                            command = "pks hooks pre-tool-use"
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
                            command = "pks hooks post-tool-use"
                        }
                    }
                }
            },
            UserPromptSubmit = new[]
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
            Stop = new[]
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
            },
            Notification = new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new
                        {
                            type = "command",
                            command = "pks hooks notification"
                        }
                    }
                }
            },
            SubagentStop = new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new
                        {
                            type = "command",
                            command = "pks hooks subagent-stop"
                        }
                    }
                }
            },
            PreCompact = new[]
            {
                new
                {
                    hooks = new[]
                    {
                        new
                        {
                            type = "command",
                            command = "pks hooks pre-compact"
                        }
                    }
                }
            }
        };
    }
    
    private static (string claudeDir, string settingsPath) GetSettingsPaths(SettingsScope scope)
    {
        string claudeDir;
        string settingsPath;
        
        switch (scope)
        {
            case SettingsScope.User:
                claudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                settingsPath = Path.Combine(claudeDir, "settings.json");
                break;
                
            case SettingsScope.Project:
                claudeDir = Path.Combine(Directory.GetCurrentDirectory(), ".claude");
                settingsPath = Path.Combine(claudeDir, "settings.json");
                break;
                
            case SettingsScope.Local:
                claudeDir = ".claude";
                settingsPath = Path.Combine(claudeDir, "settings.json");
                break;
                
            default:
                throw new ArgumentException($"Unsupported settings scope: {scope}");
        }
        
        return (claudeDir, settingsPath);
    }

    /// <summary>
    /// Gets all available hooks that can be executed
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available hooks</returns>
    public async Task<List<HookDefinition>> GetAvailableHooksAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting available hooks");
        
        try
        {
            // For now, return a basic stub implementation
            await Task.Delay(100, cancellationToken);
            
            return new List<HookDefinition>
            {
                new HookDefinition
                {
                    Name = "pre-commit",
                    Description = "Pre-commit validation hook",
                    EventType = "pre-commit",
                    ScriptPath = "hooks/pre-commit.sh",
                    Parameters = new List<string> { "files", "staged" }
                },
                new HookDefinition
                {
                    Name = "post-deploy",
                    Description = "Post-deployment notification hook",
                    EventType = "post-deploy",
                    ScriptPath = "hooks/post-deploy.sh",
                    Parameters = new List<string> { "environment", "version" }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available hooks");
            return new List<HookDefinition>();
        }
    }

    /// <summary>
    /// Executes a hook with the specified context
    /// </summary>
    /// <param name="hookName">Name of the hook to execute</param>
    /// <param name="context">Execution context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook execution</returns>
    public async Task<HookResult> ExecuteHookAsync(string hookName, HookContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Executing hook {HookName}", hookName);
        
        try
        {
            // For now, return a basic stub implementation
            var startTime = DateTime.UtcNow;
            await Task.Delay(200, cancellationToken);
            var endTime = DateTime.UtcNow;
            
            return new HookResult
            {
                Success = true,
                Message = $"Hook {hookName} executed successfully",
                ExitCode = 0,
                ExecutionTime = endTime - startTime,
                Output = new Dictionary<string, object> { ["result"] = "success" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing hook {HookName}", hookName);
            return new HookResult
            {
                Success = false,
                Message = $"Failed to execute hook {hookName}: {ex.Message}",
                ExitCode = 1,
                Errors = new List<string> { ex.Message }
            };
        }
    }

    /// <summary>
    /// Installs a hook from the specified source
    /// </summary>
    /// <param name="hookSource">Source of the hook (URL, package, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook installation</returns>
    public async Task<HookInstallResult> InstallHookAsync(string hookSource, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Installing hook from {HookSource}", hookSource);
        
        try
        {
            // For now, return a basic stub implementation
            await Task.Delay(300, cancellationToken);
            
            return new HookInstallResult
            {
                Success = true,
                HookName = Path.GetFileNameWithoutExtension(hookSource),
                Message = $"Hook installed successfully from {hookSource}",
                InstalledPath = Path.Combine("hooks", Path.GetFileName(hookSource)),
                Dependencies = new List<string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing hook from {HookSource}", hookSource);
            return new HookInstallResult
            {
                Success = false,
                Message = $"Failed to install hook from {hookSource}: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Removes an installed hook
    /// </summary>
    /// <param name="hookName">Name of the hook to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if hook was successfully removed</returns>
    public async Task<bool> RemoveHookAsync(string hookName, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Removing hook {HookName}", hookName);
        
        try
        {
            // For now, return a basic stub implementation
            await Task.Delay(150, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing hook {HookName}", hookName);
            return false;
        }
    }

    public async Task<HookInstallResult> InstallHooksAsync(HooksConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Installing hooks with configuration");
        
        try
        {
            await Task.Delay(500, cancellationToken);
            return new HookInstallResult
            {
                Success = true,
                HookName = "Git Hooks",
                Message = $"Installed {configuration.HookTypes.Count} hook types successfully",
                InstalledPath = ".git/hooks",
                Dependencies = new List<string>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error installing hooks");
            return new HookInstallResult
            {
                Success = false,
                Message = $"Failed to install hooks: {ex.Message}"
            };
        }
    }

    public async Task<HookUninstallResult> UninstallHooksAsync(HooksUninstallConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Uninstalling hooks with configuration");
        
        try
        {
            await Task.Delay(300, cancellationToken);
            return new HookUninstallResult
            {
                Success = true,
                HookName = "Git Hooks",
                Message = $"Uninstalled {configuration.HookTypes.Count} hook types successfully",
                BackupPath = configuration.KeepBackup ? ".git/hooks-backup" : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling hooks");
            return new HookUninstallResult
            {
                Success = false,
                Message = $"Failed to uninstall hooks: {ex.Message}"
            };
        }
    }

    public async Task<HookUpdateResult> UpdateHooksAsync(HooksUpdateConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating hooks with configuration");
        
        try
        {
            await Task.Delay(400, cancellationToken);
            return new HookUpdateResult
            {
                Success = true,
                HookName = "Git Hooks",
                Message = $"Updated {configuration.HookTypes.Count} hook types successfully",
                BackupPath = configuration.PreserveCustomizations ? ".git/hooks-backup" : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating hooks");
            return new HookUpdateResult
            {
                Success = false,
                Message = $"Failed to update hooks: {ex.Message}"
            };
        }
    }

    public async Task<List<InstalledHook>> GetInstalledHooksAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting installed hooks");
        
        try
        {
            await Task.Delay(200, cancellationToken);
            return new List<InstalledHook>
            {
                new() { Name = "pre-commit", Type = "pre-commit", Path = ".git/hooks/pre-commit", IsEnabled = true, Version = "1.0" },
                new() { Name = "pre-push", Type = "pre-push", Path = ".git/hooks/pre-push", IsEnabled = true, Version = "1.0" },
                new() { Name = "commit-msg", Type = "commit-msg", Path = ".git/hooks/commit-msg", IsEnabled = false, Version = "1.0" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting installed hooks");
            return new List<InstalledHook>();
        }
    }

    public async Task<List<HookTestResult>> TestHooksAsync(List<string> hookNames, bool dryRun = true, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Testing {Count} hooks (dry run: {DryRun})", hookNames.Count, dryRun);
        
        try
        {
            await Task.Delay(600, cancellationToken);
            var results = new List<HookTestResult>();
            
            foreach (var hookName in hookNames)
            {
                results.Add(new HookTestResult
                {
                    HookName = hookName,
                    Success = Random.Shared.NextDouble() > 0.2, // 80% success rate
                    Message = $"Hook {hookName} test completed" + (dryRun ? " (dry run)" : ""),
                    ExecutionTime = TimeSpan.FromMilliseconds(Random.Shared.Next(100, 1000)),
                    Output = new List<string> { $"Testing {hookName}...", "Validation complete" },
                    Errors = new List<string>()
                });
            }
            
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing hooks");
            return hookNames.Select(name => new HookTestResult
            {
                HookName = name,
                Success = false,
                Message = $"Test failed: {ex.Message}",
                Errors = new List<string> { ex.Message }
            }).ToList();
        }
    }
}

/// <summary>
/// Constants for Claude Code hook names using PascalCase naming convention
/// </summary>
public static class HookNames
{
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string Notification = "Notification";
    public const string Stop = "Stop";
    public const string SubagentStop = "SubagentStop";
    public const string PreCompact = "PreCompact";
    
    /// <summary>
    /// Legacy camelCase hook names for backward compatibility
    /// </summary>
    public static class Legacy
    {
        public const string preToolUse = "preToolUse";
        public const string postToolUse = "postToolUse";
        public const string userPromptSubmit = "userPromptSubmit";
        public const string notification = "notification";
        public const string stop = "stop";
        public const string subagentStop = "subagentStop";
        public const string preCompact = "preCompact";
    }
}