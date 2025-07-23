using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling UserPromptSubmit hook events from Claude Code
/// This hook is called before Claude Code processes a user prompt
/// </summary>
public class UserPromptSubmitCommand : BaseHookCommand
{
    /// <summary>
    /// Process the UserPromptSubmit hook event
    /// For now, we just proceed without any special processing
    /// </summary>
    protected override async Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings)
    {
        // Read any context information from stdin (user prompt content)
        var stdinContent = await ReadStdinAsync();
        
        // For debugging in non-JSON mode, we can still show environment info
        if (!settings.Json)
        {
            await ShowDebugInformationAsync(stdinContent);
        }
        
        // For now, we don't filter or modify user prompts - just proceed
        // Future enhancements could analyze user prompts for content filtering
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
            AnsiConsole.MarkupLine("[dim]User Prompt Content:[/]");
            // Only show first 200 characters for privacy
            var preview = stdinContent.Length > 200 
                ? stdinContent[..200] + "..." 
                : stdinContent;
            AnsiConsole.WriteLine(preview);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No prompt content received[/]");
        }
        
        AnsiConsole.MarkupLine($"[dim]Working Directory:[/] {Directory.GetCurrentDirectory()}");
        
        await Task.CompletedTask;
    }
}