using PKS.Commands.Hooks;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks integration
/// </summary>
public interface IHooksService
{
    /// <summary>
    /// Initializes Claude Code hooks by creating proper settings.json configuration
    /// </summary>
    /// <param name="force">Force overwrite existing hooks configuration</param>
    /// <param name="scope">Settings scope (user, project, local) - defaults to project</param>
    Task<bool> InitializeClaudeCodeHooksAsync(bool force = false, SettingsScope scope = SettingsScope.Project);

    /// <summary>
    /// Gets all available hooks that can be executed
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available hooks</returns>
    Task<List<HookDefinition>> GetAvailableHooksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a hook with the specified context
    /// </summary>
    /// <param name="hookName">Name of the hook to execute</param>
    /// <param name="context">Execution context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook execution</returns>
    Task<HookResult> ExecuteHookAsync(string hookName, HookContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a hook from the specified source
    /// </summary>
    /// <param name="hookSource">Source of the hook (URL, package, etc.)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook installation</returns>
    Task<HookInstallResult> InstallHookAsync(string hookSource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an installed hook
    /// </summary>
    /// <param name="hookName">Name of the hook to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if hook was successfully removed</returns>
    Task<bool> RemoveHookAsync(string hookName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs Git hooks with the specified configuration
    /// </summary>
    /// <param name="configuration">Hook installation configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook installation</returns>
    Task<HookInstallResult> InstallHooksAsync(HooksConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls Git hooks with the specified configuration
    /// </summary>
    /// <param name="configuration">Hook uninstallation configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook uninstallation</returns>
    Task<HookUninstallResult> UninstallHooksAsync(HooksUninstallConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates existing Git hooks with the specified configuration
    /// </summary>
    /// <param name="configuration">Hook update configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of hook update</returns>
    Task<HookUpdateResult> UpdateHooksAsync(HooksUpdateConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all installed hooks in the current repository
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of installed hooks</returns>
    Task<List<InstalledHook>> GetInstalledHooksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the execution of specified hooks
    /// </summary>
    /// <param name="hookNames">Names of hooks to test</param>
    /// <param name="dryRun">Whether to perform a dry run</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of test results</returns>
    Task<List<HookTestResult>> TestHooksAsync(List<string> hookNames, bool dryRun = true, CancellationToken cancellationToken = default);
}