using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS MCP server self-management operations
/// This service provides MCP tools for MCP server control, configuration, and monitoring
/// </summary>
public class McpManagementToolService
{
    private readonly ILogger<McpManagementToolService> _logger;
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpToolService _mcpToolService;
    private readonly McpResourceService _mcpResourceService;

    public McpManagementToolService(
        ILogger<McpManagementToolService> logger,
        IMcpHostingService mcpHostingService,
        McpToolService mcpToolService,
        McpResourceService mcpResourceService)
    {
        _logger = logger;
        _mcpHostingService = mcpHostingService;
        _mcpToolService = mcpToolService;
        _mcpResourceService = mcpResourceService;
    }

    /// <summary>
    /// Get MCP server status and configuration
    /// This tool provides self-management capabilities for the MCP server
    /// </summary>
    [McpServerTool]
    [Description("Get MCP server status and configuration")]
    public async Task<object> GetMcpServerStatusAsync(
        bool includeConfig = false,
        bool includeTools = true,
        bool includeResources = true)
    {
        _logger.LogInformation("MCP Tool: Getting MCP server status, includeConfig: {IncludeConfig}, includeTools: {IncludeTools}, includeResources: {IncludeResources}",
            includeConfig, includeTools, includeResources);

        try
        {
            // Get server status
            var isRunning = await _mcpHostingService.IsRunningAsync();
            var serverInfo = await _mcpHostingService.GetServerInfoAsync();

            var baseStatus = new
            {
                success = true,
                isRunning,
                serverVersion = "SDK-based",
                implementation = "PKS MCP Hosting Service",
                transport = serverInfo.Transport,
                startedAt = serverInfo.StartedAt,
                uptime = isRunning ? DateTime.UtcNow - serverInfo.StartedAt : TimeSpan.Zero,
                connectionCount = serverInfo.ActiveConnections,
                statusCheckedAt = DateTime.UtcNow,
                message = isRunning ? "MCP server is running" : "MCP server is stopped"
            };

            var result = new Dictionary<string, object>
            {
                ["success"] = baseStatus.success,
                ["isRunning"] = baseStatus.isRunning,
                ["serverVersion"] = baseStatus.serverVersion,
                ["implementation"] = baseStatus.implementation,
                ["transport"] = baseStatus.transport,
                ["startedAt"] = baseStatus.startedAt,
                ["uptime"] = baseStatus.uptime,
                ["connectionCount"] = baseStatus.connectionCount,
                ["statusCheckedAt"] = baseStatus.statusCheckedAt,
                ["message"] = baseStatus.message
            };

            if (includeConfig)
            {
                result["configuration"] = new
                {
                    defaultTransport = serverInfo.DefaultTransport,
                    supportedTransports = serverInfo.SupportedTransports,
                    enableAutoToolDiscovery = serverInfo.EnableAutoToolDiscovery,
                    enabledCategories = serverInfo.EnabledCategories,
                    disabledTools = serverInfo.DisabledTools,
                    maxConnections = serverInfo.MaxConnections,
                    timeoutSettings = serverInfo.TimeoutSettings
                };
            }

            if (includeTools)
            {
                var availableTools = new[] { "test-tool" }; // Simulated tools for now
                result["tools"] = new
                {
                    totalCount = availableTools.Length,
                    enabledCount = availableTools.Length,
                    categories = new[] { new { category = "test", count = 1, enabled = 1 } },
                    tools = availableTools.Select(t => new
                    {
                        name = t,
                        description = "Test tool",
                        category = "test",
                        enabled = true
                    }).ToArray()
                };
            }

            if (includeResources)
            {
                var availableResources = new[] { "test-resource" }; // Simulated resources for now
                result["resources"] = new
                {
                    totalCount = availableResources.Count(),
                    resources = availableResources.Select(r => new
                    {
                        uri = $"test://{r}",
                        name = r,
                        description = "Test resource",
                        mimeType = "text/plain"
                    }).ToArray()
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get MCP server status");
            return new
            {
                success = false,
                isRunning = false,
                error = ex.Message,
                message = $"Failed to get MCP server status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Start the MCP server
    /// </summary>
    [McpServerTool]
    [Description("Start the MCP server")]
    public async Task<object> StartMcpServerAsync(
        string transport = "stdio",
        bool force = false)
    {
        _logger.LogInformation("MCP Tool: Starting MCP server with transport '{Transport}', force: {Force}", transport, force);

        try
        {
            var isRunning = await _mcpHostingService.IsRunningAsync();

            if (isRunning && !force)
            {
                return new
                {
                    success = true,
                    alreadyRunning = true,
                    transport,
                    message = "MCP server is already running. Use force=true to restart."
                };
            }

            // Stop if running and force restart
            if (isRunning && force)
            {
                await _mcpHostingService.StopAsync();
                await Task.Delay(1000); // Brief pause between stop and start
            }

            // Start server
            var startResult = await _mcpHostingService.StartAsync(transport);

            if (startResult)
            {
                var serverInfo = await _mcpHostingService.GetServerInfoAsync();

                return new
                {
                    success = true,
                    alreadyRunning = false,
                    transport,
                    force,
                    startedAt = serverInfo.StartedAt,
                    serverInfo = new
                    {
                        implementation = "SDK-based",
                        supportedTransports = serverInfo.SupportedTransports,
                        activeConnections = serverInfo.ActiveConnections
                    },
                    message = $"MCP server started successfully with {transport} transport"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    transport,
                    force,
                    error = "Server failed to start",
                    message = "MCP server failed to start. Check server logs for details."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP server");
            return new
            {
                success = false,
                transport,
                force,
                error = ex.Message,
                message = $"MCP server start failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Stop the MCP server
    /// </summary>
    [McpServerTool]
    [Description("Stop the MCP server")]
    public async Task<object> StopMcpServerAsync(
        bool force = false,
        int gracePeriodSeconds = 10)
    {
        _logger.LogInformation("MCP Tool: Stopping MCP server, force: {Force}, gracePeriod: {GracePeriod}s",
            force, gracePeriodSeconds);

        try
        {
            var isRunning = await _mcpHostingService.IsRunningAsync();

            if (!isRunning)
            {
                return new
                {
                    success = true,
                    wasRunning = false,
                    message = "MCP server is not currently running"
                };
            }

            var serverInfo = await _mcpHostingService.GetServerInfoAsync();
            var uptime = DateTime.UtcNow - serverInfo.StartedAt;
            var activeConnections = serverInfo.ActiveConnections;

            // Stop server
            var stopResult = await _mcpHostingService.StopAsync(gracePeriodSeconds, force);

            if (stopResult)
            {
                return new
                {
                    success = true,
                    wasRunning = true,
                    force,
                    gracePeriodSeconds,
                    uptimeBeforeStop = uptime,
                    activeConnectionsBeforeStop = activeConnections,
                    stoppedAt = DateTime.UtcNow,
                    message = $"MCP server stopped successfully after {uptime:hh\\:mm\\:ss} uptime"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    wasRunning = true,
                    force,
                    gracePeriodSeconds,
                    error = "Server failed to stop cleanly",
                    message = "MCP server failed to stop within the grace period. May require manual intervention."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop MCP server");
            return new
            {
                success = false,
                force,
                gracePeriodSeconds,
                error = ex.Message,
                message = $"MCP server stop failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Restart the MCP server
    /// </summary>
    [McpServerTool]
    [Description("Restart the MCP server")]
    public async Task<object> RestartMcpServerAsync(
        string transport = "stdio",
        int stopGracePeriodSeconds = 5)
    {
        _logger.LogInformation("MCP Tool: Restarting MCP server with transport '{Transport}', stopGracePeriod: {StopGracePeriod}s",
            transport, stopGracePeriodSeconds);

        try
        {
            var isRunning = await _mcpHostingService.IsRunningAsync();
            var originalUptime = TimeSpan.Zero;
            var originalConnections = 0;

            if (isRunning)
            {
                var serverInfo = await _mcpHostingService.GetServerInfoAsync();
                originalUptime = DateTime.UtcNow - serverInfo.StartedAt;
                originalConnections = serverInfo.ActiveConnections;

                // Stop the server
                var stopResult = await _mcpHostingService.StopAsync(stopGracePeriodSeconds, false);
                if (!stopResult)
                {
                    return new
                    {
                        success = false,
                        phase = "stop",
                        transport,
                        error = "Failed to stop server",
                        message = "Server restart failed during stop phase"
                    };
                }

                // Brief pause between stop and start
                await Task.Delay(1000);
            }

            // Start the server
            var startResult = await _mcpHostingService.StartAsync(transport);

            if (startResult)
            {
                var newServerInfo = await _mcpHostingService.GetServerInfoAsync();

                return new
                {
                    success = true,
                    wasRunning = isRunning,
                    transport,
                    stopGracePeriodSeconds,
                    originalUptime = originalUptime,
                    originalActiveConnections = originalConnections,
                    restartedAt = newServerInfo.StartedAt,
                    newServerInfo = new
                    {
                        implementation = "SDK-based",
                        supportedTransports = newServerInfo.SupportedTransports,
                        activeConnections = newServerInfo.ActiveConnections
                    },
                    message = isRunning
                        ? $"MCP server restarted successfully (was running for {originalUptime:hh\\:mm\\:ss})"
                        : "MCP server started successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    phase = "start",
                    wasRunning = isRunning,
                    transport,
                    error = "Failed to start server",
                    message = "Server restart failed during start phase"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart MCP server");
            return new
            {
                success = false,
                transport,
                stopGracePeriodSeconds,
                error = ex.Message,
                message = $"MCP server restart failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get MCP server logs and diagnostics
    /// </summary>
    [McpServerTool]
    [Description("Get MCP server logs and diagnostics")]
    public async Task<object> GetMcpServerLogsAsync(
        int entryCount = 50,
        string? logLevel = null,
        bool includeMetrics = true)
    {
        _logger.LogInformation("MCP Tool: Getting MCP server logs, entryCount: {EntryCount}, logLevel: {LogLevel}, includeMetrics: {IncludeMetrics}",
            entryCount, logLevel, includeMetrics);

        try
        {
            // Get server logs
            var logs = await _mcpHostingService.GetLogsAsync(entryCount, logLevel);

            var baseResult = new
            {
                success = true,
                entryCount,
                logLevel = logLevel ?? "all",
                retrievedEntries = logs.Count(),
                logs = logs.Select(log => new
                {
                    timestamp = log.Timestamp,
                    level = log.Level,
                    message = log.Message,
                    category = log.Category,
                    exception = log.Exception
                }).ToArray(),
                retrievedAt = DateTime.UtcNow,
                message = $"Retrieved {logs.Count()} log entries"
            };

            if (includeMetrics)
            {
                var metrics = await _mcpHostingService.GetPerformanceMetricsAsync();

                return new
                {
                    success = baseResult.success,
                    entryCount = baseResult.entryCount,
                    logLevel = baseResult.logLevel,
                    retrievedEntries = baseResult.retrievedEntries,
                    logs = baseResult.logs,
                    retrievedAt = baseResult.retrievedAt,
                    message = baseResult.message,
                    metrics = new
                    {
                        requestCount = metrics.TotalRequests,
                        averageResponseTime = metrics.AverageResponseTime,
                        peakMemoryUsage = metrics.PeakMemoryUsage,
                        totalUptime = metrics.TotalUptime,
                        errorRate = metrics.ErrorRate,
                        connectionCount = metrics.ActiveConnections,
                        toolInvocations = metrics.ToolInvocations,
                        resourceAccess = metrics.ResourceAccesses
                    }
                };
            }

            return baseResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get MCP server logs");
            return new
            {
                success = false,
                entryCount,
                logLevel,
                error = ex.Message,
                message = $"Failed to retrieve MCP server logs: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Configure MCP server settings
    /// </summary>
    [McpServerTool]
    [Description("Configure MCP server settings")]
    public async Task<object> ConfigureMcpServerAsync(
        string? defaultTransport = null,
        bool? enableAutoToolDiscovery = null,
        string[]? enabledCategories = null,
        string[]? disabledTools = null,
        int? maxConnections = null,
        int? requestTimeoutSeconds = null)
    {
        _logger.LogInformation("MCP Tool: Configuring MCP server, defaultTransport: {DefaultTransport}, enableAutoToolDiscovery: {EnableAutoToolDiscovery}",
            defaultTransport, enableAutoToolDiscovery);

        try
        {
            var currentConfig = await _mcpHostingService.GetConfigurationAsync();
            var changes = new List<string>();

            // Apply configuration changes
            var newConfig = new McpConfiguration
            {
                DefaultTransport = defaultTransport ?? currentConfig.DefaultTransport,
                EnableAutoToolDiscovery = enableAutoToolDiscovery ?? currentConfig.EnableAutoToolDiscovery,
                EnabledToolCategories = enabledCategories ?? currentConfig.EnabledToolCategories,
                DisabledTools = disabledTools ?? currentConfig.DisabledTools,
                MaxConnections = maxConnections ?? currentConfig.MaxConnections,
                OperationTimeoutMs = (requestTimeoutSeconds ?? currentConfig.RequestTimeoutSeconds) * 1000
            };

            // Track changes
            if (defaultTransport != null && defaultTransport != currentConfig.DefaultTransport)
                changes.Add($"Default transport: {currentConfig.DefaultTransport} → {defaultTransport}");

            if (enableAutoToolDiscovery.HasValue && enableAutoToolDiscovery != currentConfig.EnableAutoToolDiscovery)
                changes.Add($"Auto tool discovery: {currentConfig.EnableAutoToolDiscovery} → {enableAutoToolDiscovery}");

            if (maxConnections.HasValue && maxConnections != currentConfig.MaxConnections)
                changes.Add($"Max connections: {currentConfig.MaxConnections} → {maxConnections}");

            if (requestTimeoutSeconds.HasValue && requestTimeoutSeconds != currentConfig.RequestTimeoutSeconds)
                changes.Add($"Request timeout: {currentConfig.RequestTimeoutSeconds}s → {requestTimeoutSeconds}s");

            // Apply configuration
            var configResult = await _mcpHostingService.UpdateConfigurationAsync(newConfig);

            if (configResult)
            {
                var isRunning = await _mcpHostingService.IsRunningAsync();

                return new
                {
                    success = true,
                    changesApplied = changes.Count,
                    changes = changes.ToArray(),
                    newConfiguration = new
                    {
                        defaultTransport = newConfig.DefaultTransport,
                        enableAutoToolDiscovery = newConfig.EnableAutoToolDiscovery,
                        enabledCategories = newConfig.EnabledToolCategories,
                        disabledTools = newConfig.DisabledTools,
                        maxConnections = newConfig.MaxConnections,
                        requestTimeoutSeconds = newConfig.RequestTimeoutSeconds
                    },
                    serverRunning = isRunning,
                    restartRequired = changes.Count > 0 && isRunning,
                    configuredAt = DateTime.UtcNow,
                    message = changes.Count > 0
                        ? $"Configuration updated with {changes.Count} changes. Restart may be required for all changes to take effect."
                        : "No configuration changes were necessary"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    changesAttempted = changes.Count,
                    changes = changes.ToArray(),
                    error = "Configuration update failed",
                    message = "Failed to apply MCP server configuration changes"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to configure MCP server");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"MCP server configuration failed: {ex.Message}"
            };
        }
    }
}