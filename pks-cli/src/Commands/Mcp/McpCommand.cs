using System.ComponentModel;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.CLI.Commands.Mcp;

/// <summary>
/// Command for managing the Model Context Protocol (MCP) server
/// </summary>
public class McpCommand : AsyncCommand<McpSettings>
{
    private readonly IMcpServerService _mcpServerService;
    private readonly ILogger<McpCommand> _logger;

    public McpCommand(IMcpServerService mcpServerService, ILogger<McpCommand> logger)
    {
        _mcpServerService = mcpServerService;
        _logger = logger;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, McpSettings settings)
    {
        try
        {
            return settings.Action switch
            {
                McpAction.Start => await StartServerAsync(settings),
                McpAction.Stop => await StopServerAsync(settings),
                McpAction.Restart => await RestartServerAsync(settings),
                McpAction.Status => await ShowStatusAsync(settings),
                _ => await ShowStatusAsync(settings)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing MCP command");
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> StartServerAsync(McpSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Starting MCP Server...[/]");

        var config = new McpServerConfig
        {
            Port = settings.Port,
            Transport = settings.Transport,
            Debug = settings.Debug,
            ConfigFile = settings.ConfigFile
        };

        var result = await AnsiConsole.Status()
            .Start("Starting MCP server...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Star);
                ctx.SpinnerStyle(Style.Parse("green"));
                return await _mcpServerService.StartServerAsync(config);
            });

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] MCP Server started successfully");
            
            if (result.Port.HasValue)
            {
                AnsiConsole.MarkupLine($"[dim]Port:[/] [cyan]{result.Port}[/]");
            }
            
            AnsiConsole.MarkupLine($"[dim]Transport:[/] [cyan]{result.Transport}[/]");
            
            if (!string.IsNullOrEmpty(result.Message))
            {
                AnsiConsole.MarkupLine($"[dim]{result.Message}[/]");
            }

            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to start server: {result.Message}");
            return 1;
        }
    }

    private async Task<int> StopServerAsync(McpSettings settings)
    {
        var status = await _mcpServerService.GetServerStatusAsync();
        
        if (!status.IsRunning)
        {
            AnsiConsole.MarkupLine("[yellow]MCP Server is not running[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[cyan]Stopping MCP Server...[/]");

        var success = await AnsiConsole.Status()
            .Start("Stopping MCP server...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Star);
                ctx.SpinnerStyle(Style.Parse("red"));
                return await _mcpServerService.StopServerAsync();
            });

        if (success)
        {
            AnsiConsole.MarkupLine("[green]✓[/] MCP Server stopped successfully");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[red]✗[/] Failed to stop server");
            return 1;
        }
    }

    private async Task<int> RestartServerAsync(McpSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Restarting MCP Server...[/]");

        var result = await AnsiConsole.Status()
            .Start("Restarting MCP server...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Star);
                ctx.SpinnerStyle(Style.Parse("yellow"));
                return await _mcpServerService.RestartServerAsync();
            });

        if (result.Success)
        {
            AnsiConsole.MarkupLine("[green]✓[/] MCP Server restarted successfully");
            
            if (result.Port.HasValue)
            {
                AnsiConsole.MarkupLine($"[dim]Port:[/] [cyan]{result.Port}[/]");
            }
            
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to restart server: {result.Message}");
            return 1;
        }
    }

    private async Task<int> ShowStatusAsync(McpSettings settings)
    {
        AnsiConsole.MarkupLine("[bold cyan]MCP Server Status[/]");
        AnsiConsole.WriteLine();

        var status = await _mcpServerService.GetServerStatusAsync();

        var statusTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan2)
            .AddColumn("[cyan]Property[/]")
            .AddColumn("[yellow]Value[/]");

        statusTable.AddRow("Status", status.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]");
        statusTable.AddRow("Version", status.Version);
        
        if (status.Port.HasValue)
        {
            statusTable.AddRow("Port", status.Port.ToString()!);
        }
        
        if (!string.IsNullOrEmpty(status.Transport))
        {
            statusTable.AddRow("Transport", status.Transport);
        }
        
        if (status.StartTime.HasValue)
        {
            var uptime = DateTime.UtcNow - status.StartTime.Value;
            statusTable.AddRow("Uptime", uptime.ToString(@"hh\:mm\:ss"));
        }
        
        if (status.ProcessId.HasValue)
        {
            statusTable.AddRow("Process ID", status.ProcessId.ToString()!);
        }

        statusTable.AddRow("Active Connections", status.ActiveConnections.ToString());

        AnsiConsole.Write(statusTable);
        AnsiConsole.WriteLine();

        // Show resources and tools if server is running
        if (status.IsRunning)
        {
            await ShowResourcesAsync();
            await ShowToolsAsync();
        }

        return 0;
    }

    private async Task ShowResourcesAsync()
    {
        AnsiConsole.MarkupLine("[bold cyan]Available Resources[/]");
        
        var resources = await _mcpServerService.GetResourcesAsync();
        
        var resourceTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[green]URI[/]")
            .AddColumn("[yellow]Name[/]")
            .AddColumn("[dim]Description[/]");

        foreach (var resource in resources)
        {
            resourceTable.AddRow(resource.Uri, resource.Name, resource.Description);
        }

        AnsiConsole.Write(resourceTable);
        AnsiConsole.WriteLine();
    }

    private async Task ShowToolsAsync()
    {
        AnsiConsole.MarkupLine("[bold cyan]Available Tools[/]");
        
        var tools = await _mcpServerService.GetToolsAsync();
        
        var toolTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[blue]Tool[/]")
            .AddColumn("[yellow]Category[/]")
            .AddColumn("[dim]Description[/]");

        foreach (var tool in tools)
        {
            toolTable.AddRow(tool.Name, tool.Category, tool.Description);
        }

        AnsiConsole.Write(toolTable);
        AnsiConsole.WriteLine();
    }
}

/// <summary>
/// Settings for the MCP command
/// </summary>
public class McpSettings : CommandSettings
{
    [CommandOption("-a|--action")]
    [Description("Action to perform (start, stop, restart, status)")]
    public McpAction Action { get; set; } = McpAction.Status;

    [CommandOption("-p|--port")]
    [Description("Port number for HTTP/SSE transport")]
    [DefaultValue(3000)]
    public int Port { get; set; } = 3000;

    [CommandOption("-t|--transport")]
    [Description("Transport mode (stdio, http, sse)")]
    [DefaultValue("stdio")]
    public string Transport { get; set; } = "stdio";

    [CommandOption("-c|--config")]
    [Description("Path to configuration file")]
    public string ConfigFile { get; set; } = string.Empty;

    [CommandOption("-d|--debug")]
    [Description("Enable debug mode")]
    public bool Debug { get; set; } = false;
}

/// <summary>
/// Available MCP actions
/// </summary>
public enum McpAction
{
    Start,
    Stop,
    Restart,
    Status
}