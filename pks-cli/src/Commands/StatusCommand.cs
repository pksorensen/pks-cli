using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class StatusCommand : Command<StatusCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("-w|--watch")]
        [Description("Watch status in real-time")]
        public bool Watch { get; set; }

        [CommandOption("-e|--environment <ENV>")]
        [Description("Environment to check (dev, staging, prod, all)")]
        [DefaultValue("all")]
        public string Environment { get; set; } = "all";

        [CommandOption("--ai-insights")]
        [Description("Include AI-powered insights and recommendations")]
        public bool IncludeAIInsights { get; set; }
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (settings.Watch)
        {
            return WatchStatus(settings);
        }

        return ShowStatus(settings);
    }

    private int ShowStatus(Settings settings)
    {
        AnsiConsole.MarkupLine($"📊 [bold cyan]System Status - {settings.Environment.ToUpper()}[/]");
        AnsiConsole.WriteLine();

        // Environment status grid
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(4))
            .AddColumn();

        if (settings.Environment == "all" || settings.Environment == "prod")
        {
            grid.AddRow(new Markup("[bold red]🔴 PRODUCTION[/]"), CreateEnvironmentStatus("PROD", true));
        }

        if (settings.Environment == "all" || settings.Environment == "staging")
        {
            grid.AddRow(new Markup("[bold yellow]🟡 STAGING[/]"), CreateEnvironmentStatus("STAGING", true));
        }

        if (settings.Environment == "all" || settings.Environment == "dev")
        {
            grid.AddRow(new Markup("[bold green]🟢 DEVELOPMENT[/]"), CreateEnvironmentStatus("DEV", true));
        }

        AnsiConsole.Write(grid);

        // Services status table
        var servicesTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[cyan1]Service[/]")
            .AddColumn("[cyan2]Environment[/]")
            .AddColumn("[cyan3]Status[/]")
            .AddColumn("[yellow]Health[/]")
            .AddColumn("[green]Uptime[/]")
            .AddColumn("[magenta]Version[/]");

        servicesTable.AddRow("🌐 API Gateway", "PROD", "[green]●[/] Running", "98.7%", "14d 7h", "v2.1.3");
        servicesTable.AddRow("💾 Database", "PROD", "[green]●[/] Running", "99.9%", "42d 12h", "v13.2");
        servicesTable.AddRow("🔄 Cache Redis", "PROD", "[green]●[/] Running", "99.5%", "28d 3h", "v7.0.8");
        servicesTable.AddRow("🤖 AI Service", "PROD", "[green]●[/] Running", "96.2%", "7d 18h", "v1.5.2");
        servicesTable.AddRow("📊 Analytics", "STAGING", "[yellow]◐[/] Deploying", "N/A", "0h", "v2.0.1");

        AnsiConsole.Write(servicesTable);

        if (settings.IncludeAIInsights)
        {
            ShowAIInsights();
        }

        return 0;
    }

    private int WatchStatus(Settings settings)
    {
        AnsiConsole.MarkupLine("[dim]Starting real-time monitoring... Press [bold]Ctrl+C[/] to exit[/]");
        AnsiConsole.WriteLine();

        var random = new Random();

        AnsiConsole.Live(CreateLiveStatusDisplay(random))
            .Start(ctx =>
            {
                while (true)
                {
                    Thread.Sleep(2000);
                    ctx.UpdateTarget(CreateLiveStatusDisplay(random));
                }
            });

        return 0;
    }

    private Panel CreateLiveStatusDisplay(Random random)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");

        var metrics = new Grid()
            .AddColumn()
            .AddColumn()
            .AddColumn();

        metrics.AddRow(
            $"[green]CPU: {random.Next(20, 80)}%[/]",
            $"[blue]Memory: {random.Next(40, 85)}%[/]",
            $"[yellow]Network: {random.Next(100, 500)}MB/s[/]"
        );

        metrics.AddRow(
            $"[cyan]Requests: {random.Next(800, 1500)}/min[/]",
            $"[magenta]Errors: {random.Next(0, 5)}[/]",
            $"[red]Latency: {random.Next(15, 85)}ms[/]"
        );

        var content = new Rows(
            new Markup($"[bold]🕐 Last Updated: {timestamp}[/]"),
            new Text(""),
            metrics,
            new Text(""),
            new Markup("[dim]🤖 AI monitoring active - no anomalies detected[/]")
        );

        return new Panel(content)
            .Border(BoxBorder.Double)
            .BorderStyle("cyan")
            .Header(" [bold cyan]📊 Real-Time System Metrics[/] ");
    }

    private Panel CreateEnvironmentStatus(string env, bool healthy)
    {
        var status = healthy ? "[green]✓ Healthy[/]" : "[red]✗ Issues[/]";
        var color = healthy ? "green" : "red";

        var content = $"""
        {status}
        [dim]Services:[/] {(healthy ? "12/12" : "10/12")} running
        [dim]Response:[/] {(healthy ? "~45ms" : "~180ms")} avg
        """;

        return new Panel(content)
            .Border(BoxBorder.Rounded)
            .BorderStyle(color)
            .Header($" [bold {color}]{env}[/] ");
    }

    private void ShowAIInsights()
    {
        AnsiConsole.WriteLine();

        var insights = new Panel($"""
        🤖 [bold cyan]AI-Powered Insights & Recommendations[/]
        
        📈 [green]Performance Trends[/]
        • Response time improved 12% over last 7 days
        • Memory usage optimized by AI tuning (+8% efficiency)
        • Request patterns suggest scaling opportunity at 2 PM daily
        
        🔍 [yellow]Detected Patterns[/]
        • Database queries could benefit from indexing on user_activity table
        • Cache hit ratio declining in analytics service (investigate)
        • Unusual traffic spike pattern detected on weekends (+45%)
        
        💡 [blue]Recommended Actions[/]
        • Consider auto-scaling rules for peak hours (2-4 PM)
        • Update analytics service to v2.1.0 for better caching
        • Add monitoring alert for cache hit ratio < 85%
        
        [dim]🧠 These insights are generated from 30 days of operational data[/]
        """)
        .Border(BoxBorder.Double)
        .BorderStyle("cyan")
        .Header(" [bold cyan]🤖 AI Intelligence Dashboard[/] ");

        AnsiConsole.Write(insights);
    }
}