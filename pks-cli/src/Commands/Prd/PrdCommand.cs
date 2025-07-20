using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using System.Text.Json;

namespace PKS.Commands.Prd;

/// <summary>
/// Settings for the main PRD command
/// </summary>
public class PrdMainSettings : CommandSettings
{
}

/// <summary>
/// Main PRD command that shows help when used without subcommands
/// This will be replaced by PrdBranchCommand for the new branch structure
/// </summary>
[Description("Manage Product Requirements Documents (PRDs) with AI-powered generation")]
public class PrdCommand : Command<PrdMainSettings>
{
    public override int Execute(CommandContext context, PrdMainSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        
        // Display help when no subcommand is provided
        AnsiConsole.MarkupLine("[cyan]PKS PRD Management[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Available commands:");
        AnsiConsole.MarkupLine("  [green]pks prd generate[/] <idea> - Generate PRD from idea description");
        AnsiConsole.MarkupLine("  [green]pks prd load[/] <file>     - Load and parse existing PRD");
        AnsiConsole.MarkupLine("  [green]pks prd requirements[/]    - List requirements from PRD");
        AnsiConsole.MarkupLine("  [green]pks prd status[/]          - Show PRD status and progress");
        AnsiConsole.MarkupLine("  [green]pks prd validate[/]        - Validate PRD completeness");
        AnsiConsole.MarkupLine("  [green]pks prd template[/] <name> - Generate PRD template");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Use [yellow]pks prd <command> --help[/] for more information about a command.");
        
        return 0;
    }
}

/// <summary>
/// Branch command for PRD management - this is the new structure that acts as a branch
/// </summary>
[Description("Manage Product Requirements Documents (PRDs) with AI-powered generation")]
public class PrdBranchCommand : Command<PrdBranchMainSettings>
{
    public override int Execute(CommandContext context, PrdBranchMainSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (settings.ShowVersion)
        {
            AnsiConsole.MarkupLine("[cyan]PKS PRD Management v1.0.0[/]");
            return 0;
        }

        if (settings.ListCommands)
        {
            DisplayCommandList();
            return 0;
        }

        // Default behavior: show help
        DisplayHelp();
        return 0;
    }

    private void DisplayHelp()
    {
        var helpContent = GetHelpContent();
        AnsiConsole.Write(new Markup(helpContent));
    }

    private void DisplayCommandList()
    {
        AnsiConsole.MarkupLine("[bold cyan]Available PRD Commands:[/]");
        AnsiConsole.WriteLine();

        var commands = new[]
        {
            ("generate", "Generate a comprehensive PRD from an idea description"),
            ("load", "Load and parse an existing PRD file"),
            ("requirements", "List and filter requirements from a PRD document"),
            ("status", "Display PRD status, progress, and statistics"),
            ("validate", "Validate PRD for completeness, consistency, and quality"),
            ("template", "Generate PRD templates for different project types")
        };

        foreach (var (name, description) in commands)
        {
            AnsiConsole.MarkupLine($"  [green]{name,-12}[/] {description}");
        }
    }

    public string GetHelpContent()
    {
        return """
            [cyan]PKS PRD Management - Manage Product Requirements Documents (PRDs) with AI-powered generation[/]

            [bold]USAGE:[/]
                pks prd [COMMAND] [OPTIONS]

            [bold]COMMANDS:[/]
                [green]generate[/]        Generate a comprehensive PRD from an idea description
                [green]load[/]           Load and parse an existing PRD file
                [green]requirements[/]   List and filter requirements from a PRD document
                [green]status[/]         Display PRD status, progress, and statistics
                [green]validate[/]       Validate PRD for completeness, consistency, and quality
                [green]template[/]       Generate PRD templates for different project types

            [bold]EXAMPLES:[/]
                pks prd generate "Build a task management app"
                pks prd load docs/PRD.md
                pks prd requirements --status draft
                pks prd status --watch
                pks prd validate --strict
                pks prd template MyProject --type web

            [bold]OPTIONS:[/]
                -h, --help     Show this help message
                -v, --version  Show version information
                -l, --list     List all available commands

            Use 'pks prd [COMMAND] --help' for more information about a specific command.
            """;
    }
}

/// <summary>
/// Settings for the main PRD branch command
/// </summary>
public class PrdBranchMainSettings : CommandSettings
{
    [CommandOption("-v|--version")]
    [Description("Show version information")]
    public bool ShowVersion { get; set; }

    [CommandOption("-l|--list")]
    [Description("List all available commands")]
    public bool ListCommands { get; set; }
}

