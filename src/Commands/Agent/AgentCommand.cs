using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Agent;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agent;

/// <summary>
/// Command for managing AI agents
/// </summary>
public class AgentCommand : AsyncCommand<AgentSettings>
{
    private readonly IAgentFrameworkService _agentService;
    private readonly ILogger<AgentCommand> _logger;
    private readonly CodingAgentService? _codingAgent;

    public AgentCommand(
        IAgentFrameworkService agentService,
        ILogger<AgentCommand> logger,
        CodingAgentService? codingAgent = null)
    {
        _agentService = agentService;
        _logger = logger;
        _codingAgent = codingAgent;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, AgentSettings settings)
    {
        try
        {
            // If a positional prompt is provided, route to the coding-agent loop.
            if (!string.IsNullOrWhiteSpace(settings.Prompt))
            {
                if (_codingAgent is null)
                {
                    AnsiConsole.MarkupLine("[red]CodingAgentService not registered — DI wiring is incomplete.[/]");
                    return 1;
                }
                var options = new CodingAgentRunOptions(
                    Prompt: settings.Prompt!,
                    ModelId: string.IsNullOrWhiteSpace(settings.Model) ? "gpt-5.5" : settings.Model,
                    Cwd: string.IsNullOrWhiteSpace(settings.Cwd) ? null : settings.Cwd,
                    SkillName: string.IsNullOrWhiteSpace(settings.Skill) ? null : settings.Skill,
                    MaxTurns: settings.MaxTurns > 0 ? settings.MaxTurns : 50,
                    ReadOnly: settings.ReadOnly);
                return await _codingAgent.RunAsync(options, CancellationToken.None);
            }

            return settings.Action switch
            {
                AgentAction.Create => await CreateAgentAsync(settings),
                AgentAction.List => await ListAgentsAsync(settings),
                AgentAction.Status => await GetAgentStatusAsync(settings),
                AgentAction.Start => await StartAgentAsync(settings),
                AgentAction.Stop => await StopAgentAsync(settings),
                AgentAction.Remove => await RemoveAgentAsync(settings),
                _ => await ListAgentsAsync(settings) // Default to list
            };
        }
        catch (AgentNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]❌ Agent not found: {ex.Message}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing agent command");
            AnsiConsole.MarkupLine($"[red]❌ Error: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> CreateAgentAsync(AgentSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]🤖 Creating agent...[/]");

        var configuration = await BuildAgentConfigurationAsync(settings);
        if (configuration == null)
        {
            return 1;
        }

        var result = await _agentService.CreateAgentAsync(configuration);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✅ Agent created successfully[/]");
            AnsiConsole.MarkupLine($"[dim]Agent ID: {result.AgentId}[/]");
            AnsiConsole.MarkupLine($"[dim]Name: {configuration.Name}[/]");
            AnsiConsole.MarkupLine($"[dim]Type: {configuration.Type}[/]");

            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                AnsiConsole.MarkupLine($"[green]{result.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]❌ Failed to create agent[/]");
            AnsiConsole.MarkupLine($"[red]{result.Message}[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> ListAgentsAsync(AgentSettings settings)
    {
        var agents = await _agentService.ListAgentsAsync();

        if (!agents.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No agents found[/]");
            AnsiConsole.MarkupLine("[dim]Use 'pks agent create <name>' to create your first agent.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[cyan1]Name[/]")
            .AddColumn("[cyan2]Type[/]")
            .AddColumn("[cyan3]Status[/]")
            .AddColumn("[cyan]Created[/]")
            .AddColumn("[yellow]Last Activity[/]");

        table.Title = new TableTitle("[bold cyan]Available Agents[/]");

        foreach (var agent in agents)
        {
            var lastActivity = agent.LastActivity?.ToString("yyyy-MM-dd HH:mm") ?? "Never";
            var createdAt = agent.CreatedAt.ToString("yyyy-MM-dd");

            table.AddRow(
                agent.Name,
                agent.Type,
                GetStatusMarkup(agent.Status),
                createdAt,
                lastActivity
            );
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private async Task<int> GetAgentStatusAsync(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentId))
        {
            AnsiConsole.MarkupLine("[red]❌ Agent ID is required for status command[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[cyan]🔍 Getting agent status...[/]");

        var status = await _agentService.GetAgentStatusAsync(settings.AgentId);

        var panel = new Panel($"""
        [cyan1]Agent ID:[/] {status.Id}
        [cyan2]Status:[/] {GetStatusMarkup(status.Status)}
        [cyan3]Last Activity:[/] {status.LastActivity?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Never"}
        [yellow]Message Queue:[/] {status.MessageQueueCount} pending messages
        [green]Current Tasks:[/] {string.Join(", ", status.CurrentTasks)}
        
        {(!string.IsNullOrWhiteSpace(status.Details) ? $"[dim]{status.Details}[/]" : "")}
        """)
        .Border(BoxBorder.Rounded)
        .BorderStyle("cyan")
        .Header($" [bold cyan]🤖 Agent Status[/] ");

        AnsiConsole.Write(panel);
        return 0;
    }

    private async Task<int> StartAgentAsync(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentId))
        {
            AnsiConsole.MarkupLine("[red]❌ Agent ID is required for start command[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[cyan]🚀 Starting agent...[/]");

        var result = await _agentService.StartAgentAsync(settings.AgentId);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✅ Agent started[/]");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                AnsiConsole.MarkupLine($"[green]{result.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]❌ Failed to start agent[/]");
            AnsiConsole.MarkupLine($"[red]{result.Message}[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> StopAgentAsync(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentId))
        {
            AnsiConsole.MarkupLine("[red]❌ Agent ID is required for stop command[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[cyan]🛑 Stopping agent...[/]");

        var result = await _agentService.StopAgentAsync(settings.AgentId);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✅ Agent stopped[/]");
            if (!string.IsNullOrWhiteSpace(result.Message))
            {
                AnsiConsole.MarkupLine($"[green]{result.Message}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]❌ Failed to stop agent[/]");
            AnsiConsole.MarkupLine($"[red]{result.Message}[/]");
            return 1;
        }

        return 0;
    }

    private async Task<int> RemoveAgentAsync(AgentSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AgentId))
        {
            AnsiConsole.MarkupLine("[red]❌ Agent ID is required for remove command[/]");
            return 1;
        }

        AnsiConsole.MarkupLine("[cyan]🗑️ Removing agent...[/]");

        var success = await _agentService.RemoveAgentAsync(settings.AgentId);

        if (success)
        {
            AnsiConsole.MarkupLine($"[green]✅ Agent removed successfully[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]❌ Failed to remove agent[/]");
            return 1;
        }

        return 0;
    }

    private async Task<AgentConfiguration?> BuildAgentConfigurationAsync(AgentSettings settings)
    {
        AgentConfiguration? configuration = null;

        // Load from config file if specified
        if (!string.IsNullOrWhiteSpace(settings.ConfigFile))
        {
            AnsiConsole.MarkupLine($"[cyan]📄 Loading configuration from file: {settings.ConfigFile}[/]");

            try
            {
                configuration = await _agentService.LoadConfigurationAsync(settings.ConfigFile);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]❌ Failed to load configuration file: {ex.Message}[/]");
                return null;
            }
        }
        else
        {
            // Build configuration from command line options
            configuration = new AgentConfiguration
            {
                Name = settings.Name,
                Type = settings.Type,
                Settings = new Dictionary<string, object>()
            };

            // Parse additional settings
            if (settings.Settings?.Any() == true)
            {
                foreach (var setting in settings.Settings)
                {
                    var parts = setting.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        configuration.Settings[parts[0]] = parts[1];
                    }
                }
            }
        }

        // Validate configuration
        if (string.IsNullOrWhiteSpace(configuration.Name))
        {
            AnsiConsole.MarkupLine("[red]❌ Agent name is required[/]");
            return null;
        }

        return configuration;
    }

    private string GetStatusMarkup(string status)
    {
        return status.ToLower() switch
        {
            "active" => "[green]Active[/]",
            "inactive" => "[gray]Inactive[/]",
            "error" => "[red]Error[/]",
            "starting" => "[yellow]Starting[/]",
            "stopping" => "[orange1]Stopping[/]",
            _ => $"[dim]{status}[/]"
        };
    }
}

/// <summary>
/// Settings for the agent command
/// </summary>
public class AgentSettings : CommandSettings
{
    [CommandOption("-a|--action")]
    [Description("Action to perform (create, list, status, start, stop, remove)")]
    [DefaultValue(AgentAction.List)]
    public AgentAction Action { get; set; } = AgentAction.List;

