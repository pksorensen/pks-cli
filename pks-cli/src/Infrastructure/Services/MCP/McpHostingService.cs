using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.CLI.Infrastructure.Services.Models;
using System.Diagnostics;
using System.ComponentModel;

namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Modern SDK-based MCP server hosting service implementation
/// </summary>
public class McpHostingService : IMcpHostingService, IDisposable
{
    private readonly ILogger<McpHostingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly McpResourceService _resourceService;
    
    private McpServerStatusInfo _currentStatus;
    private McpServerLifecycleState _lifecycleState;
    private IHost? _mcpHost;
    private Process? _stdioProcess;
    private CancellationTokenSource? _hostCancellation;
    private TaskCompletionSource<bool>? _shutdownCompletion;

    public McpHostingService(
        ILogger<McpHostingService> logger,
        IServiceProvider serviceProvider,
        McpResourceService resourceService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _resourceService = resourceService;
        
        _currentStatus = new McpServerStatusInfo
        {
            Status = McpServerStatus.Stopped,
            Version = "1.0.0"
        };
        _lifecycleState = McpServerLifecycleState.Stopped;
        
        // SDK-based hosting with WithToolsFromAssembly() handles tool discovery automatically
        _logger.LogInformation("SDK-based MCP hosting service initialized");
    }

    // Tool discovery and registration is now handled automatically by WithToolsFromAssembly()

