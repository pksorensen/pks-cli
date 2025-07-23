using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling Stop hook events from Claude Code
/// This hook is called when Claude Code stops responding or encounters an error
/// </summary>
public class StopCommand : BaseHookCommand
{
    /// <summary>
    /// Process the Stop hook event
    /// For now, we just acknowledge the stop event
    /// </summary>
    protected override async Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings)
    {
        // Read any context information from stdin (stop reason, error details)
        var stdinContent = await ReadStdinAsync();
        
        // For debugging in non-JSON mode, we can still show environment info
        if (!settings.Json)
        {
            await ShowDebugInformationAsync(stdinContent);
        }
        
        // For stop events, we typically just acknowledge - no decision needed
        // Future enhancements could log stop events or perform cleanup
        return HookDecision.Proceed();
    }
    
    /// <summary>
    /// Show debug information in non-JSON mode only
    /// </summary>
    private async Task ShowDebugInformationAsync(string? stdinContent)
    {
        AnsiConsole.MarkupLine("\n[yellow]Debug Information:[/]");
        
        if (!string.IsNullOrEmpty(stdinContent))
        {
            AnsiConsole.MarkupLine("[dim]Stop Event Details:[/]");
            AnsiConsole.WriteLine(stdinContent);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No stop details received[/]");
        }
        
        AnsiConsole.MarkupLine($"[dim]Working Directory:[/] {Directory.GetCurrentDirectory()}");
        
        await Task.CompletedTask;
    }
}