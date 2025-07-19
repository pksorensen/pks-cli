using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling UserPromptSubmit hook events from Claude Code
/// </summary>
public class UserPromptSubmitCommand : AsyncCommand<HooksSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, HooksSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]PKS Hooks: UserPromptSubmit Event Triggered[/]");
        
        // Print all environment variables
        AnsiConsole.MarkupLine("\n[yellow]Environment Variables:[/]");
        foreach (var env in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().OrderBy(e => e.Key))
        {
            AnsiConsole.MarkupLine($"  [dim]{env.Key}[/] = [green]{env.Value}[/]");
        }
        
        // Print command line arguments
        AnsiConsole.MarkupLine("\n[yellow]Command Line Arguments:[/]");
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            AnsiConsole.MarkupLine($"  [dim]args[{i}][/] = [green]{args[i]}[/]");
        }
        
        // Read stdin if available
        AnsiConsole.MarkupLine("\n[yellow]STDIN Input:[/]");
        try
        {
            if (!Console.IsInputRedirected)
            {
                AnsiConsole.MarkupLine("  [dim]No piped input detected[/]");
            }
            else
            {
                var stdinContent = await Console.In.ReadToEndAsync();
                AnsiConsole.MarkupLine($"  [green]{stdinContent}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]Error reading stdin: {ex.Message}[/]");
        }
        
        // Print working directory
        AnsiConsole.MarkupLine($"\n[yellow]Working Directory:[/] [green]{Directory.GetCurrentDirectory()}[/]");
        
        // Success exit code
        AnsiConsole.MarkupLine("\n[green]✓ UserPromptSubmit hook completed successfully[/]");
        return 0;
    }
}