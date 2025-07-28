using PKS.Infrastructure.Attributes;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling PostToolUse hook events from Claude Code
/// This hook is called after Claude Code executes a tool and can process the results
/// </summary>
[SkipFirstTimeWarning]
public class PostToolUseCommand : BaseHookCommand
{
    /// <summary>
    /// Process the PostToolUse hook event
    /// For now, we just proceed without any special processing
    /// </summary>
    protected override async Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings)
    {
        // Read any context information from stdin (tool execution results)
        var stdinContent = await ReadStdinAsync();

        // For debugging in non-JSON mode, we can still show environment info
        if (!settings.Json)
        {
            await ShowDebugInformationAsync(stdinContent);
        }

        // For now, we don't process tool results - just proceed
        // Future enhancements could analyze tool execution results and provide feedback
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
            AnsiConsole.MarkupLine("[dim]Tool Execution Results:[/]");
            AnsiConsole.WriteLine(stdinContent);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No tool results received[/]");
        }

        AnsiConsole.MarkupLine($"[dim]Working Directory:[/] {Directory.GetCurrentDirectory()}");

        await Task.CompletedTask;
    }
}