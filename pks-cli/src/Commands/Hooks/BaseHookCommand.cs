using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Attributes;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Base class for all Claude Code hook commands
/// Provides common functionality for JSON output and silent operation
/// </summary>
[SkipFirstTimeWarning]
public abstract class BaseHookCommand : AsyncCommand<HooksSettings>
{
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Executes the hook command with proper output handling
    /// </summary>
    public override async Task<int> ExecuteAsync(CommandContext context, HooksSettings settings)
    {
        try
        {
            // Process the hook event
            var decision = await ProcessHookEventAsync(context, settings);

            // Output the result
            await OutputResultAsync(decision, settings);

            return 0; // Success
        }
        catch (Exception ex)
        {
            if (settings.Json)
            {
                // For JSON mode, output error as JSON and exit with error code
                var errorDecision = new HookDecision
                {
                    Continue = false,
                    StopReason = $"Hook execution failed: {ex.Message}"
                };

                var json = JsonSerializer.Serialize(errorDecision, JsonOptions);
                Console.WriteLine(json);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            return 1; // Error
        }
    }

    /// <summary>
    /// Process the specific hook event - to be implemented by derived classes
    /// </summary>
    protected abstract Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings);

    /// <summary>
    /// Output the hook result in appropriate format
    /// </summary>
    protected virtual async Task OutputResultAsync(HookDecision decision, HooksSettings settings)
    {
        if (settings.Json)
        {
            // JSON output mode - only output JSON if there's a decision
            if (ShouldOutputJson(decision))
            {
                var json = JsonSerializer.Serialize(decision, JsonOptions);
                Console.WriteLine(json);
            }
            // Otherwise output nothing (proceed silently)
        }
        else
        {
            // Regular UI mode - show user-friendly output
            await DisplayUserFriendlyOutputAsync(decision);
        }
    }

    /// <summary>
    /// Determines whether JSON should be output for this decision
    /// </summary>
    protected virtual bool ShouldOutputJson(HookDecision decision)
    {
        // Output JSON only if there's an explicit decision or control instruction
        return !string.IsNullOrEmpty(decision.Decision) ||
               decision.Continue == false ||
               decision.SuppressOutput == true ||
               !string.IsNullOrEmpty(decision.StopReason);
    }

    /// <summary>
    /// Display user-friendly output for non-JSON mode
    /// </summary>
    protected virtual async Task DisplayUserFriendlyOutputAsync(HookDecision decision)
    {
        var hookName = GetType().Name.Replace("Command", "");

        AnsiConsole.MarkupLine($"[cyan]PKS Hooks: {hookName} Event Triggered[/]");

        // Display environment variables
        AnsiConsole.MarkupLine("\n[yellow]Environment Variables:[/]");
        var envVars = Environment.GetEnvironmentVariables();
        foreach (var key in envVars.Keys)
        {
            var value = envVars[key];
            AnsiConsole.MarkupLine($"[dim]{key} = {value}[/]");
        }

        // Display command line arguments
        AnsiConsole.MarkupLine("\n[yellow]Command Line Arguments:[/]");
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            AnsiConsole.MarkupLine($"[dim]args[{i}] = {args[i]}[/]");
        }

        // Display working directory
        AnsiConsole.MarkupLine($"\n[yellow]Working Directory:[/]");
        AnsiConsole.MarkupLine($"[dim]{Directory.GetCurrentDirectory()}[/]");

        // Display STDIN input information
        AnsiConsole.MarkupLine("\n[yellow]STDIN Input:[/]");
        var stdinContent = await ReadStdinAsync();
        if (!string.IsNullOrEmpty(stdinContent))
        {
            AnsiConsole.MarkupLine($"[dim]{stdinContent}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No piped input detected[/]");
        }

        if (!string.IsNullOrEmpty(decision.Decision))
        {
            var color = decision.Decision switch
            {
                "approve" => "green",
                "block" => "red",
                _ => "yellow"
            };

            AnsiConsole.MarkupLine($"\n[{color}]Decision: {decision.Decision}[/]");

            if (!string.IsNullOrEmpty(decision.Message))
            {
                AnsiConsole.MarkupLine($"[dim]Message: {decision.Message}[/]");
            }
        }

        if (decision.Continue == false)
        {
            AnsiConsole.MarkupLine($"\n[red]Continue: false[/]");
            if (!string.IsNullOrEmpty(decision.StopReason))
            {
                AnsiConsole.MarkupLine($"[dim]Reason: {decision.StopReason}[/]");
            }
        }

        AnsiConsole.MarkupLine($"\n[green]✓ {hookName} hook completed successfully[/]");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Read input from stdin if available (for hook context)
    /// </summary>
    protected async Task<string?> ReadStdinAsync()
    {
        try
        {
            if (Console.IsInputRedirected)
            {
                return await Console.In.ReadToEndAsync();
            }
        }
        catch
        {
            // Ignore stdin read errors in hook context
        }

        return null;
    }
}