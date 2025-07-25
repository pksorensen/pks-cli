using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PKS.CLI.Infrastructure.Services.MCP;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.CLI.Commands.Mcp;

/// <summary>
/// Command for managing the Model Context Protocol (MCP) server
/// </summary>
public class McpCommand : AsyncCommand<McpSettings>
{
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpConfiguration _mcpConfiguration;
    private readonly ILogger<McpCommand> _logger;

    public McpCommand(
        IMcpHostingService mcpHostingService,
        IOptions<McpConfiguration> mcpConfiguration,
        ILogger<McpCommand> logger)
    {
        _mcpHostingService = mcpHostingService;
        _mcpConfiguration = mcpConfiguration.Value;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, McpSettings settings)
    {
        try
        {
            var isStdioTransport = string.Equals(settings.Transport, "stdio", StringComparison.OrdinalIgnoreCase);

            // Suppress logging to stdout for stdio transport
            if (!isStdioTransport)
            {
                _logger.LogInformation("Starting SDK-based MCP server with transport: {Transport}", settings.Transport);
            }

            var config = new McpServerConfig
            {
                Transport = settings.Transport,
                Port = settings.Port,
                Debug = settings.Debug,
                ConfigFile = settings.ConfigFile
            };

            var cancellationTokenSource = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            var result = await _mcpHostingService.StartServerAsync(config, cancellationTokenSource.Token);

            if (!result.Success)
            {
                if (!isStdioTransport)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] Failed to start MCP server: {result.Message}");
                }
                return 1;
            }

            if (settings.Debug && !isStdioTransport)
            {
                var transportInfo = settings.Transport.ToLower() switch
                {
                    "stdio" => "STDIO transport",
                    "http" => $"HTTP transport on port {result.Port}",
                    "sse" => $"SSE transport on port {result.Port}",
                    _ => $"{settings.Transport} transport"
                };

                AnsiConsole.MarkupLine($"[green]✓[/] MCP Server started successfully with {transportInfo}");
                AnsiConsole.MarkupLine("[dim]Server is ready to accept connections. Press Ctrl+C to stop.[/]");
            }

            // Wait for cancellation
            try
            {
                await Task.Delay(-1, cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (settings.Debug && !isStdioTransport)
                {
                    AnsiConsole.MarkupLine("[cyan]Stopping MCP Server...[/]");
                }

                await _mcpHostingService.StopServerAsync();

                if (settings.Debug && !isStdioTransport)
                {
                    AnsiConsole.MarkupLine("[green]✓[/] MCP Server stopped successfully");
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            var isStdioTransport = string.Equals(settings.Transport, "stdio", StringComparison.OrdinalIgnoreCase);

            if (!isStdioTransport)
            {
                _logger.LogError(ex, "Error starting MCP server");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }
            return 1;
        }
    }
}

/// <summary>
/// Settings for the MCP command
/// </summary>
public class McpSettings : CommandSettings
{
    [CommandOption("-t|--transport")]
    [Description("Transport mode (stdio, http, sse)")]
    [DefaultValue("stdio")]
    public string Transport { get; set; } = "stdio";

    [CommandOption("-p|--port")]
    [Description("Port number for HTTP/SSE transport")]
    [DefaultValue(3000)]
    public int Port { get; set; } = 3000;

    [CommandOption("-d|--debug")]
    [Description("Enable debug mode with verbose logging")]
    public bool Debug { get; set; } = false;

    [CommandOption("-c|--config")]
    [Description("Path to configuration file")]
    public string? ConfigFile { get; set; }
}