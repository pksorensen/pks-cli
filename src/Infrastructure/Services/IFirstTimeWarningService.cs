using Spectre.Console.Cli;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service responsible for managing the first-time warning system.
/// Handles displaying disclaimers about AI-generated code to new users.
/// </summary>
public interface IFirstTimeWarningService
{
    /// <summary>
    /// Determines if the first-time warning should be displayed based on:
    /// - Whether the user has already acknowledged the warning
    /// - Existing skip conditions (MCP stdio, hooks with JSON flags)
    /// - Command-level SkipFirstTimeWarningAttribute
    /// </summary>
    /// <param name="context">Command context for attribute detection (can be null in early stages)</param>
    /// <param name="commandArgs">Command line arguments for skip condition evaluation</param>
    /// <returns>True if warning should be displayed, false if it should be skipped</returns>
    Task<bool> ShouldShowWarningAsync(CommandContext? context, string[] commandArgs);

    /// <summary>
    /// Displays the first-time warning dialog using Spectre.Console UI.
    /// Shows disclaimer about AI-generated code and prompts for user acknowledgment.
    /// </summary>
    /// <returns>True if user acknowledged the warning, false if declined</returns>
    Task<bool> DisplayWarningAsync();

    /// <summary>
    /// Marks the first-time warning as acknowledged in persistent storage.
    /// Prevents the warning from appearing in future CLI invocations.
    /// </summary>
    Task MarkWarningAcknowledgedAsync();

    /// <summary>
    /// Checks if the first-time warning has been acknowledged by the user.
    /// Used for quick checks without triggering complex skip logic.
    /// </summary>
    /// <returns>True if warning has been acknowledged, false otherwise</returns>
    Task<bool> IsWarningAcknowledgedAsync();
}