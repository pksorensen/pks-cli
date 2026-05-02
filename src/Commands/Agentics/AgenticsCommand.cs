using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics;

/// <summary>
/// Shared base for `pks agentics …` commands. Provides the same Display helpers
/// as DevcontainerCommand so all branches feel consistent. Existing Agentics
/// commands (runner/task) call AnsiConsole directly today; new commands should
/// inherit from this base.
/// </summary>
public abstract class AgenticsCommand<T> : Command<T> where T : AgenticsSettings
{
    protected readonly IAnsiConsole Console;

    protected AgenticsCommand(IAnsiConsole console)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
    }

    protected void DisplayBanner(string operation)
    {
        var panel = new Panel($"[bold cyan]🤖 PKS Agentics {operation}[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        Console.Write(panel);
        Console.WriteLine();
    }

    protected void DisplaySuccess(string message) => Console.MarkupLine($"[green]✓ {message}[/]");
    protected void DisplayError(string message) => Console.MarkupLine($"[red]✗ {message.EscapeMarkup()}[/]");
    protected void DisplayWarning(string message) => Console.MarkupLine($"[yellow]⚠ {message.EscapeMarkup()}[/]");
    protected void DisplayInfo(string message) => Console.MarkupLine($"[cyan]ℹ {message}[/]");
    protected void DisplayProgress(string message) => Console.MarkupLine($"[dim]  {message}[/]");
}
