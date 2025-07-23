using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling PreToolUse hook events from Claude Code
/// This hook is called before Claude Code executes a tool and can approve, block, or allow the operation
/// </summary>
public class PreToolUseCommand : BaseHookCommand
{
    /// <summary>
    /// Process the PreToolUse hook event
    /// For now, we always allow tool execution (proceed with no explicit decision)
    /// </summary>
    protected override async Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings)
    {
        // Read any context information from stdin
        var stdinContent = await ReadStdinAsync();

        // For debugging in non-JSON mode, we can still show environment info
        if (!settings.Json)
        {
            await ShowDebugInformationAsync(stdinContent);
        }

        // For now, we don't block or approve anything - just proceed
        // Future enhancements could analyze the tool being executed and make decisions
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
            AnsiConsole.MarkupLine("[dim]STDIN Content:[/]");
            AnsiConsole.WriteLine(stdinContent);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No STDIN content received[/]");
        }

        AnsiConsole.MarkupLine($"[dim]Working Directory:[/] {Directory.GetCurrentDirectory()}");

        await Task.CompletedTask;
    }
}