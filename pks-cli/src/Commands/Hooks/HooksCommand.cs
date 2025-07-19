using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for managing Claude Code hooks integration
/// Handles Claude Code hook events: pre-tool-use, post-tool-use, etc.
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
            // Check the command name to determine action
            var commandName = context.Name?.ToLower();
            
            return commandName switch
            {
                "init" => await InitHooksAsync(settings),
                "list" => await ListHooksAsync(),
                _ => await ShowHelpAsync()
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> InitHooksAsync(HooksSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Initializing Claude Code hooks integration[/]");

        try
        {
            var success = await _hooksService.InitializeClaudeCodeHooksAsync(settings.Force);
            
            if (success)
            {
                AnsiConsole.MarkupLine("[green]✓ Claude Code hooks configuration created[/]");
                
                var panel = new Panel(
                    "[yellow]Claude Code hooks have been configured![/]\n\n" +
                    "The following commands are now available for Claude Code:\n" +
                    "• [cyan]pks hooks pre-tool-use[/] - Before tool execution\n" +
                    "• [cyan]pks hooks post-tool-use[/] - After tool execution\n" +
                    "• [cyan]pks hooks user-prompt-submit[/] - Before prompt processing\n" +
                    "• [cyan]pks hooks stop[/] - When agent stops responding\n\n" +
                    "[dim]Configuration written to ~/.claude/settings.json[/]"
                );
                panel.Header = new PanelHeader("[cyan]Setup Complete[/]");
                panel.Border = BoxBorder.Rounded;
                
                AnsiConsole.Write(panel);
                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine("[red]✗ Failed to initialize Claude Code hooks[/]");
                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error initializing hooks: {ex.Message}[/]");
            return 1;
        }
    }

    private async Task<int> ListHooksAsync()
    {
        AnsiConsole.MarkupLine("[cyan]Available Claude Code Hook Events[/]");
        
        var table = new Table();
        table.AddColumn("Command");
        table.AddColumn("Event Type");
        table.AddColumn("Description");

        table.AddRow(
            "[green]pks hooks pre-tool-use[/]",
            "PreToolUse",
            "Called before Claude Code executes a tool"
        );
        
        table.AddRow(
            "[green]pks hooks post-tool-use[/]",
            "PostToolUse", 
            "Called after Claude Code executes a tool"
        );
        
        table.AddRow(
            "[green]pks hooks user-prompt-submit[/]",
            "UserPromptSubmit",
            "Called before Claude Code processes user prompts"
        );
        
        table.AddRow(
            "[green]pks hooks stop[/]",
            "Stop",
            "Called when Claude Code agent stops responding"
        );

        AnsiConsole.Write(table);
        
        AnsiConsole.MarkupLine("\n[dim]Use 'pks hooks init' to configure Claude Code integration[/]");
        return 0;
    }

    private async Task<int> ShowHelpAsync()
    {
        AnsiConsole.MarkupLine("[yellow]PKS Hooks - Claude Code Integration[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[cyan]Usage:[/]");
        AnsiConsole.MarkupLine("  pks hooks init                   - Initialize Claude Code hooks");
        AnsiConsole.MarkupLine("  pks hooks init --force           - Force overwrite existing hooks");
        AnsiConsole.MarkupLine("  pks hooks list                   - List available hook events");
        AnsiConsole.MarkupLine("  pks hooks pre-tool-use           - Handle PreToolUse event");
        AnsiConsole.MarkupLine("  pks hooks post-tool-use          - Handle PostToolUse event");
        AnsiConsole.MarkupLine("  pks hooks user-prompt-submit     - Handle UserPromptSubmit event");
        AnsiConsole.MarkupLine("  pks hooks stop                   - Handle Stop event");
        return 0;
    }
}

/// <summary>
/// Settings for the hooks command
/// </summary>
public class HooksSettings : CommandSettings
{
    [CommandOption("-a|--action")]
    [Description("Action to perform (init, list)")]
    [DefaultValue(HookAction.List)]
    public HookAction Action { get; set; } = HookAction.List;
    
    [CommandOption("-f|--force")]
    [Description("Force overwrite existing hooks configuration")]
    [DefaultValue(false)]
    public bool Force { get; set; } = false;
}

/// <summary>
/// Available hook actions
/// </summary>
public enum HookAction
{
    Init,
    List
}