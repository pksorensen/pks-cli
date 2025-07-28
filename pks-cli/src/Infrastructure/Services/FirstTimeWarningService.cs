using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;
using PKS.Infrastructure.Attributes;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service responsible for managing the first-time warning system.
/// Handles displaying disclaimers about AI-generated code to new users.
/// </summary>
public class FirstTimeWarningService : IFirstTimeWarningService
{
    private readonly IConfigurationService _configurationService;

    public FirstTimeWarningService(IConfigurationService configurationService)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
    }

    /// <summary>
    /// Determines if the first-time warning should be displayed based on:
    /// - Whether the user has already acknowledged the warning
    /// - Existing skip conditions (MCP stdio, hooks with JSON flags)
    /// - Command-level SkipFirstTimeWarningAttribute
    /// </summary>
    /// <param name="context">Command context for attribute detection (can be null in early stages)</param>
    /// <param name="commandArgs">Command line arguments for skip condition evaluation</param>
    /// <returns>True if warning should be displayed, false if it should be skipped</returns>
    public async Task<bool> ShouldShowWarningAsync(CommandContext? context, string[] commandArgs)
    {
        try
        {
            // Check if warning has already been acknowledged
            if (await IsWarningAcknowledgedAsync())
            {
                return false;
            }

            // Check command-line based skip conditions
            if (ShouldSkipBasedOnCommandArgs(commandArgs))
            {
                return false;
            }

            // Check command attribute-based skip conditions
            if (context != null && ShouldSkipBasedOnCommandAttribute(context))
            {
                return false;
            }

            return true;
        }
        catch
        {
            // If there are any errors determining skip conditions, show the warning as a safety measure
            return true;
        }
    }

    /// <summary>
    /// Displays the first-time warning dialog using Spectre.Console UI.
    /// Shows disclaimer about AI-generated code and prompts for user acknowledgment.
    /// </summary>
    /// <returns>True if user acknowledged the warning, false if declined</returns>
    public async Task<bool> DisplayWarningAsync()
    {
        try
        {
            // Display the first-time warning panel
            var warningPanel = new Panel(
                """
                [yellow]⚠️  IMPORTANT DISCLAIMER ⚠️[/]

                This CLI tool is powered by AI and generates code automatically.
                The generated code has [red]NOT[/] been validated by humans.

                [red]AI may make mistakes - use at your own risk.[/]

                Please review all generated code before use and report any issues at:
                [cyan]https://github.com/pksorensen/pks-cli[/]
                """)
                .Header("[red bold]First-Time Usage Warning[/]")
                .BorderColor(Color.Red)
                .Padding(1, 1, 1, 1);

            AnsiConsole.Write(warningPanel);
            AnsiConsole.WriteLine();

            // Ask for user acknowledgment
            var acknowledged = AnsiConsole.Confirm("Do you acknowledge and accept these terms?", false);

            if (acknowledged)
            {
                await MarkWarningAcknowledgedAsync();
                AnsiConsole.MarkupLine("[green]Thank you for acknowledging the terms.[/]");
                AnsiConsole.WriteLine();
                return true;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]You must acknowledge the terms to use PKS CLI.[/]");
                return false;
            }
        }
        catch
        {
            // If there's an error displaying the warning, fail safe and don't continue
            AnsiConsole.MarkupLine("[red]Error displaying first-time warning. Please try again.[/]");
            return false;
        }
    }

    /// <summary>
    /// Marks the first-time warning as acknowledged in persistent storage.
    /// Prevents the warning from appearing in future CLI invocations.
    /// </summary>
    public async Task MarkWarningAcknowledgedAsync()
    {
        await _configurationService.SetFirstTimeWarningAcknowledgedAsync();
    }

    /// <summary>
    /// Checks if the first-time warning has been acknowledged by the user.
    /// Used for quick checks without triggering complex skip logic.
    /// </summary>
    /// <returns>True if warning has been acknowledged, false otherwise</returns>
    public async Task<bool> IsWarningAcknowledgedAsync()
    {
        return await _configurationService.IsFirstTimeWarningAcknowledgedAsync();
    }

    /// <summary>
    /// Checks if warning should be skipped based on command line arguments
    /// </summary>
    private bool ShouldSkipBasedOnCommandArgs(string[] commandArgs)
    {
        // Skip for MCP stdio transport
        var isMcpStdio = commandArgs.Length > 2 &&
                         commandArgs.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase)) &&
                         (commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) &&
                          Array.IndexOf(commandArgs, a) + 1 < commandArgs.Length &&
                          commandArgs[Array.IndexOf(commandArgs, a) + 1].Equals("stdio", StringComparison.OrdinalIgnoreCase)) ||
                          !commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)));

        if (isMcpStdio)
        {
            return true;
        }

        // Skip for hooks commands with --json flag OR when it's a hook event command
        var isHooksCommand = commandArgs.Length > 1 &&
                             commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase);

        var hasJsonFlag = commandArgs.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("-j", StringComparison.OrdinalIgnoreCase));

        var isHookEventCommand = commandArgs.Length > 2 &&
                                commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase) &&
                                new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop" }
                                    .Contains(commandArgs[2], StringComparer.OrdinalIgnoreCase);

        if (isHooksCommand && (hasJsonFlag || isHookEventCommand))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if warning should be skipped based on command attribute
    /// Note: This method is currently not functional because CommandContext doesn't provide
    /// access to the command type. Attribute-based skipping will need to be implemented
    /// at the command level or through a different mechanism.
    /// </summary>
    private bool ShouldSkipBasedOnCommandAttribute(CommandContext context)
    {
        // TODO: Implement attribute-based skipping when CommandContext provides access
        // to command type information, or implement this check at the command level
        return false;
    }
}