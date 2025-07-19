using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Services;
using PKS.Commands.Hooks;
using PKS.CLI.Commands.Mcp;
using PKS.Commands.Agent;
using PKS.Commands.Prd;
using PKS.CLI.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;

// Set UTF-8 encoding for proper ASCII art display
Console.OutputEncoding = Encoding.UTF8;

// Display welcome banner with fancy ASCII art
DisplayWelcomeBanner();

// Configure the application
var services = new ServiceCollection();

// Register logging
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));

// Register infrastructure services
services.AddSingleton<IKubernetesService, KubernetesService>();
services.AddSingleton<IConfigurationService, ConfigurationService>();
services.AddSingleton<IDeploymentService, DeploymentService>();
services.AddSingleton<IHooksService, HooksService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.IMcpServerService, PKS.CLI.Infrastructure.Services.McpServerService>();
services.AddSingleton<IAgentFrameworkService, AgentFrameworkService>();
services.AddSingleton<IPrdService, PrdService>();

// Register GitHub and Project Identity services
services.AddHttpClient<IGitHubService, GitHubService>();
services.AddSingleton<IProjectIdentityService, ProjectIdentityService>();

// Register individual initializers as transient services
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.GitHubIntegrationInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.AgenticFeaturesInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ReadmeInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ClaudeDocumentationInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.McpConfigurationInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.HooksInitializer>();

// Register initializer system with configured registry
services.AddSingleton<IInitializerRegistry>(serviceProvider =>
{
    var registry = new InitializerRegistry(serviceProvider);
    
    // Register initializer types
    registry.Register<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.GitHubIntegrationInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.AgenticFeaturesInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.ReadmeInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.ClaudeDocumentationInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.McpConfigurationInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.HooksInitializer>();
    
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
        
    config.AddCommand<PKS.Commands.Agent.AgentCommand>("agent")
        .WithDescription("Manage AI agents for development automation");
        
    config.AddCommand<AsciiCommand>("ascii")
        .WithDescription("Generate beautiful ASCII art for your projects");
        
    config.AddCommand<PKS.CLI.Commands.Mcp.McpCommand>("mcp")
        .WithDescription("Model Context Protocol (MCP) server for AI integration");
        
    config.AddCommand<HooksCommand>("hooks")
        .WithDescription("Manage Claude Code hooks with smart dispatcher pattern");
        
    // Add PRD commands
    config.AddCommand<PrdCommand>("prd")
        .WithDescription("Manage Product Requirements Documents (PRDs) with AI-powered generation");
        
    config.AddCommand<PrdGenerateCommand>("prd-generate")
        .WithDescription("Generate comprehensive PRD from idea description");
        
    config.AddCommand<PrdLoadCommand>("prd-load")
        .WithDescription("Load and parse existing PRD file");
        
    config.AddCommand<PrdRequirementsCommand>("prd-requirements")
        .WithDescription("List and filter requirements from PRD");
        
    config.AddCommand<PrdStatusCommand>("prd-status")
        .WithDescription("Display PRD status, progress, and statistics");
        
    config.AddCommand<PrdValidateCommand>("prd-validate")
        .WithDescription("Validate PRD for completeness and consistency");
        
    config.AddCommand<PrdTemplateCommand>("prd-template")
        .WithDescription("Generate PRD templates for different project types");
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
 