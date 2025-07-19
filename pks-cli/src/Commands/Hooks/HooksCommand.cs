using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for managing Claude Code hooks integration with smart dispatcher pattern
/// </summary>
public class HooksCommand : AsyncCommand<HooksSettings>
{
    private readonly IHooksService _hooksService;

    public HooksCommand(IHooksService hooksService)
    {
        _hooksService = hooksService ?? throw new ArgumentNullException(nameof(hooksService));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, HooksSettings settings)
    {
        try
        {
            return settings.Action switch
            {
                HookAction.List => await ListHooksAsync(),
                HookAction.Execute => await ExecuteHookAsync(settings),
                HookAction.Install => await InstallHookAsync(settings),
                HookAction.Remove => await RemoveHookAsync(settings),
                _ => await ListHooksAsync()
            };
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> ListHooksAsync()
    {
        AnsiConsole.MarkupLine("[cyan]Available Hooks[/]");
        
        var hooks = await _hooksService.GetAvailableHooksAsync();
        
        if (!hooks.Any())
        {
            AnsiConsole.MarkupLine("[dim]No hooks found.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Description");
        table.AddColumn("Parameters");

        foreach (var hook in hooks.OrderBy(h => h.Name))
        {
            var parameters = hook.Parameters.Any() 
                ? string.Join(", ", hook.Parameters)
                : "[dim]none[/]";
            
            table.AddRow(
                $"[green]{hook.Name}[/]",
                hook.Description,
                parameters
            );
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private async Task<int> ExecuteHookAsync(HooksSettings settings)
    {
        if (string.IsNullOrEmpty(settings.HookName))
        {
            throw new ArgumentException("Hook name is required for execute action");
        }

        AnsiConsole.MarkupLine($"[cyan]Executing hook[/]: [yellow]{settings.HookName}[/]");

        // Create hook context from parameters
        var context = new HookContext
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Parameters = ParseParameters(settings.Parameters)
        };

        // Show progress while executing
        var result = await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]Executing {settings.HookName}[/]");
                
                var hookResult = await _hooksService.ExecuteHookAsync(settings.HookName, context);
                task.Increment(100);
                
                return hookResult;
            });

        // Display results
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]✓ Hook executed successfully[/]");
            AnsiConsole.MarkupLine($"[dim]{result.Message}[/]");
            
            if (result.Output.Any())
            {
                AnsiConsole.MarkupLine("[cyan]Output:[/]");
                foreach (var output in result.Output)
                {
                    AnsiConsole.MarkupLine($"  [dim]{output.Key}:[/] {output.Value}");
                }
            }
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Hook execution failed[/]");
            AnsiConsole.MarkupLine($"[red]{result.Message}[/]");
            return 1;
        }
    }

    private async Task<int> InstallHookAsync(HooksSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Source))
        {
            AnsiConsole.MarkupLine("[red]Source is required for install action[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]Installing hook[/] from: [yellow]{settings.Source}[/]");

        var result = await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Installing hook[/]");
                
                var installResult = await _hooksService.InstallHookAsync(settings.Source);
                task.Increment(100);
                
                return installResult;
            });

        if (result.Success)
        {
            AnsiConsole.MarkupLine("[green]✓ Hook installed successfully[/]");
            AnsiConsole.MarkupLine($"[dim]Hook name: {result.HookName}[/]");
            AnsiConsole.MarkupLine($"[dim]Installed to: {result.InstalledPath}[/]");
            
            if (result.Dependencies.Any())
            {
                AnsiConsole.MarkupLine("[cyan]Dependencies:[/]");
                foreach (var dependency in result.Dependencies)
                {
                    AnsiConsole.MarkupLine($"  [dim]- {dependency}[/]");
                }
            }
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[red]✗ Hook installation failed[/]");
            AnsiConsole.MarkupLine($"[red]{result.Message}[/]");
            return 1;
        }
    }

    private async Task<int> RemoveHookAsync(HooksSettings settings)
    {
        if (string.IsNullOrEmpty(settings.HookName))
        {
            AnsiConsole.MarkupLine("[red]Hook name is required for remove action[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[cyan]Removing hook[/]: [yellow]{settings.HookName}[/]");

        var success = await _hooksService.RemoveHookAsync(settings.HookName);

        if (success)
        {
            AnsiConsole.MarkupLine("[green]✓ Hook removed successfully[/]");
            return 0;
        }
        else
        {
            AnsiConsole.MarkupLine("[red]✗ Failed to remove hook[/]");
            return 1;
        }
    }

    private Dictionary<string, object> ParseParameters(string[] parameters)
    {
        var result = new Dictionary<string, object>();

        foreach (var param in parameters)
        {
            var parts = param.Split('=', 2);
            if (parts.Length == 2)
            {
                result[parts[0]] = parts[1];
            }
            else
            {
                result[param] = true; // Boolean flag
            }
        }

        return result;
    }
}

/// <summary>
/// Settings for the hooks command
/// </summary>
public class HooksSettings : CommandSettings
{
    [CommandOption("-a|--action")]
    [Description("Action to perform (list, execute, install, remove)")]
    [DefaultValue(HookAction.List)]
    public HookAction Action { get; set; } = HookAction.List;

    [CommandOption("-n|--name")]
    [Description("Name of the hook to execute or remove")]
    public string HookName { get; set; } = string.Empty;

    [CommandOption("-s|--source")]
    [Description("Source URL or path for hook installation")]
    public string Source { get; set; } = string.Empty;

    [CommandOption("-p|--parameter")]
    [Description("Parameters to pass to the hook (format: key=value)")]
    public string[] Parameters { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Available hook actions
/// </summary>
public enum HookAction
{
    List,
    Execute,
    Install,
    Remove
}