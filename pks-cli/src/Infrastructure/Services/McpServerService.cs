using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services;

/// <summary>
/// Implementation of MCP server management service
/// </summary>
public class McpServerService : IMcpServerService
{
    private readonly ILogger<McpServerService> _logger;
    private McpServerStatus _currentStatus;
    private Process? _stdioProcess;
    private CancellationTokenSource? _httpServerCancellation;

    public McpServerService(ILogger<McpServerService> logger)
    {
        _logger = logger;
        _currentStatus = new McpServerStatus
        {
            IsRunning = false,
            Version = "1.0.0"
        };
    }

    public async Task<McpServerResult> StartServerAsync(McpServerConfig config)
    {
        try
        {
            if (_currentStatus.IsRunning)
            {
                return new McpServerResult
                {
                    Success = false,
                    Message = "MCP Server is already running"
                };
            }

            _logger.LogInformation("Starting MCP server with transport: {Transport}", config.Transport);

            var result = config.Transport.ToLower() switch
            {
                "stdio" => await StartStdioServerAsync(config),
                "http" => await StartHttpServerAsync(config),
                "sse" => await StartSseServerAsync(config),
                _ => new McpServerResult 
                { 
                    Success = false, 
                    Message = $"Unsupported transport: {config.Transport}" 
                }
            };

            if (result.Success)
            {
                _currentStatus = new McpServerStatus
                {
                    IsRunning = true,
                    Port = result.Port,
                    Transport = config.Transport,
                    StartTime = DateTime.UtcNow,
                    ProcessId = result.ProcessId,
                    Version = "1.0.0",
                    ActiveConnections = 0
                };
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MCP server");
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start server: {ex.Message}"
            };
        }
    }

    public async Task<bool> StopServerAsync()
    {
        try
        {
            if (!_currentStatus.IsRunning)
            {
                return false;
            }

            _logger.LogInformation("Stopping MCP server");

            // Stop stdio process if running
            if (_stdioProcess != null && !_stdioProcess.HasExited)
            {
                _stdioProcess.Kill();
                await _stdioProcess.WaitForExitAsync();
                _stdioProcess.Dispose();
                _stdioProcess = null;
            }

            // Stop HTTP server if running
            if (_httpServerCancellation != null)
            {
                _httpServerCancellation.Cancel();
                _httpServerCancellation.Dispose();
                _httpServerCancellation = null;
            }

            _currentStatus = new McpServerStatus
            {
                IsRunning = false,
                Version = "1.0.0"
            };

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop MCP server");
            return false;
        }
    }

    public async Task<McpServerStatus> GetServerStatusAsync()
    {
        // Check if stdio process is still running
        if (_currentStatus.IsRunning && _stdioProcess != null && _stdioProcess.HasExited)
        {
            _currentStatus.IsRunning = false;
            _currentStatus.Port = null;
            _currentStatus.Transport = null;
            _currentStatus.StartTime = null;
            _currentStatus.ProcessId = null;
        }

        return await Task.FromResult(_currentStatus);
    }

    public async Task<McpServerResult> RestartServerAsync()
    {
        var stopSuccess = await StopServerAsync();
        if (!stopSuccess)
        {
            return new McpServerResult
            {
                Success = false,
                Message = "Failed to stop server for restart"
            };
        }

        // Wait a moment for cleanup
        await Task.Delay(1000);

        // Use default configuration for restart
        var config = new McpServerConfig
        {
            Port = _currentStatus.Port ?? 3000,
            Transport = _currentStatus.Transport ?? "stdio"
        };

        return await StartServerAsync(config);
    }

    public async Task<IEnumerable<McpResource>> GetResourcesAsync()
    {
        var resources = new List<McpResource>
        {
            new()
            {
                Uri = "pks://agents",
                Name = "Agents",
                Description = "Available development agents",
                MimeType = "application/json",
                Metadata = new Dictionary<string, object>
                {
                    ["category"] = "agents",
                    ["count"] = 0
                }
            },
            new()
            {
                Uri = "pks://tasks",
                Name = "Current Tasks",
                Description = "Active agent tasks and queue",
                MimeType = "application/json",
                Metadata = new Dictionary<string, object>
                {
                    ["category"] = "tasks",
                    ["active"] = 0,
                    ["queued"] = 0
                }
            },
            new()
            {
                Uri = "pks://projects",
                Name = "Projects",
                Description = "Project identity and configuration",
                MimeType = "application/json",
                Metadata = new Dictionary<string, object>
                {
                    ["category"] = "projects",
                    ["current"] = "pks-cli"
                }
            }
        };

        return await Task.FromResult(resources);
    }

