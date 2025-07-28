using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Attributes;
using PKS.Commands.Hooks;
using PKS.CLI.Commands.Mcp;
using PKS.CLI.Commands;
using PKS.Commands.Agent;
using PKS.Commands.Prd;
using PKS.CLI.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text;
using System.Reflection;

// Set UTF-8 encoding for proper ASCII art display
Console.OutputEncoding = Encoding.UTF8;

// Check if we should skip banner output
var commandArgs = Environment.GetCommandLineArgs();

// Skip banner for MCP stdio transport
var isMcpStdio = commandArgs.Length > 2 &&
                 commandArgs.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase)) &&
                 (commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) &&
                  Array.IndexOf(commandArgs, a) + 1 < commandArgs.Length &&
                  commandArgs[Array.IndexOf(commandArgs, a) + 1].Equals("stdio", StringComparison.OrdinalIgnoreCase)) ||
                  !commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)));

// Skip banner for hooks commands (Claude Code compatibility)
var isHooksCommand = commandArgs.Length > 1 &&
                     commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase);

// Skip banner for hooks commands with --json flag OR when it's a hook event command
var hasJsonFlag = commandArgs.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-j", StringComparison.OrdinalIgnoreCase));

var isHookEventCommand = commandArgs.Length > 2 &&
                        commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase) &&
                        new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop" }
                            .Contains(commandArgs[2], StringComparer.OrdinalIgnoreCase);

// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();

    // Check if we should display the first-time warning
    DisplayFirstTimeWarningIfNeeded(commandArgs).GetAwaiter().GetResult();
}

// Configure the application
var services = new ServiceCollection();

// Register logging - suppress console logging for MCP stdio transport
if (isMcpStdio)
{
    // For MCP stdio transport, only log to memory or file to avoid contaminating stdout
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Error));
}
else
{
    services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
}

// Register comprehensive logging system
services.AddSingleton<PKS.Infrastructure.Services.Logging.ICommandTelemetryService, PKS.Infrastructure.Services.Logging.CommandTelemetryService>();
services.AddSingleton<PKS.Infrastructure.Services.Logging.IUserInteractionService, PKS.Infrastructure.Services.Logging.UserInteractionService>();
services.AddSingleton<PKS.Infrastructure.Services.Logging.ISessionDataService, PKS.Infrastructure.Services.Logging.SessionDataService>();
services.AddSingleton<PKS.Infrastructure.Services.Logging.ILoggingOrchestrator, PKS.Infrastructure.Services.Logging.LoggingOrchestrator>();
services.AddSingleton<PKS.Infrastructure.Services.Logging.ICommandLoggingWrapper, PKS.Infrastructure.Services.Logging.CommandLoggingWrapper>();

// Register infrastructure services
services.AddSingleton<IKubernetesService, KubernetesService>();
services.AddSingleton<IConfigurationService, ConfigurationService>();
services.AddSingleton<IDeploymentService, DeploymentService>();
services.AddSingleton<IHooksService, HooksService>();
services.AddSingleton<IFirstTimeWarningService, FirstTimeWarningService>();
// Legacy MCP services removed in Phase 3 - now using SDK-based services only

// New SDK-based MCP hosting services
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.McpToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.McpResourceService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.IMcpHostingService, PKS.CLI.Infrastructure.Services.MCP.McpHostingService>();

// MCP configuration
services.Configure<PKS.CLI.Infrastructure.Services.MCP.McpConfiguration>(config =>
{
    config.UseSdkHosting = true; // Enable SDK-based hosting by default
    config.DefaultTransport = "stdio";
    config.DefaultPort = 3000;
    config.EnableDebugLogging = false;
    config.EnableAutoToolDiscovery = true;
    config.EnableAutoResourceDiscovery = true;
});

// Register MCP tool services for SDK-based hosting
// These services contain the actual tool implementations marked with [McpServerTool] attributes
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.ProjectToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.AgentToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.DeploymentToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.StatusToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.SwarmToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.UtilityToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.DevcontainerToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.HooksToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.PrdToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.McpManagementToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.GitHubToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.TemplateToolService>();
services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.Tools.ReportToolService>();

// Configure tool service registration after container is built
services.PostConfigure<PKS.CLI.Infrastructure.Services.MCP.McpConfiguration>(_ =>
{
    // This will be handled by the hosting service when it initializes
});
services.AddSingleton<IAgentFrameworkService, AgentFrameworkService>();
services.AddSingleton<IPrdService, PrdService>();

