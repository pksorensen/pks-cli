using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;

// Set UTF-8 encoding for proper ASCII art display
Console.OutputEncoding = Encoding.UTF8;

// Display welcome banner with fancy ASCII art
DisplayWelcomeBanner();

// Configure the application
var services = new ServiceCollection();

// Register infrastructure services
services.AddSingleton<IKubernetesService, KubernetesService>();
services.AddSingleton<IConfigurationService, ConfigurationService>();
services.AddSingleton<IDeploymentService, DeploymentService>();

// Register individual initializers as transient services
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.AgenticFeaturesInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ReadmeInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ClaudeDocumentationInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.McpConfigurationInitializer>();

// Register initializer system with configured registry
services.AddSingleton<IInitializerRegistry>(serviceProvider =>
{
    var registry = new InitializerRegistry(serviceProvider);
    
    // Register initializer types
    registry.Register<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.AgenticFeaturesInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.ReadmeInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.ClaudeDocumentationInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.McpConfigurationInitializer>();
    
    return registry;
});

services.AddSingleton<IInitializationService, InitializationService>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("pks");
    config.SetApplicationVersion("1.0.0");
    
    // Add commands
    config.AddCommand<InitCommand>("init")
        .WithDescription("Initialize a new project with agentic capabilities");
        
    config.AddCommand<DeployCommand>("deploy")
        .WithDescription("Deploy applications with intelligent orchestration");
        
    config.AddCommand<StatusCommand>("status")
        .WithDescription("View system status with real-time insights");
        
    config.AddCommand<AgentCommand>("agent")
        .WithDescription("Manage AI agents for development automation");
        
    config.AddCommand<AsciiCommand>("ascii")
        .WithDescription("Generate beautiful ASCII art for your projects");
        
    config.AddCommand<McpCommand>("mcp")
        .WithDescription("Model Context Protocol (MCP) server for AI integration");
});

return await app.RunAsync(args);

static void DisplayWelcomeBanner()
{
    var banner = """
    ██████╗ ██╗  ██╗███████╗
    ██╔══██╗██║ ██╔╝██╔════╝
    ██████╔╝█████╔╝ ███████╗
    ██╔═══╝ ██╔═██╗ ╚════██║
    ██║     ██║  ██╗███████║
    ╚═╝     ╚═╝  ╚═╝╚══════╝
    
    🤖 Professional Agentic Simplifier
    🚀 The Next Agentic CLI for .NET Developers
    """;

    var rule = new Rule("[bold cyan]PKS CLI - Agentic Development Assistant[/]")
        .RuleStyle("cyan");
    
    AnsiConsole.Write(rule);
    AnsiConsole.WriteLine();
    
    // Display the ASCII art banner in gradient colors
    var lines = banner.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var color = i switch
        {
            <= 6 => "cyan1",
            <= 8 => "cyan2", 
            _ => "cyan3"
        };
        AnsiConsole.MarkupLine($"[{color}]{lines[i]}[/]");
    }
    
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Type 'pks --help' to get started with your agentic development journey![/]");
    AnsiConsole.WriteLine();
}
 