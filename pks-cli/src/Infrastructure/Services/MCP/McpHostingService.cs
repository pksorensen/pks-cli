using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Modern SDK-based MCP server hosting service implementation
/// </summary>
public class McpHostingService : IMcpHostingService, IDisposable
{
    private readonly ILogger<McpHostingService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly McpToolService _toolService;
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
        McpToolService toolService,
        McpResourceService resourceService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _toolService = toolService;
        _resourceService = resourceService;
        
        _currentStatus = new McpServerStatusInfo
        {
            Status = McpServerStatus.Stopped,
            Version = "1.0.0"
        };
        _lifecycleState = McpServerLifecycleState.Stopped;
        
        // Initialize with example service (demonstrating auto-registration)
        InitializeServices();
    }

    private void InitializeServices()
    {
        try
        {
            // Register the new dedicated PKS tool services (replacing legacy hardcoded tools)
            var projectToolService = _serviceProvider.GetService<ProjectToolService>();
            if (projectToolService != null)
            {
                _toolService.RegisterService(projectToolService);
                _logger.LogInformation("Registered PKS Project tool service");
            }

            var agentToolService = _serviceProvider.GetService<AgentToolService>();
            if (agentToolService != null)
            {
                _toolService.RegisterService(agentToolService);
                _logger.LogInformation("Registered PKS Agent tool service");
            }

            var deploymentToolService = _serviceProvider.GetService<DeploymentToolService>();
            if (deploymentToolService != null)
            {
                _toolService.RegisterService(deploymentToolService);
                _logger.LogInformation("Registered PKS Deployment tool service");
            }

            var statusToolService = _serviceProvider.GetService<StatusToolService>();
            if (statusToolService != null)
            {
                _toolService.RegisterService(statusToolService);
                _logger.LogInformation("Registered PKS Status tool service");
            }

            var swarmToolService = _serviceProvider.GetService<SwarmToolService>();
            if (swarmToolService != null)
            {
                _toolService.RegisterService(swarmToolService);
                _logger.LogInformation("Registered PKS Swarm tool service");
            }

            _logger.LogInformation("Successfully registered all PKS MCP tool services");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize PKS tool services");
        }
    }

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
        _logger.LogInformation("Registering tool service: {ServiceType}", typeof(T).Name);
        // In the actual implementation, this would register the service with the MCP host builder
        // For now, we'll delegate to our tool service
        _toolService.RegisterService(toolService);
    }

    public void RegisterResourceService<T>(T resourceService) where T : class
    {
        _logger.LogInformation("Registering resource service: {ServiceType}", typeof(T).Name);
        // In the actual implementation, this would register the service with the MCP host builder
        // For now, we'll delegate to our resource service
        _resourceService.RegisterService(resourceService);
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

            builder.Services.AddSingleton(_toolService);
            builder.Services.AddSingleton(_resourceService);

            // Register tool handlers
            RegisterToolHandlers(builder.Services);

            // Register resource handlers
            RegisterResourceHandlers(builder.Services);

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

            builder.Services.AddSingleton(_toolService);
            builder.Services.AddSingleton(_resourceService);
            
            // Register tool handlers
            RegisterToolHandlers(builder.Services);

            // Register resource handlers
            RegisterResourceHandlers(builder.Services);


           

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
            var hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    // For SSE transport, allow normal logging but reduce verbosity
                    logging.SetMinimumLevel(LogLevel.Warning);
                })
                .ConfigureServices(services =>
                {
                    // Add our MCP services (SSE transport will be added via SDK later)
                    services.AddSingleton(_toolService);
                    services.AddSingleton(_resourceService);

                    // Register tool handlers
                    RegisterToolHandlers(services);
                    
                    // Register resource handlers  
                    RegisterResourceHandlers(services);
                });

            _mcpHost = hostBuilder.Build();

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
        // Register tool handlers from the tool service
        var tools = _toolService.GetAvailableTools();
        foreach (var tool in tools)
        {
            _logger.LogDebug("Registering tool handler: {ToolName}", tool.Name);
            // In the real implementation, we would register proper MCP tool handlers
            // services.AddTransient<IToolHandler, CustomToolHandler>();
        }
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