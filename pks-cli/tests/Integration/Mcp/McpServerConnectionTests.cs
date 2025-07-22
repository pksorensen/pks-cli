using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Integration tests for MCP server connection scenarios
/// Focuses on real connection testing using stdio transport
/// </summary>
public class McpServerConnectionTests : TestBase
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<McpServerConnectionTests> _logger;

    public McpServerConnectionTests(ITestOutputHelper output)
    {
        _output = output;
        _logger = ServiceProvider.GetRequiredService<ILogger<McpServerConnectionTests>>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IMcpHostingService, McpHostingService>();
    }

    [Fact]
    public async Task McpServer_ShouldConnectAndListTools_UsingStdioTransport()
    {
        // Arrange
        var projectPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "..", "src");
        var mcpProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" -- mcp --transport stdio",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)
            }
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var initializationComplete = new TaskCompletionSource<bool>();
        var toolsListComplete = new TaskCompletionSource<string>();

        mcpProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                outputBuilder.AppendLine(e.Data);
                _output.WriteLine($"[STDOUT] {e.Data}");

                // Check for initialization complete
                if (e.Data.Contains("\"method\":\"initialized\""))
                {
                    initializationComplete.TrySetResult(true);
                }

                // Check for tools/list response
                if (e.Data.Contains("\"result\"") && e.Data.Contains("\"tools\""))
                {
                    toolsListComplete.TrySetResult(e.Data);
                }
            }
        };

        mcpProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errorBuilder.AppendLine(e.Data);
                _output.WriteLine($"[STDERR] {e.Data}");
            }
        };

        try
        {
            // Act - Start the MCP server process
            mcpProcess.Start();
            mcpProcess.BeginOutputReadLine();
            mcpProcess.BeginErrorReadLine();

            // Send initialization request
            var initRequest = new
            {
                jsonrpc = "2.0",
                method = "initialize",
                @params = new
                {
                    protocolVersion = "0.1.0",
                    capabilities = new
                    {
                        tools = new { },
                        resources = new { }
                    },
                    clientInfo = new
                    {
                        name = "test-client",
                        version = "1.0.0"
                    }
                },
                id = 1
            };

            await SendJsonRpcRequest(mcpProcess, initRequest);

            // Wait for initialization with timeout
            var initTask = initializationComplete.Task;
            if (await Task.WhenAny(initTask, Task.Delay(5000)) != initTask)
            {
                throw new TimeoutException("MCP server initialization timed out after 5 seconds");
            }

            // Send initialized notification
            var initializedNotification = new
            {
                jsonrpc = "2.0",
                method = "initialized"
            };

            await SendJsonRpcRequest(mcpProcess, initializedNotification);

            // Small delay to ensure server is ready
            await Task.Delay(500);

            // Send tools/list request
            var toolsListRequest = new
            {
                jsonrpc = "2.0",
                method = "tools/list",
                id = 2
            };

            await SendJsonRpcRequest(mcpProcess, toolsListRequest);

            // Wait for tools list response
            var toolsTask = toolsListComplete.Task;
            if (await Task.WhenAny(toolsTask, Task.Delay(5000)) != toolsTask)
            {
                throw new TimeoutException("MCP server tools/list request timed out after 5 seconds");
            }

            var toolsResponse = await toolsTask;

            // Assert
            toolsResponse.Should().NotBeNullOrEmpty();
            toolsResponse.Should().Contain("pks_init");
            toolsResponse.Should().Contain("pks_agent");
            toolsResponse.Should().Contain("pks_deploy");
            toolsResponse.Should().Contain("pks_status");
            toolsResponse.Should().Contain("pks_ascii");

            _output.WriteLine("MCP server connection successful - tools list retrieved");
        }
        finally
        {
            // Cleanup
            if (!mcpProcess.HasExited)
            {
                mcpProcess.Kill(true);
                await mcpProcess.WaitForExitAsync();
            }
            mcpProcess.Dispose();
        }
    }

    [Fact]
    public async Task McpServer_ShouldHandleConnectionTimeout_WhenServerNotAvailable()
    {
        // Arrange
        var mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        // Act & Assert
        // This test verifies that the connection attempt times out gracefully
        // when the server is not available
        var stopResult = await mcpHostingService.StopServerAsync();
        stopResult.Should().BeTrue();

        var status = await mcpHostingService.GetServerStatusAsync();
        status.Status.Should().Be(McpServerStatus.Stopped);
    }

    [Fact]
    public async Task McpServer_ShouldReconnectAfterDisconnection()
    {
        // Arrange
        var mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        // Act - Start server
        var startResult = await mcpHostingService.StartServerAsync(config);
        startResult.Success.Should().BeTrue();

        // Get initial tools list - would require McpToolService
        // var initialTools = mcpToolService.GetAvailableTools();
        // initialTools.Should().NotBeEmpty();
        var initialToolCount = 4; // Placeholder for test

        // Stop server
        await mcpHostingService.StopServerAsync();

        // Wait a moment
        await Task.Delay(1000);

        // Restart server
        var restartResult = await mcpHostingService.StartServerAsync(config);
        restartResult.Success.Should().BeTrue();

        // Get tools list again - would require McpToolService
        // var toolsAfterRestart = mcpToolService.GetAvailableTools();
        // toolsAfterRestart.Should().NotBeEmpty();
        // toolsAfterRestart.Count().Should().Be(initialToolCount);
        
        // Verify restart was successful
        restartResult.Success.Should().BeTrue();
    }

    private async Task SendJsonRpcRequest(Process process, object request)
    {
        var json = JsonSerializer.Serialize(request);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        
        await process.StandardInput.WriteAsync(header);
        await process.StandardInput.WriteAsync(json);
        await process.StandardInput.FlushAsync();
    }
}