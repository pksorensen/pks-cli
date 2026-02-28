using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Base command for all GitHub runner operations
/// </summary>
public abstract class RunnerCommand<T> : Command<T> where T : GitHubSettings
{
    protected readonly IAnsiConsole Console;

    protected RunnerCommand(IAnsiConsole console)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
    }

    protected void DisplayBanner(string operation)
    {
        var panel = new Panel($"[bold cyan]GitHub Runner {operation}[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);

        Console.Write(panel);
        Console.WriteLine();
    }

    protected void DisplaySuccess(string message)
    {
        Console.MarkupLine($"[green]{message}[/]");
    }

    protected void DisplayError(string message)
    {
        Console.MarkupLine($"[red]{message.EscapeMarkup()}[/]");
    }

    protected void DisplayWarning(string message)
    {
        Console.MarkupLine($"[yellow]{message.EscapeMarkup()}[/]");
    }

    protected void DisplayInfo(string message)
    {
        Console.MarkupLine($"[cyan]{message}[/]");
    }

    protected async Task WithSpinnerAsync(string message, Func<Task> operation)
    {
        await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                await operation();
            });
    }

    protected async Task<TResult> WithSpinnerAsync<TResult>(string message, Func<Task<TResult>> operation)
    {
        return await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                return await operation();
            });
    }

    /// <summary>
    /// Parse a repository string in "owner/repo" format into its components
    /// </summary>
    protected static (string Owner, string Repo) ParseRepository(string? repository)
    {
        if (string.IsNullOrEmpty(repository))
            return (string.Empty, string.Empty);

        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }
}