// Register devcontainer services
services.AddSingleton<IDevcontainerService, DevcontainerService>();
services.AddSingleton<IDevcontainerFeatureRegistry, DevcontainerFeatureRegistry>();
services.AddSingleton<IDevcontainerTemplateService, DevcontainerTemplateService>();
services.AddSingleton<IDevcontainerFileGenerator, DevcontainerFileGenerator>();
services.AddSingleton<IVsCodeExtensionService, VsCodeExtensionService>();
services.AddHttpClient<INuGetTemplateDiscoveryService, NuGetTemplateDiscoveryService>();

// Register PRD branch command
services.AddTransient<PrdBranchCommand>();

// Configure GitHub authentication
services.AddSingleton(serviceProvider => new PKS.Infrastructure.Services.Models.GitHubAuthConfig
{
    ClientId = "Ov23li6wdCCwQfTdFrTJ", // PKS CLI GitHub App client ID
    DefaultScopes = new[] { "repo", "user:email", "write:packages" },
    DeviceCodeUrl = "https://github.com/login/device/code",
    TokenUrl = "https://github.com/login/oauth/access_token",
    ApiBaseUrl = "https://api.github.com",
    UserAgent = "PKS-CLI/1.0.0 (+https://github.com/pksorensen/pks-cli)",
    PollingIntervalSeconds = 5,
    MaxPollingAttempts = 120
});

// Configure GitHub retry policy  
services.AddSingleton(serviceProvider => new PKS.Infrastructure.Services.Models.GitHubRetryPolicy
{
    MaxRetries = 3,
    BaseDelay = TimeSpan.FromSeconds(1),
    MaxDelay = TimeSpan.FromSeconds(30),
    BackoffMultiplier = 2.0
});

// Register GitHub and Project Identity services
services.AddHttpClient<IGitHubService, GitHubService>();
services.AddSingleton<IProjectIdentityService, ProjectIdentityService>();

// Register GitHub Authentication service with HttpClient
services.AddHttpClient<IGitHubAuthenticationService, GitHubAuthenticationService>();

// Register GitHub API client and related services
services.AddHttpClient<IGitHubApiClient, GitHubApiClient>();
services.AddSingleton<IGitHubIssuesService, GitHubIssuesService>();

// Register enhanced GitHub service that integrates authentication
// Note: Using the existing GitHubService registration for IGitHubService
// EnhancedGitHubService implements IGitHubService but needs the full dependency chain
services.AddSingleton<EnhancedGitHubService>();

// Register System Information service
services.AddSingleton<ISystemInformationService, SystemInformationService>();

// Register Template Packaging service
services.AddSingleton<ITemplatePackagingService, TemplatePackagingService>();

// Register Report services
services.AddSingleton<IReportService, ReportService>();
services.AddSingleton<ITelemetryService, TelemetryService>();