    public async Task<McpServerResult> StartServerAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_lifecycleState == McpServerLifecycleState.Running)
            {
                return new McpServerResult
                {
                    Success = false,
                    Message = "MCP Server is already running",
                    Transport = _currentStatus.Transport,
                    Port = _currentStatus.Port
                };
            }

            if (_lifecycleState == McpServerLifecycleState.Starting)
            {
                return new McpServerResult
                {
                    Success = false,
                    Message = "MCP Server is currently starting"
                };
            }

            _lifecycleState = McpServerLifecycleState.Starting;
            _logger.LogInformation("Starting SDK-based MCP server with transport: {Transport}", config.Transport);

            var result = config.Transport.ToLower() switch
            {
                "stdio" => await StartStdioServerAsync(config, cancellationToken),
                "http" => await StartHttpServerAsync(config, cancellationToken),
                "sse" => await StartSseServerAsync(config, cancellationToken),
                _ => new McpServerResult 
                { 
                    Success = false, 
                    Message = $"Unsupported transport: {config.Transport}" 
                }
            };

            if (result.Success)
            {
                _currentStatus = new McpServerStatusInfo
                {
                    Status = McpServerStatus.Running,
                    Port = result.Port,
                    Transport = config.Transport,
                    StartTime = DateTime.UtcNow,
                    Version = "1.0.0",
                    ActiveConnections = 0
                };
                _lifecycleState = McpServerLifecycleState.Running;
                _logger.LogInformation("MCP server started successfully with transport {Transport}", config.Transport);
            }
            else
            {
                _lifecycleState = McpServerLifecycleState.Error;
                _logger.LogError("Failed to start MCP server: {Message}", result.Message);
            }

            return result;
        }
        catch (Exception ex)
        {
            _lifecycleState = McpServerLifecycleState.Error;
            _logger.LogError(ex, "Failed to start MCP server");
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start server: {ex.Message}"
            };
        }
    }

    public async Task<bool> StopServerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_lifecycleState != McpServerLifecycleState.Running)
            {
                _logger.LogWarning("MCP server is not running, current state: {State}", _lifecycleState);
                return false;
            }

            _lifecycleState = McpServerLifecycleState.Stopping;
            _logger.LogInformation("Stopping MCP server");

            // Stop the host gracefully
            if (_mcpHost != null)
            {
                await _mcpHost.StopAsync(cancellationToken);
                _mcpHost.Dispose();
                _mcpHost = null;
            }

            // Cancel host operations
            _hostCancellation?.Cancel();
            _hostCancellation?.Dispose();
            _hostCancellation = null;

            // Stop stdio process if running
            if (_stdioProcess != null && !_stdioProcess.HasExited)
            {
                _stdioProcess.Kill();
                await _stdioProcess.WaitForExitAsync(cancellationToken);
                _stdioProcess.Dispose();
                _stdioProcess = null;
            }

            // Signal shutdown completion
            _shutdownCompletion?.SetResult(true);

            _currentStatus.Status = McpServerStatus.Stopped;
            _currentStatus.StartTime = DateTime.UtcNow;
            _currentStatus.ActiveConnections = 0;
            _lifecycleState = McpServerLifecycleState.Stopped;

            _logger.LogInformation("MCP server stopped successfully");
            return true;
        }
        catch (Exception ex)
        {
            _lifecycleState = McpServerLifecycleState.Error;
            _logger.LogError(ex, "Failed to stop MCP server");
            return false;
        }
    }

    public async Task<McpServerStatusInfo> GetServerStatusAsync()
    {
        // Update active connections if server is running
        if (_lifecycleState == McpServerLifecycleState.Running && _mcpHost != null)
        {
            try
            {
                // In a real implementation, you would get this from the MCP server
                // For now, we'll keep the current value or use a default
                _currentStatus.ActiveConnections = _currentStatus.ActiveConnections;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update active connections count");
            }
        }

        return await Task.FromResult(_currentStatus);
    }

    public async Task<McpServerResult> RestartServerAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Restarting MCP server");

        // Store current config (in a real implementation, we'd persist this)
        var currentConfig = new McpServerConfig
        {
            Transport = _currentStatus.Transport ?? "stdio",
            Port = _currentStatus.Port == 0 ? 3000 : _currentStatus.Port,
            Debug = false
        };

        await StopServerAsync(cancellationToken);
        
        // Wait a moment for clean shutdown
        await Task.Delay(1000, cancellationToken);
        
        return await StartServerAsync(currentConfig, cancellationToken);
    }

    public McpServerLifecycleState GetLifecycleState()
    {
        return _lifecycleState;
    }

    public void RegisterToolService<T>(T toolService) where T : class
    {
        _logger.LogInformation("SDK-based hosting: Tool service {ServiceType} will be discovered automatically by WithToolsFromAssembly()", typeof(T).Name);
        // With SDK-based hosting, tool discovery is automatic - no manual registration needed
    }

    public void RegisterResourceService<T>(T resourceService) where T : class
    {
        _logger.LogInformation("SDK-based hosting: Resource service {ServiceType} will be discovered automatically", typeof(T).Name);
        // With SDK-based hosting, resource discovery is automatic - no manual registration needed
    }

    private async Task<McpServerResult> StartStdioServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting MCP server with stdio transport using SDK hosting");

            _hostCancellation = new CancellationTokenSource();
            _shutdownCompletion = new TaskCompletionSource<bool>();

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.AddConsole(consoleLogOptions =>
            {
                // Configure all logs to go to stderr
                consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
            });
            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            // WithToolsFromAssembly() will automatically discover and register tools
            // No manual registration needed - the SDK handles everything

            _mcpHost = builder.Build();

            // Start the host
            await _mcpHost.StartAsync(cancellationToken);

            // Create a dummy process entry for compatibility
            var currentProcess = Process.GetCurrentProcess();

            return new McpServerResult
            {
                Success = true,
                Message = "MCP server started with stdio transport",
                Transport = "stdio",
                ProcessId = currentProcess.Id
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start stdio MCP server");
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start stdio server: {ex.Message}"
            };
        }
    }

    private async Task<McpServerResult> StartHttpServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting MCP server with HTTP transport on port {Port}", config.Port);

            _hostCancellation = new CancellationTokenSource();


            var builder = WebApplication.CreateBuilder();

            // Configure to listen on all interfaces for devcontainer access
            builder.WebHost.UseUrls($"http://localhost:{config.Port}");

            // Register MCP server and discover tools from the current assembly
            builder.Services.AddMcpServer().WithHttpTransport(options =>
            {
                options.Stateless = true; // Enable stateless mode  
            }
            ).WithToolsFromAssembly();

            // WithToolsFromAssembly() will automatically discover and register tools
            // No manual registration needed - the SDK handles everything


           

            var app = builder.Build();
          
            app.MapMcp();


            _mcpHost = app;
            // Start the host
            await _mcpHost.StartAsync(cancellationToken);

            return new McpServerResult
            {
                Success = true,
                Message = $"MCP server started with HTTP transport on port {config.Port}",
                Transport = "http",
                Port = config.Port
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start HTTP MCP server");
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start HTTP server: {ex.Message}"
            };
        }
    }

    private async Task<McpServerResult> StartSseServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting MCP server with SSE transport on port {Port}", config.Port);

            _hostCancellation = new CancellationTokenSource();

            // Create host builder with MCP services and SSE transport
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            
            // Register MCP server with SSE transport
            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            _mcpHost = builder.Build();

            // Start the host
            await _mcpHost.StartAsync(cancellationToken);

            return new McpServerResult
            {
                Success = true,
                Message = $"MCP server started with SSE transport on port {config.Port}",
                Transport = "sse",
                Port = config.Port
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SSE MCP server");
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start SSE server: {ex.Message}"
            };
        }
    }

    private void RegisterToolHandlers(IServiceCollection services)
    {
        // With WithToolsFromAssembly(), tool discovery is automatic
        // The SDK will discover classes with McpServerToolType attribute
        _logger.LogInformation("Using SDK-based tool discovery with WithToolsFromAssembly()");
        
        // Static methods in tool classes can have dependencies injected as parameters
        // The SDK will use the service provider to resolve these dependencies
    }

    private void RegisterResourceHandlers(IServiceCollection services)
    {
        // Register resource handlers from the resource service
        var resources = _resourceService.GetAvailableResources();
        foreach (var resource in resources)
        {
            

            _logger.LogDebug("Registering resource handler: {ResourceUri}", resource.Uri);
            // In the real implementation, we would register proper MCP resource handlers
            // services.AddTransient<IResourceHandler, CustomResourceHandler>();
        }
    }

    public async Task<bool> IsRunningAsync()
    {
        await Task.CompletedTask;
        return _lifecycleState == McpServerLifecycleState.Running;
    }

    public async Task<McpServerInfo> GetServerInfoAsync()
    {
        await Task.CompletedTask;
        
        return new McpServerInfo
        {
            Transport = _currentStatus.Transport,
            StartedAt = _currentStatus.StartTime,
            ActiveConnections = _currentStatus.ActiveConnections,
            DefaultTransport = "stdio",
            SupportedTransports = new[] { "stdio", "http", "sse" },
            EnableAutoToolDiscovery = true,
            EnabledCategories = Array.Empty<string>(),
            DisabledTools = Array.Empty<string>(),
            MaxConnections = 100,
            TimeoutSettings = new McpTimeoutSettings()
        };
    }

    public async Task<bool> StartAsync(string transport = "stdio")
    {
        var config = new McpServerConfig 
        { 
            Transport = transport,
            Port = transport == "stdio" ? 0 : 3000 
        };
        var result = await StartServerAsync(config);
        return result.Success;
    }

    public async Task<bool> StopAsync(int gracePeriodSeconds = 10, bool force = false)
    {
        // For now, ignore the grace period parameters and use the existing implementation
        return await StopServerAsync(CancellationToken.None);
    }

    public async Task<McpConfiguration> GetConfigurationAsync()
    {
        await Task.CompletedTask;
        
        return new McpConfiguration
        {
            DefaultTransport = "stdio",
            EnableAutoToolDiscovery = true,
            EnabledToolCategories = Array.Empty<string>(),
            DisabledTools = Array.Empty<string>(),
            MaxConnections = 100,
            OperationTimeoutMs = 30000
        };
    }

    public async Task<bool> UpdateConfigurationAsync(McpConfiguration configuration)
    {
        await Task.CompletedTask;
        
        _logger.LogInformation("Configuration update requested with transport: {Transport}, maxConnections: {MaxConnections}", 
            configuration.DefaultTransport, configuration.MaxConnections);
        
        // In a real implementation, we would apply these configuration changes
        // For now, just return true to indicate the configuration was "updated"
        return true;
    }

    public async Task<IEnumerable<McpLogEntry>> GetLogsAsync(int entryCount = 50, string? logLevel = null)
    {
        await Task.CompletedTask;
        
        // In a real implementation, we would retrieve actual log entries
        // For now, return some sample log entries
        var logs = new List<McpLogEntry>();
        
        for (int i = 0; i < Math.Min(entryCount, 10); i++)
        {
            logs.Add(new McpLogEntry
            {
                Timestamp = DateTime.UtcNow.AddMinutes(-i * 5),
                Level = i % 3 == 0 ? "Error" : (i % 3 == 1 ? "Warning" : "Information"),
                Message = $"Sample log entry {i + 1}",
                Category = "PKS.CLI.MCP.Hosting",
                Exception = i % 5 == 0 ? "Sample exception details" : null
            });
        }
        
        if (!string.IsNullOrEmpty(logLevel))
        {
            logs = logs.Where(l => string.Equals(l.Level, logLevel, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        
        return logs;
    }

    public async Task<McpPerformanceMetrics> GetPerformanceMetricsAsync()
    {
        await Task.CompletedTask;
        
        var uptime = _lifecycleState == McpServerLifecycleState.Running 
            ? DateTime.UtcNow - _currentStatus.StartTime 
            : TimeSpan.Zero;
        
        return new McpPerformanceMetrics
        {
            TotalRequests = 42, // Sample data
            AverageResponseTime = 125.5,
            PeakMemoryUsage = 1024 * 1024 * 50, // 50MB
            TotalUptime = uptime,
            ErrorRate = 2.5,
            ActiveConnections = _currentStatus.ActiveConnections,
            ToolInvocations = 15,
            ResourceAccesses = 8
        };
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing MCP hosting service");
        
        try
        {
            StopServerAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during MCP hosting service disposal");
        }

        _hostCancellation?.Dispose();
        _shutdownCompletion?.TrySetResult(true);
        _mcpHost?.Dispose();
        _stdioProcess?.Dispose();
    }
}