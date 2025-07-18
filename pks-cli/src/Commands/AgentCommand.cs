using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class AgentCommand : Command<AgentCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[ACTION]")]
        [Description("Action to perform (create, list, remove, status)")]
        public string? Action { get; set; }

        [CommandOption("-n|--name <NAME>")]
        [Description("Name of the agent")]
        public string? AgentName { get; set; }

        [CommandOption("-t|--type <TYPE>")]
        [Description("Type of agent (developer, tester, architect, devops)")]
        [DefaultValue("developer")]
        public string AgentType { get; set; } = "developer";

        [CommandOption("-s|--skills <SKILLS>")]
        [Description("Comma-separated list of skills")]
        public string? Skills { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var action = settings.Action?.ToLower() ?? 
            AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("What would you like to do with [cyan]AI agents[/]?")
                    .AddChoices("create", "list", "status", "remove"));

        switch (action)
        {
            case "create":
                return CreateAgent(settings);
            case "list":
                return ListAgents();
            case "status":
                return ShowAgentStatus();
            case "remove":
                return RemoveAgent(settings);
            default:
                AnsiConsole.MarkupLine("[red]Unknown action. Use: create, list, status, or remove[/]");
                return 1;
        }
    }

    private int CreateAgent(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.AgentName))
        {
            settings.AgentName = AnsiConsole.Ask<string>("What's the [green]name[/] of your AI agent?");
        }

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots12)
            .SpinnerStyle(Style.Parse("cyan bold"))
            .Start($"Creating AI agent '{settings.AgentName}'...", ctx =>
            {
                ctx.Status("Initializing neural networks...");
                Thread.Sleep(1500);
                
                ctx.Status("Training on your codebase...");
                Thread.Sleep(2000);
                
                ctx.Status("Setting up automation workflows...");
                Thread.Sleep(1500);
            });

        var agentPanel = new Panel($"""
        🤖 [bold green]Agent '{settings.AgentName}' created successfully![/]
        
        [cyan1]Type:[/] {settings.AgentType.ToUpper()}
        [cyan1]Skills:[/] {settings.Skills ?? "General development, testing, code review"}
        [cyan1]Status:[/] [green]Active and learning[/]
        
        Your agent is now:
        • 📖 Analyzing your codebase patterns
        • 🧠 Learning your development style  
        • ⚡ Ready to assist with automation
        
        [dim]Use 'pks agent status' to monitor learning progress[/]
        """)
        .Border(BoxBorder.Rounded)
        .BorderStyle("green")
        .Header(" [bold green]🤖 AI Agent Ready[/] ");

        AnsiConsole.Write(agentPanel);
        return 0;
    }

    private int ListAgents()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[cyan1]Name[/]")
            .AddColumn("[cyan2]Type[/]")
            .AddColumn("[cyan3]Status[/]")
            .AddColumn("[cyan]Last Active[/]")
            .AddColumn("[yellow]Tasks Completed[/]");

        // Simulate some agents
        table.AddRow("🧠 CodeMaster", "Developer", "[green]Active[/]", "2 minutes ago", "147");
        table.AddRow("🧪 TestGuru", "Tester", "[green]Active[/]", "5 minutes ago", "89");
        table.AddRow("🏗️ ArchWiz", "Architect", "[yellow]Learning[/]", "1 hour ago", "23");
        table.AddRow("🚀 DeployBot", "DevOps", "[green]Active[/]", "30 seconds ago", "312");

        AnsiConsole.Write(table);
        return 0;
    }

    private int ShowAgentStatus()
    {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn()
            .AddRow("[bold]🤖 Agent Swarm Status[/]", "")
            .AddRow("", "")
            .AddRow("🧠 [cyan1]Neural Activity[/]", CreateProgressBar(87, "cyan1"))
            .AddRow("📊 [cyan2]Learning Progress[/]", CreateProgressBar(64, "cyan2"))
            .AddRow("⚡ [cyan3]Automation Level[/]", CreateProgressBar(92, "cyan3"))
            .AddRow("🎯 [yellow]Task Efficiency[/]", CreateProgressBar(78, "yellow"));

        var panel = new Panel(grid)
            .Border(BoxBorder.Double)
            .BorderStyle("cyan")
            .Header(" [bold cyan]🚀 Agentic System Dashboard[/] ");

        AnsiConsole.Write(panel);
        
        // Real-time activity feed
        AnsiConsole.MarkupLine("\n[bold]📡 Recent Agent Activity:[/]");
        AnsiConsole.MarkupLine("[dim]• CodeMaster optimized 3 database queries[/]");
        AnsiConsole.MarkupLine("[dim]• TestGuru added 12 new test cases[/]");
        AnsiConsole.MarkupLine("[dim]• DeployBot automated CI/CD pipeline[/]");
        
        return 0;
    }

    private int RemoveAgent(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.AgentName))
        {
            settings.AgentName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Which agent would you like to [red]remove[/]?")
                    .AddChoices("CodeMaster", "TestGuru", "ArchWiz", "DeployBot"));
        }

        var confirm = AnsiConsole.Confirm($"Are you sure you want to remove agent '[red]{settings.AgentName}[/]'?");
        
        if (confirm)
        {
            AnsiConsole.MarkupLine($"[red]🗑️ Agent '{settings.AgentName}' has been removed.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]Operation cancelled.[/]");
        }

        return 0;
    }

    private string CreateProgressBar(int value, string color)
    {
        var filled = value / 5;
        var empty = 20 - filled;
        var bar = new string('█', filled) + new string('░', empty);
        return $"[{color}]{bar}[/] {value}%";
    }
}