// Register individual initializers as transient services
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DevcontainerInitializer>();
services.AddTransient<PKS.Infrastructure.Initializers.Implementations.GitHubIntegrationInitializer>();
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
    registry.Register<PKS.Infrastructure.Initializers.Implementations.DevcontainerInitializer>();
    registry.Register<PKS.Infrastructure.Initializers.Implementations.GitHubIntegrationInitializer>();
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

    config.AddCommand<PKS.Commands.Agent.AgentCommand>("agent")
        .WithDescription("Manage AI agents for development automation");

    config.AddCommand<AsciiCommand>("ascii")
        .WithDescription("Generate beautiful ASCII art for your projects");

    config.AddCommand<PKS.Commands.ReportCommand>("report")
        .WithDescription("Create GitHub issues with system information and user feedback")
        .WithExample(new[] { "report", "Found a bug in the init command" })
        .WithExample(new[] { "report", "--bug", "Application crashes when saving" })
        .WithExample(new[] { "report", "--feature", "Add dark mode support" })
        .WithExample(new[] { "report", "--dry-run", "Preview what the report looks like" });

    config.AddCommand<PKS.Commands.LoggingExampleCommand>("logging-demo")
        .WithDescription("Demonstrate the comprehensive logging system capabilities")
        .WithExample(new[] { "logging-demo" })
        .WithExample(new[] { "logging-demo", "--verbose", "--interactive" })
        .WithExample(new[] { "logging-demo", "--simulate-error", "--delay", "2000" });

    config.AddCommand<PKS.CLI.Commands.Mcp.McpCommand>("mcp")
        .WithDescription("Run Model Context Protocol (MCP) server for AI integration")
        .WithExample(new[] { "mcp" })
        .WithExample(new[] { "mcp", "--transport", "stdio" })
        .WithExample(new[] { "mcp", "--transport", "http", "--port", "3000" });

    config.AddCommand<TestSwarmCommand>("test-swarm")
        .WithDescription("Test swarm MCP tools functionality")
        .WithExample(new[] { "test-swarm" })
        .WithExample(new[] { "test-swarm", "--execute" });

    // Add devcontainer branch command with subcommands
    config.AddBranch<PKS.Commands.Devcontainer.DevcontainerSettings>("devcontainer", devcontainer =>
    {
        devcontainer.SetDescription("Manage devcontainer configurations for isolated development environments");

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerInitCommand>("init")
            .WithDescription("Initialize a new devcontainer configuration")
            .WithExample(new[] { "devcontainer", "init", "MyProject" })
            .WithExample(new[] { "devcontainer", "init", "--features", "dotnet,docker-in-docker" })
            .WithExample(new[] { "devcontainer", "init", "--template", "dotnet-web", "--force" });

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerWizardCommand>("wizard")
            .WithDescription("Interactive wizard for comprehensive devcontainer setup")
            .WithExample(new[] { "devcontainer", "wizard" })
            .WithExample(new[] { "devcontainer", "wizard", "--expert-mode" })
            .WithExample(new[] { "devcontainer", "wizard", "--quick-setup" })
            .WithExample(new[] { "devcontainer", "wizard", "--from-templates" })
            .WithExample(new[] { "devcontainer", "wizard", "--from-templates", "--sources", "https://api.nuget.org/v3/index.json" })
            .WithExample(new[] { "devcontainer", "wizard", "--from-templates", "--add-sources", "https://custom-feed.example.com/v3/index.json" });

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerValidateCommand>("validate")
            .WithDescription("Validate existing devcontainer configuration")
            .WithExample(new[] { "devcontainer", "validate" })
            .WithExample(new[] { "devcontainer", "validate", ".devcontainer/devcontainer.json", "--strict" })
            .WithExample(new[] { "devcontainer", "validate", "--check-features", "--check-extensions" });

        devcontainer.AddBranch<PKS.Commands.Devcontainer.DevcontainerListSettings>("list", list =>
        {
            list.SetDescription("List available devcontainer resources");

            list.AddCommand<PKS.Commands.Devcontainer.DevcontainerListCommand>("features")
                .WithDescription("List available devcontainer features")
                .WithExample(new[] { "devcontainer", "list", "features" })
                .WithExample(new[] { "devcontainer", "list", "features", "--category", "language" })
                .WithExample(new[] { "devcontainer", "list", "features", "--search", "dotnet", "--show-details" });

            list.AddCommand<PKS.Commands.Devcontainer.DevcontainerListCommand>("templates")
                .WithDescription("List available devcontainer templates")
                .WithExample(new[] { "devcontainer", "list", "templates" })
                .WithExample(new[] { "devcontainer", "list", "templates", "--category", "web" })
                .WithExample(new[] { "devcontainer", "list", "templates", "--show-details" });

            list.AddCommand<PKS.Commands.Devcontainer.DevcontainerListCommand>("extensions")
                .WithDescription("List recommended VS Code extensions")
                .WithExample(new[] { "devcontainer", "list", "extensions" })
                .WithExample(new[] { "devcontainer", "list", "extensions", "--category", "language" })
                .WithExample(new[] { "devcontainer", "list", "extensions", "--search", "dotnet" });

            list.AddCommand<PKS.Commands.Devcontainer.DevcontainerListCommand>("")
                .WithDescription("List all available devcontainer resources")
                .WithExample(new[] { "devcontainer", "list" });
        });
    });

    // Add hooks branch command with subcommands
    config.AddBranch<HooksSettings>("hooks", hooks =>
    {
        hooks.SetDescription("Manage Claude Code hooks integration");

        hooks.AddCommand<HooksCommand>("init")
            .WithDescription("Initialize Claude Code hooks configuration");

        hooks.AddCommand<HooksCommand>("list")
            .WithDescription("List available hook events");

        hooks.AddCommand<PreToolUseCommand>("pre-tool-use")
            .WithDescription("Handle PreToolUse hook event from Claude Code");

        hooks.AddCommand<PostToolUseCommand>("post-tool-use")
            .WithDescription("Handle PostToolUse hook event from Claude Code");

        hooks.AddCommand<UserPromptSubmitCommand>("user-prompt-submit")
            .WithDescription("Handle UserPromptSubmit hook event from Claude Code");

        hooks.AddCommand<StopCommand>("stop")
            .WithDescription("Handle Stop hook event from Claude Code");

        hooks.AddCommand<NotificationCommand>("notification")
            .WithDescription("Handle Notification hook event from Claude Code");

        hooks.AddCommand<SubagentStopCommand>("subagent-stop")
            .WithDescription("Handle SubagentStop hook event from Claude Code");

        hooks.AddCommand<PreCompactCommand>("pre-compact")
            .WithDescription("Handle PreCompact hook event from Claude Code");
    });

    // Add PRD branch command with subcommands
    config.AddBranch<PrdSettings>("prd", prd =>
    {
        prd.SetDescription("Manage Product Requirements Documents (PRDs) with AI-powered generation");

        prd.AddCommand<PrdGenerateCommand>("generate")
            .WithDescription("Generate comprehensive PRD from idea description")
            .WithExample(new[] { "prd", "generate", "A mobile app for task management" });

        prd.AddCommand<PrdLoadCommand>("load")
            .WithDescription("Load and parse existing PRD file")
            .WithExample(new[] { "prd", "load", "docs/PRD.md" });

        prd.AddCommand<PrdRequirementsCommand>("requirements")
            .WithDescription("List and filter requirements from PRD")
            .WithExample(new[] { "prd", "requirements", "--status", "pending" });

        prd.AddCommand<PrdStatusCommand>("status")
            .WithDescription("Display PRD status, progress, and statistics")
            .WithExample(new[] { "prd", "status" });

        prd.AddCommand<PrdValidateCommand>("validate")
            .WithDescription("Validate PRD for completeness and consistency")
            .WithExample(new[] { "prd", "validate", "--strict" });

        prd.AddCommand<PrdTemplateCommand>("template")
            .WithDescription("Generate PRD templates for different project types")
            .WithExample(new[] { "prd", "template", "MyProject", "--type", "web" });
    });
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
    
    🤖 Poul's Killer Swarms
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