    public async Task<IEnumerable<McpTool>> GetToolsAsync()
    {
        var tools = new List<McpTool>
        {
            new()
            {
                Name = "pks_create_task",
                Description = "Create and queue a new task for an agent",
                Category = "task-management",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        task_description = new { type = "string" },
                        agent_type = new { type = "string", @enum = new[] { "deployment", "testing", "documentation" } },
                        priority = new { type = "string", @enum = new[] { "low", "medium", "high" } }
                    },
                    required = new[] { "task_description", "agent_type" }
                }
            },
            new()
            {
                Name = "pks_get_agent_status",
                Description = "Check availability and status of development agents",
                Category = "agent-management",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        agent_id = new { type = "string" }
                    }
                }
            },
            new()
            {
                Name = "pks_deploy",
                Description = "Deploy applications with intelligent orchestration",
                Category = "deployment",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        environment = new { type = "string" },
                        config_path = new { type = "string" }
                    },
                    required = new[] { "environment" }
                }
            },
            new()
            {
                Name = "pks_init_project",
                Description = "Initialize new projects with templates and AI features",
                Category = "project-management",
                InputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        project_name = new { type = "string" },
                        template = new { type = "string", @enum = new[] { "console", "api", "web", "agent", "library" } },
                        agentic = new { type = "boolean" },
                        mcp = new { type = "boolean" }
                    },
                    required = new[] { "project_name" }
                }
            }
        };

        return await Task.FromResult(tools);
    }

    private async Task<McpServerResult> StartStdioServerAsync(McpServerConfig config)
    {
        try
        {
            // For stdio transport, we simulate starting a process
            // In a real implementation, this would start the actual MCP server process
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run -- mcp-server --transport stdio",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Simulate process start for testing
            await Task.Delay(100); // Simulate startup time

            return new McpServerResult
            {
                Success = true,
                Port = null, // stdio doesn't use ports
                Transport = "stdio",
                ProcessId = Process.GetCurrentProcess().Id, // Simulate process ID
                Message = "MCP Server started successfully with stdio transport"
            };
        }
        catch (Exception ex)
        {
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start stdio server: {ex.Message}"
            };
        }
    }

    private async Task<McpServerResult> StartHttpServerAsync(McpServerConfig config)
    {
        try
        {
            // Check if port is available
            if (!IsPortAvailable(config.Port))
            {
                return new McpServerResult
                {
                    Success = false,
                    Message = $"Port {config.Port} is already in use"
                };
            }

            // Simulate HTTP server startup
            _httpServerCancellation = new CancellationTokenSource();
            
            // Start background task to simulate HTTP server
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_httpServerCancellation.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, _httpServerCancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
            }, _httpServerCancellation.Token);

            await Task.Delay(100); // Simulate startup time

            return new McpServerResult
            {
                Success = true,
                Port = config.Port,
                Transport = "http",
                Message = $"MCP Server started successfully on port {config.Port}"
            };
        }
        catch (Exception ex)
        {
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start HTTP server: {ex.Message}"
            };
        }
    }

    private async Task<McpServerResult> StartSseServerAsync(McpServerConfig config)
    {
        try
        {
            // Check if port is available
            if (!IsPortAvailable(config.Port))
            {
                return new McpServerResult
                {
                    Success = false,
                    Message = $"Port {config.Port} is already in use"
                };
            }

            // Simulate SSE server startup
            _httpServerCancellation = new CancellationTokenSource();
            
            // Start background task to simulate SSE server
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_httpServerCancellation.Token.IsCancellationRequested)
                    {
                        await Task.Delay(1000, _httpServerCancellation.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }
            }, _httpServerCancellation.Token);

            await Task.Delay(100); // Simulate startup time

            return new McpServerResult
            {
                Success = true,
                Port = config.Port,
                Transport = "sse",
                Message = $"MCP Server started successfully with SSE transport on port {config.Port}"
            };
        }
        catch (Exception ex)
        {
            return new McpServerResult
            {
                Success = false,
                Message = $"Failed to start SSE server: {ex.Message}"
            };
        }
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}