    [CommandOption("-n|--name")]
    [Description("Name of the agent")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("-t|--type")]
    [Description("Type of agent (automation, monitoring, deployment, etc.)")]
    [DefaultValue("automation")]
    public string Type { get; set; } = "automation";

    [CommandOption("-i|--id")]
    [Description("Agent ID for status, start, stop, or remove operations")]
    public string AgentId { get; set; } = string.Empty;

    [CommandOption("-c|--config")]
    [Description("Path to agent configuration file")]
    public string ConfigFile { get; set; } = string.Empty;

    [CommandOption("-s|--settings")]
    [Description("Additional settings in key=value format")]
    public string[] Settings { get; set; } = Array.Empty<string>();

    // ----- Coding-agent (one-shot LLM task) settings -----

    [CommandArgument(0, "[prompt]")]
    [Description("Prompt for the one-shot LLM agent (read/write/edit/bash tools available). When provided, all other action options are ignored.")]
    public string? Prompt { get; set; }

    [CommandOption("-m|--model")]
    [Description("Model id for the coding agent. Default: gpt-5.5")]
    public string? Model { get; set; }

    [CommandOption("--cwd")]
    [Description("Sandbox root for tool calls. Default: current working directory")]
    public string? Cwd { get; set; }

    [CommandOption("--skill")]
    [Description("Skill name (loads ~/.pks-cli/agent-skills/<name>.md as the system prompt)")]
    public string? Skill { get; set; }

    [CommandOption("--max-turns")]
    [Description("Maximum tool-call iterations. Default: 50")]
    public int MaxTurns { get; set; }

    [CommandOption("--read-only")]
    [Description("Disable mutating tools (write, edit, bash); leaves read, grep, find, ls enabled")]
    public bool ReadOnly { get; set; }
}

/// <summary>
/// Available agent actions
/// </summary>
public enum AgentAction
{
    Create,
    List,
    Status,
    Start,
    Stop,
    Remove
}