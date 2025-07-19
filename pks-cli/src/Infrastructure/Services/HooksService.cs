using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks integration with smart merging
/// </summary>
public class HooksService : IHooksService
{
    public async Task<bool> InitializeClaudeCodeHooksAsync(bool force = false)
    {
        try
        {
            var claudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            // Ensure .claude directory exists
            if (!Directory.Exists(claudeDir))
            {
                Directory.CreateDirectory(claudeDir);
                AnsiConsole.MarkupLine($"[dim]Created directory: {claudeDir}[/]");
            }

            // Read existing settings or create new
            JsonNode? settingsNode = null;
            bool hasExistingHooks = false;
            List<string> existingHookTypes = new();

            if (File.Exists(settingsPath))
            {
                var existingContent = await File.ReadAllTextAsync(settingsPath);
                settingsNode = JsonNode.Parse(existingContent);
                AnsiConsole.MarkupLine($"[dim]Loading existing settings from: {settingsPath}[/]");

                // Check for existing hooks
                if (settingsNode?["hooks"] != null)
                {
                    hasExistingHooks = true;
                    var hooksNode = settingsNode["hooks"];
                    
                    if (hooksNode?["preToolUse"] != null) existingHookTypes.Add("PreToolUse");
                    if (hooksNode?["postToolUse"] != null) existingHookTypes.Add("PostToolUse");
                    if (hooksNode?["userPromptSubmit"] != null) existingHookTypes.Add("UserPromptSubmit");
                    if (hooksNode?["stop"] != null) existingHookTypes.Add("Stop");
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
                
                if (ContainsPksCommand(hooksNode?["preToolUse"])) pksHooksToOverwrite.Add("PreToolUse");
                if (ContainsPksCommand(hooksNode?["postToolUse"])) pksHooksToOverwrite.Add("PostToolUse");
                if (ContainsPksCommand(hooksNode?["userPromptSubmit"])) pksHooksToOverwrite.Add("UserPromptSubmit");
                if (ContainsPksCommand(hooksNode?["stop"])) pksHooksToOverwrite.Add("Stop");

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
            
            // Merge each hook type intelligently
            MergeHookType(hooksSection, "preToolUse", pksHooksConfig.PreToolUse, force);
            MergeHookType(hooksSection, "postToolUse", pksHooksConfig.PostToolUse, force);
            MergeHookType(hooksSection, "userPromptSubmit", pksHooksConfig.UserPromptSubmit, force);
            MergeHookType(hooksSection, "stop", pksHooksConfig.Stop, force);

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
                    existingHooks.Add(pksHook);
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
                    existingHooks.Add(pksHook);
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
            }
        };
    }
}