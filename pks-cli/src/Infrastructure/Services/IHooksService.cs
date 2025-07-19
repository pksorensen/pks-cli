using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks integration with smart dispatcher pattern
/// </summary>
public interface IHooksService
{
    /// <summary>
    /// Gets all available hooks in the system
    /// </summary>
    Task<List<HookDefinition>> GetAvailableHooksAsync();

    /// <summary>
    /// Executes a specific hook with the provided context
    /// </summary>
    /// <param name="hookName">Name of the hook to execute</param>
    /// <param name="context">Execution context for the hook</param>
    Task<HookResult> ExecuteHookAsync(string hookName, HookContext context);

    /// <summary>
    /// Installs a hook from a source (URL, local path, etc.)
    /// </summary>
    /// <param name="source">Source location of the hook</param>
    Task<HookInstallResult> InstallHookAsync(string source);

    /// <summary>
    /// Removes/uninstalls a hook by name
    /// </summary>
    /// <param name="hookName">Name of the hook to remove</param>
    Task<bool> RemoveHookAsync(string hookName);

    /// <summary>
    /// Validates hook configuration and tests connectivity
    /// </summary>
    Task<bool> ValidateHooksConfigurationAsync();

    /// <summary>
    /// Gets hook configuration for a specific event type
    /// </summary>
    /// <param name="eventType">The event type (PreToolUse, PostToolUse, etc.)</param>
    Task<List<HookDefinition>> GetHooksByEventAsync(string eventType);

    /// <summary>
    /// Creates a smart dispatcher script for efficient hook routing
    /// </summary>
    /// <param name="targetPath">Path where the dispatcher script should be created</param>
    Task<string> CreateSmartDispatcherAsync(string targetPath);
}

