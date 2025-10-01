using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS Git hooks management
/// This service provides MCP tools for Git hooks installation, configuration, and management
/// </summary>
public class HooksToolService
{
    private readonly ILogger<HooksToolService> _logger;
    private readonly IHooksService _hooksService;

    public HooksToolService(
        ILogger<HooksToolService> logger,
        IHooksService hooksService)
    {
        _logger = logger;
        _hooksService = hooksService;
    }

    /// <summary>
    /// Install Git hooks for the current repository
    /// This tool connects to the real PKS hooks command functionality
    /// </summary>
    [McpServerTool]
    [Description("Install Git hooks for the current repository")]
    public async Task<object> InstallHooksAsync(
        string[]? hookTypes = null,
        bool force = false,
        string? template = null,
        bool enableCommitValidation = true,
        bool enablePrePushChecks = true)
    {
        _logger.LogInformation("MCP Tool: Installing Git hooks, types: {HookTypes}, force: {Force}, template: {Template}",
            hookTypes != null ? string.Join(", ", hookTypes) : "all", force, template);

        try
        {
            // Check if we're in a Git repository
            if (!Directory.Exists(".git"))
            {
                return new
                {
                    success = false,
                    error = "Not a Git repository",
                    message = "Please run this command in a Git repository directory"
                };
            }

            // Get available hook types
            var availableHooks = await _hooksService.GetAvailableHooksAsync();
            var hooksToInstall = hookTypes?.ToList() ?? availableHooks.Select(h => h.Name).ToList();

            // Validate requested hook types
            var invalidHooks = hooksToInstall.Where(h => !availableHooks.Any(ah => ah.Name == h)).ToArray();
            if (invalidHooks.Length > 0)
            {
                return new
                {
                    success = false,
                    error = "Invalid hook types",
                    invalidHooks,
                    availableHooks = availableHooks.Select(h => h.Name).ToArray(),
                    message = $"Unknown hook types: {string.Join(", ", invalidHooks)}"
                };
            }

            // Create hook configuration
            var config = new HooksConfiguration
            {
                HookTypes = hooksToInstall,
                Template = template,
                Force = force,
                CommitValidation = enableCommitValidation,
                PrePushChecks = enablePrePushChecks
            };

            // Install hooks
            var result = await _hooksService.InstallHooksAsync(config);

            if (result.Success)
            {
                var installedHooks = result.InstalledHooks ?? hooksToInstall;

                return new
                {
                    success = true,
                    installedHooks = installedHooks.ToArray(),
                    hookCount = installedHooks.Count,
                    template = template ?? "default",
                    force,
                    features = new
                    {
                        commitValidation = enableCommitValidation,
                        prePushChecks = enablePrePushChecks
                    },
                    hooksDirectory = ".git/hooks",
                    configurationFile = result.ConfigurationFile,
                    installedAt = DateTime.UtcNow,
                    message = result.Message ?? $"Successfully installed {installedHooks.Count} Git hooks"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    requestedHooks = hooksToInstall.ToArray(),
                    template,
                    force,
                    error = result.Message,
                    message = $"Hook installation failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install Git hooks");
            return new
            {
                success = false,
                hookTypes,
                template,
                force,
                error = ex.Message,
                message = $"Hook installation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// List available and installed Git hooks
    /// </summary>
    [McpServerTool]
    [Description("List available and installed Git hooks")]
    public async Task<object> ListHooksAsync(
        bool detailed = false,
        bool installedOnly = false)
    {
        _logger.LogInformation("MCP Tool: Listing Git hooks, detailed: {Detailed}, installedOnly: {InstalledOnly}",
            detailed, installedOnly);

        try
        {
            var availableHooks = await _hooksService.GetAvailableHooksAsync();
            var installedHooks = await _hooksService.GetInstalledHooksAsync();

            var hooks = installedOnly
                ? availableHooks.Where(h => installedHooks.Any(ih => ih.Name == h.Name))
                : availableHooks;

            var hookList = hooks.ToArray();

            if (detailed)
            {
                return new
                {
                    success = true,
                    totalAvailable = availableHooks.Count(),
                    totalInstalled = installedHooks.Count(),
                    showing = installedOnly ? "installed only" : "all available",
                    hooks = hookList.Select(h =>
                    {
                        var installed = installedHooks.FirstOrDefault(ih => ih.Name == h.Name);
                        return new
                        {
                            name = h.Name,
                            description = h.Description,
                            category = h.Category,
                            isInstalled = installed != null,
                            installPath = installed?.Path,
                            lastModified = installed?.LastModified,
                            executable = installed?.IsExecutable ?? false,
                            configuration = h.DefaultConfiguration,
                            dependencies = h.Dependencies,
                            supportedEvents = h.SupportedEvents
                        };
                    }).ToArray(),
                    message = $"Retrieved {hookList.Length} hooks"
                };
            }
            else
            {
                return new
                {
                    success = true,
                    totalAvailable = availableHooks.Count(),
                    totalInstalled = installedHooks.Count(),
                    showing = installedOnly ? "installed only" : "all available",
                    hooks = hookList.Select(h =>
                    {
                        var installed = installedHooks.FirstOrDefault(ih => ih.Name == h.Name);
                        return new
                        {
                            name = h.Name,
                            description = h.Description,
                            category = h.Category,
                            isInstalled = installed != null,
                            lastModified = installed?.LastModified
                        };
                    }).ToArray(),
                    categories = hookList.GroupBy(h => h.Category).Select(g => new
                    {
                        category = g.Key,
                        count = g.Count(),
                        installed = g.Count(h => installedHooks.Any(ih => ih.Name == h.Name))
                    }).ToArray(),
                    message = $"Retrieved {hookList.Length} hooks"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list Git hooks");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to list hooks: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Update or reconfigure existing Git hooks
    /// </summary>
    [McpServerTool]
    [Description("Update or reconfigure existing Git hooks")]
    public async Task<object> UpdateHooksAsync(
        string[]? hookTypes = null,
        string? template = null,
        bool updateAll = false)
    {
        _logger.LogInformation("MCP Tool: Updating Git hooks, types: {HookTypes}, updateAll: {UpdateAll}, template: {Template}",
            hookTypes != null ? string.Join(", ", hookTypes) : "none", updateAll, template);

        try
        {
            if (!Directory.Exists(".git"))
            {
                return new
                {
                    success = false,
                    error = "Not a Git repository",
                    message = "Please run this command in a Git repository directory"
                };
            }

            var installedHooks = await _hooksService.GetInstalledHooksAsync();

            if (installedHooks.Count() == 0)
            {
                return new
                {
                    success = false,
                    error = "No hooks installed",
                    message = "No Git hooks are currently installed. Use pks_hooks_install first."
                };
            }

            List<string> hooksToUpdate;

            if (updateAll)
            {
                hooksToUpdate = installedHooks.Select(h => h.Name).ToList();
            }
            else if (hookTypes != null && hookTypes.Length > 0)
            {
                // Validate requested hook types are installed
                var notInstalled = hookTypes.Where(h => !installedHooks.Any(ih => ih.Name == h)).ToArray();
                if (notInstalled.Length > 0)
                {
                    return new
                    {
                        success = false,
                        error = "Hooks not installed",
                        notInstalled,
                        installedHooks = installedHooks.Select(h => h.Name).ToArray(),
                        message = $"Hooks not installed: {string.Join(", ", notInstalled)}"
                    };
                }
                hooksToUpdate = hookTypes.ToList();
            }
            else
            {
                return new
                {
                    success = false,
                    error = "No hooks specified",
                    message = "Please specify hook types to update or use updateAll=true"
                };
            }

            // Create update configuration
            var config = new HooksUpdateConfiguration
            {
                HookTypes = hooksToUpdate,
                Template = template
            };

            // Update hooks
            var result = await _hooksService.UpdateHooksAsync(config);

            if (result.Success)
            {
                var updatedHooks = result.UpdatedHooks ?? hooksToUpdate;

                return new
                {
                    success = true,
                    updatedHooks = updatedHooks.ToArray(),
                    hookCount = updatedHooks.Count,
                    template = template ?? "current",
                    updateAll,
                    updatedAt = DateTime.UtcNow,
                    changes = result.Changes,
                    message = result.Message ?? $"Successfully updated {updatedHooks.Count} Git hooks"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    requestedHooks = hooksToUpdate.ToArray(),
                    template,
                    updateAll,
                    error = result.Message,
                    message = $"Hook update failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Git hooks");
            return new
            {
                success = false,
                hookTypes,
                template,
                updateAll,
                error = ex.Message,
                message = $"Hook update failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Uninstall Git hooks from the repository
    /// </summary>
    [McpServerTool]
    [Description("Uninstall Git hooks from the repository")]
    public async Task<object> UninstallHooksAsync(
        string[]? hookTypes = null,
        bool removeAll = false,
        bool keepBackup = true)
    {
        _logger.LogInformation("MCP Tool: Uninstalling Git hooks, types: {HookTypes}, removeAll: {RemoveAll}, keepBackup: {KeepBackup}",
            hookTypes != null ? string.Join(", ", hookTypes) : "none", removeAll, keepBackup);

        try
        {
            if (!Directory.Exists(".git"))
            {
                return new
                {
                    success = false,
                    error = "Not a Git repository",
                    message = "Please run this command in a Git repository directory"
                };
            }

            var installedHooks = await _hooksService.GetInstalledHooksAsync();

            if (installedHooks.Count() == 0)
            {
                return new
                {
                    success = true,
                    uninstalledHooks = Array.Empty<string>(),
                    message = "No Git hooks were installed"
                };
            }

            List<string> hooksToUninstall;

            if (removeAll)
            {
                hooksToUninstall = installedHooks.Select(h => h.Name).ToList();
            }
            else if (hookTypes != null && hookTypes.Length > 0)
            {
                // Validate requested hook types are installed
                var notInstalled = hookTypes.Where(h => !installedHooks.Any(ih => ih.Name == h)).ToArray();
                if (notInstalled.Length > 0)
                {
                    return new
                    {
                        success = false,
                        error = "Hooks not installed",
                        notInstalled,
                        installedHooks = installedHooks.Select(h => h.Name).ToArray(),
                        message = $"Hooks not installed: {string.Join(", ", notInstalled)}"
                    };
                }
                hooksToUninstall = hookTypes.ToList();
            }
            else
            {
                return new
                {
                    success = false,
                    error = "No hooks specified",
                    message = "Please specify hook types to uninstall or use removeAll=true"
                };
            }

            // Create uninstall configuration
            var config = new HooksUninstallConfiguration
            {
                HookTypes = hooksToUninstall,
                KeepBackup = keepBackup
            };

            // Uninstall hooks
            var result = await _hooksService.UninstallHooksAsync(config);

            if (result.Success)
            {
                var uninstalledHooks = result.UninstalledHooks ?? hooksToUninstall;
                var remainingCount = installedHooks.Count() - uninstalledHooks.Count;

                return new
                {
                    success = true,
                    uninstalledHooks = uninstalledHooks.ToArray(),
                    hookCount = uninstalledHooks.Count,
                    remainingHooks = remainingCount,
                    removeAll,
                    keepBackup,
                    backupLocation = result.BackupLocation,
                    uninstalledAt = DateTime.UtcNow,
                    message = result.Message ?? $"Successfully uninstalled {uninstalledHooks.Count} Git hooks"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    requestedHooks = hooksToUninstall.ToArray(),
                    removeAll,
                    keepBackup,
                    error = result.Message,
                    message = $"Hook uninstallation failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall Git hooks");
            return new
            {
                success = false,
                hookTypes,
                removeAll,
                keepBackup,
                error = ex.Message,
                message = $"Hook uninstallation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Test Git hooks execution
    /// </summary>
    [McpServerTool]
    [Description("Test Git hooks execution")]
    public async Task<object> TestHooksAsync(
        string[]? hookTypes = null,
        bool testAll = false,
        bool dryRun = true)
    {
        _logger.LogInformation("MCP Tool: Testing Git hooks, types: {HookTypes}, testAll: {TestAll}, dryRun: {DryRun}",
            hookTypes != null ? string.Join(", ", hookTypes) : "none", testAll, dryRun);

        try
        {
            var installedHooks = await _hooksService.GetInstalledHooksAsync();

            if (installedHooks.Count() == 0)
            {
                return new
                {
                    success = false,
                    error = "No hooks installed",
                    message = "No Git hooks are currently installed to test"
                };
            }

            List<string> hooksToTest;

            if (testAll)
            {
                hooksToTest = installedHooks.Select(h => h.Name).ToList();
            }
            else if (hookTypes != null && hookTypes.Length > 0)
            {
                var notInstalled = hookTypes.Where(h => !installedHooks.Any(ih => ih.Name == h)).ToArray();
                if (notInstalled.Length > 0)
                {
                    return new
                    {
                        success = false,
                        error = "Hooks not installed",
                        notInstalled,
                        installedHooks = installedHooks.Select(h => h.Name).ToArray(),
                        message = $"Hooks not installed: {string.Join(", ", notInstalled)}"
                    };
                }
                hooksToTest = hookTypes.ToList();
            }
            else
            {
                return new
                {
                    success = false,
                    error = "No hooks specified",
                    message = "Please specify hook types to test or use testAll=true"
                };
            }

            // Test hooks
            var results = await _hooksService.TestHooksAsync(hooksToTest, dryRun);

            var allPassed = results.All(r => r.Success);
            var passedCount = results.Count(r => r.Success);
            var failedCount = results.Count(r => !r.Success);

            return new
            {
                success = allPassed,
                testedHooks = hooksToTest.ToArray(),
                hookCount = hooksToTest.Count,
                passedCount,
                failedCount,
                testAll,
                dryRun,
                results = results.Select(r => new
                {
                    hookName = r.HookName,
                    success = r.Success,
                    duration = r.Duration.TotalMilliseconds,
                    output = r.Output,
                    error = r.Error,
                    exitCode = r.ExitCode
                }).ToArray(),
                testedAt = DateTime.UtcNow,
                message = allPassed
                    ? $"All {passedCount} hooks passed testing"
                    : $"{passedCount} hooks passed, {failedCount} hooks failed"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test Git hooks");
            return new
            {
                success = false,
                hookTypes,
                testAll,
                dryRun,
                error = ex.Message,
                message = $"Hook testing failed: {ex.Message}"
            };
        }
    }
}