static async Task DisplayFirstTimeWarningIfNeeded(string[] commandArgs)
{
    try
    {
        // Create a temporary configuration service to check the warning acknowledgment
        var configService = new ConfigurationService();

        // Check if the warning has already been acknowledged
        if (await configService.IsFirstTimeWarningAcknowledgedAsync())
        {
            return; // User has already acknowledged the warning
        }

        // Check if the current command should skip the warning
        if (ShouldSkipFirstTimeWarning(commandArgs))
        {
            return;
        }

        // Display the first-time warning
        var warningPanel = new Panel(
            """
            [yellow]⚠️  IMPORTANT DISCLAIMER ⚠️[/]

            This CLI tool is powered by AI and generates code automatically.
            The generated code has [red]NOT[/] been validated by humans.

            [red]AI may make mistakes - use at your own risk.[/]

            Please review all generated code before use and report any issues at:
            [cyan]https://github.com/pksorensen/pks-cli[/]
            """)
            .Header("[red bold]First-Time Usage Warning[/]")
            .BorderColor(Color.Red)
            .Padding(1, 1, 1, 1);

        AnsiConsole.Write(warningPanel);
        AnsiConsole.WriteLine();

        // Ask for user acknowledgment
        var acknowledged = AnsiConsole.Confirm("Do you acknowledge and accept these terms?", false);

        if (!acknowledged)
        {
            AnsiConsole.MarkupLine("[red]You must acknowledge the terms to use PKS CLI.[/]");
            Environment.Exit(1);
        }

        // Save the acknowledgment
        await configService.SetFirstTimeWarningAcknowledgedAsync();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Thank you for acknowledging the terms. Continuing with PKS CLI...[/]");
        AnsiConsole.WriteLine();
    }
    catch
    {
        // Gracefully handle any errors - continue without warning if configuration fails
        // This ensures the CLI remains functional even if file system operations fail
    }
}

static bool ShouldSkipFirstTimeWarning(string[] commandArgs)
{
    try
    {
        // Skip for MCP stdio transport
        var isMcpStdio = commandArgs.Length > 2 &&
                         commandArgs.Any(a => a.Equals("mcp", StringComparison.OrdinalIgnoreCase)) &&
                         (commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) &&
                          Array.IndexOf(commandArgs, a) + 1 < commandArgs.Length &&
                          commandArgs[Array.IndexOf(commandArgs, a) + 1].Equals("stdio", StringComparison.OrdinalIgnoreCase)) ||
                          !commandArgs.Any(a => a.Equals("--transport", StringComparison.OrdinalIgnoreCase) || a.Equals("-t", StringComparison.OrdinalIgnoreCase)));

        if (isMcpStdio)
        {
            return true;
        }

        // Skip for hooks commands with --json flag OR when it's a hook event command
        var isHooksCommand = commandArgs.Length > 1 &&
                             commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase);

        var hasJsonFlag = commandArgs.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                                              a.Equals("-j", StringComparison.OrdinalIgnoreCase));

        var isHookEventCommand = commandArgs.Length > 2 &&
                                commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase) &&
                                new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop" }
                                    .Contains(commandArgs[2], StringComparer.OrdinalIgnoreCase);

        if (isHooksCommand && (hasJsonFlag || isHookEventCommand))
        {
            return true;
        }

        // TODO: Check for SkipFirstTimeWarning attribute on command classes
        // This requires reflection to get the command type from the command name
        // and check if it has the [SkipFirstTimeWarning] attribute
        // Implementation would be added here once command attribute detection is needed

        return false;
    }
    catch
    {
        // If anything fails, err on the side of not skipping the warning
        return false;
    }
}
