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
            var success = await _hooksService.InitializeClaudeCodeHooksAsync(settings.Force, settings.Scope);

            if (success)
            {
                AnsiConsole.MarkupLine("[green]✓ Claude Code hooks configuration created[/]");

                var configPath = GetConfigurationPath(settings.Scope);
                var panel = new Panel(
                    "[yellow]Claude Code hooks have been configured![/]\n\n" +
                    "The following commands are now available for Claude Code:\n" +
                    "• [cyan]pks hooks pre-tool-use[/] - Before tool execution (PreToolUse)\n" +
                    "• [cyan]pks hooks post-tool-use[/] - After tool execution (PostToolUse)\n" +
                    "• [cyan]pks hooks user-prompt-submit[/] - Before prompt processing (UserPromptSubmit)\n" +
                    "• [cyan]pks hooks notification[/] - General notifications (Notification)\n" +
                    "• [cyan]pks hooks stop[/] - When agent stops responding (Stop)\n" +
                    "• [cyan]pks hooks subagent-stop[/] - When subagent stops (SubagentStop)\n" +
                    "• [cyan]pks hooks pre-compact[/] - Before compacting context (PreCompact)\n\n" +
                    $"[dim]Configuration written to {configPath}[/]\n" +
                    "[dim]Hook names now use PascalCase as expected by Claude Code[/]"
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

    private Task<int> ListHooksAsync()
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
            "[green]pks hooks notification[/]",
            "Notification",
            "Called for general notifications from Claude Code"
        );

        table.AddRow(
            "[green]pks hooks stop[/]",
            "Stop",
            "Called when Claude Code agent stops responding"
        );

        table.AddRow(
            "[green]pks hooks subagent-stop[/]",
            "SubagentStop",
            "Called when a Claude Code subagent stops"
        );

        table.AddRow(
            "[green]pks hooks pre-compact[/]",
            "PreCompact",
            "Called before Claude Code compacts context"
        );

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine("\n[dim]Use 'pks hooks init' to configure Claude Code integration[/]");
        return Task.FromResult(0);
    }

    private Task<int> ShowHelpAsync()
    {
        AnsiConsole.MarkupLine("[yellow]PKS Hooks - Claude Code Integration[/]");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[cyan]Usage:[/]");
        AnsiConsole.MarkupLine("  pks hooks init                          - Initialize Claude Code hooks (project scope)");
        AnsiConsole.MarkupLine("  pks hooks init --force                  - Force overwrite existing hooks");
        AnsiConsole.MarkupLine("  pks hooks init --scope user             - Initialize in user global settings");
        AnsiConsole.MarkupLine("  pks hooks init --scope project          - Initialize in project settings (default)");
        AnsiConsole.MarkupLine("  pks hooks init --scope local            - Initialize in local .claude folder");
        AnsiConsole.MarkupLine("  pks hooks list                          - List available hook events");
        AnsiConsole.MarkupLine("  pks hooks pre-tool-use                  - Handle PreToolUse event");
        AnsiConsole.MarkupLine("  pks hooks post-tool-use                 - Handle PostToolUse event");
        AnsiConsole.MarkupLine("  pks hooks user-prompt-submit            - Handle UserPromptSubmit event");
        AnsiConsole.MarkupLine("  pks hooks notification                  - Handle Notification event");
        AnsiConsole.MarkupLine("  pks hooks stop                          - Handle Stop event");
        AnsiConsole.MarkupLine("  pks hooks subagent-stop                 - Handle SubagentStop event");
        AnsiConsole.MarkupLine("  pks hooks pre-compact                   - Handle PreCompact event");

        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[dim]Note: Hook names now use PascalCase format (PreToolUse, PostToolUse, etc.) as expected by Claude Code.[/]");
        AnsiConsole.MarkupLine("[dim]Legacy camelCase hooks are automatically migrated to PascalCase during initialization.[/]");
        return Task.FromResult(0);
    }

    private static string GetConfigurationPath(SettingsScope scope)
    {
        return scope switch
        {
            SettingsScope.User => "~/.claude/settings.json",
            SettingsScope.Project => "./.claude/settings.json",
            SettingsScope.Local => "./.claude/settings.json",
            _ => "settings.json"
        };
    }
}

/// <summary>
/// Settings for the hooks command
/// </summary>
public class HooksSettings : CommandSettings
{
    [CommandOption("-f|--force")]
    [Description("Force overwrite existing hooks configuration")]
    [DefaultValue(false)]
    public bool Force { get; set; } = false;

    [CommandOption("-s|--scope")]
    [Description("Settings scope: user (global), project (current directory), or local (.claude folder)")]
    [DefaultValue(SettingsScope.Project)]
    public SettingsScope Scope { get; set; } = SettingsScope.Project;

    [CommandOption("-j|--json")]
    [Description("Output result in JSON format (suppresses banner and UI output)")]
    [DefaultValue(false)]
    public bool Json { get; set; } = false;
}

/// <summary>
/// Settings scope for Claude Code hooks configuration
/// </summary>
public enum SettingsScope
{
    /// <summary>
    /// Global user settings (~/.claude/settings.json)
    /// </summary>
    User,

    /// <summary>
    /// Project-specific settings (current directory/.claude/settings.json)
    /// </summary>
    Project,

    /// <summary>
    /// Local directory settings (./.claude/settings.json)
    /// </summary>
    Local
}