using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Attributes;
using PKS.Commands;
using PKS.Commands.Hooks;
using PKS.CLI.Commands.Mcp;
using PKS.CLI.Commands;
using PKS.Commands.Agent;
using PKS.Commands.Prd;
using PKS.CLI.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using PKS.Commands.GitHub.Runner;
using PKS.Commands.Agentics;
using PKS.Commands.Agentics.Runner;
using PKS.Commands.Agentics.Tasks;
using PKS.Commands.Firecracker;
using PKS.Commands.Firecracker.Runner;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Commands.Ado;
using PKS.Commands.Foundry;
using PKS.Commands.FileShares;
using PKS.Commands.Storage;
using PKS.Commands.Jira;
using PKS.Commands.Confluence;
using PKS.Commands.Registry;
using PKS.Commands.Google;
using PKS.Commands.AppInsights;
using PKS.Commands.Otel;
using PKS.Commands.Image;
using PKS.Commands.Promptwall;
using PKS.Commands.Tts;
using PKS.Commands.Voice;
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

// Skip banner for git askpass (GIT_ASKPASS must have zero extra output)
var isGitAskPass = commandArgs.Length > 2 &&
                   commandArgs[1].Equals("git", StringComparison.OrdinalIgnoreCase) &&
                   commandArgs[2].Equals("askpass", StringComparison.OrdinalIgnoreCase);

// Skip banner for hooks commands (Claude Code compatibility)
var isHooksCommand = commandArgs.Length > 1 &&
                     commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase);

// Skip banner for foundry proxy (eval-capture mode — stdout must only contain env var lines)
var isFoundryProxy = commandArgs.Length > 2 &&
                     commandArgs[1].Equals("foundry", StringComparison.OrdinalIgnoreCase) &&
                     commandArgs[2].Equals("proxy", StringComparison.OrdinalIgnoreCase);

// Skip banner for ado git-proxy (background daemon — clean stdout)
var isAdoGitProxy = commandArgs.Length > 2 &&
                    commandArgs[1].Equals("ado", StringComparison.OrdinalIgnoreCase) &&
                    commandArgs[2].Equals("git-proxy", StringComparison.OrdinalIgnoreCase);

// Skip banner for hooks commands with --json flag OR when it's a hook event command
var hasJsonFlag = commandArgs.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-j", StringComparison.OrdinalIgnoreCase));

var isHookEventCommand = commandArgs.Length > 2 &&
                        commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase) &&
                        new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop" }
                            .Contains(commandArgs[2], StringComparer.OrdinalIgnoreCase);

// Skip banner when --no-logo flag is passed
var noLogo = commandArgs.Any(a => a.Equals("--no-logo", StringComparison.OrdinalIgnoreCase));

// Enable debug output when --debug flag is passed
if (commandArgs.Any(a => a.Equals("--debug", StringComparison.OrdinalIgnoreCase)))
    Environment.SetEnvironmentVariable("PKS_DEBUG", "1");

// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !isGitAskPass && !isFoundryProxy && !isAdoGitProxy && !noLogo && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
{
    DisplayWelcomeBanner();

    // Check if we should display the first-time warning
    DisplayFirstTimeWarningIfNeeded(commandArgs).GetAwaiter().GetResult();
}

// Configure the application
var services = new ServiceCollection();

// Register logging - suppress console logging for MCP stdio transport and foundry proxy
if (isMcpStdio || isFoundryProxy)
{
    // For MCP stdio and foundry proxy, suppress console output to keep stdout clean
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
services.AddSingleton<PKS.Infrastructure.Services.ISshTargetConfigurationService, PKS.Infrastructure.Services.SshTargetConfigurationService>();
services.AddSingleton<PKS.Infrastructure.Services.ISshCommandRunner, PKS.Infrastructure.Services.SshCommandRunner>();
services.AddSingleton<PKS.Infrastructure.Services.IRsyncTargetConfigurationService, PKS.Infrastructure.Services.RsyncTargetConfigurationService>();

// Model registry (local AI models — Parakeet, etc.)
services.AddSingleton<PKS.Infrastructure.Services.IModelRegistryService, PKS.Infrastructure.Services.ModelRegistryService>();
services.AddHttpClient<PKS.Infrastructure.Services.IModelDownloadService, PKS.Infrastructure.Services.ModelDownloadService>();

// Claude marketplace services
services.AddSingleton<PKS.Infrastructure.Services.Claude.IClaudeMarketplaceConfigurationService, PKS.Infrastructure.Services.Claude.ClaudeMarketplaceConfigurationService>();
services.AddSingleton<PKS.Infrastructure.Services.Claude.IClaudeManagedSettingsRenderer, PKS.Infrastructure.Services.Claude.ClaudeManagedSettingsRenderer>();
services.AddSingleton<PKS.Infrastructure.Services.Claude.IClaudeMarketplaceFetcher, PKS.Infrastructure.Services.Claude.ClaudeMarketplaceFetcher>();

// Brain services — ingest, extract, and synthesize Claude session history
// See /home/node/.claude/plans/atomic-mixing-allen.md for the multi-phase design.
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainPathResolver, PKS.Infrastructure.Services.Brain.BrainPathResolver>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainIndexStore, PKS.Infrastructure.Services.Brain.BrainIndexStore>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.ISessionDiscoveryService, PKS.Infrastructure.Services.Brain.SessionDiscoveryService>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.ISessionParser, PKS.Infrastructure.Services.Brain.SessionParser>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IPricingService, PKS.Infrastructure.Services.Brain.PricingService>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IPlanFileIndexer, PKS.Infrastructure.Services.Brain.PlanFileIndexer>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainIngestPipeline, PKS.Infrastructure.Services.Brain.BrainIngestPipeline>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainSkillReader, PKS.Infrastructure.Services.Brain.BrainSkillReader>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainExtractContextBuilder, PKS.Infrastructure.Services.Brain.BrainExtractContextBuilder>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IClaudeRunner, PKS.Infrastructure.Services.Brain.ClaudeRunner>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainExtractPipeline, PKS.Infrastructure.Services.Brain.BrainExtractPipeline>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IExtractReader, PKS.Infrastructure.Services.Brain.ExtractReader>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainSynthesisPipeline, PKS.Infrastructure.Services.Brain.BrainSynthesisPipeline>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainWikiPipeline, PKS.Infrastructure.Services.Brain.BrainWikiPipeline>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainSkillCatalog, PKS.Infrastructure.Services.Brain.BrainSkillCatalog>();
services.AddSingleton<PKS.Infrastructure.Services.Brain.IBrainAdrPipeline, PKS.Infrastructure.Services.Brain.BrainAdrPipeline>();
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

// Register devcontainer spawner service
services.AddSingleton<IDevcontainerSpawnerService, DevcontainerSpawnerService>();
services.AddSingleton<IConfigurationHashService, ConfigurationHashService>();

// Register Docker client (Docker.DotNet)
services.AddSingleton<Docker.DotNet.IDockerClient>(sp =>
{
    var config = new Docker.DotNet.DockerClientConfiguration();
    return config.CreateClient();
});

services.AddHttpClient<INuGetTemplateDiscoveryService, NuGetTemplateDiscoveryService>();
services.AddSingleton<PKS.Infrastructure.Services.Templates.IDevcontainerTemplateRendererService, PKS.Infrastructure.Services.Templates.DevcontainerTemplateRendererService>();

// Register PRD branch command
services.AddTransient<PrdBranchCommand>();

// Configure GitHub authentication
services.AddSingleton(serviceProvider => new PKS.Infrastructure.Services.Models.GitHubAuthConfig
{
    ClientId = "Iv23liFv43zosMUb8t9y", // Agentics Live GitHub App (si14agents org)
    AppSlug = "agentics-live",
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

// Configure Azure DevOps authentication
services.AddSingleton<PKS.Infrastructure.Services.Models.AzureDevOpsAuthConfig>();
services.AddHttpClient<IAzureDevOpsAuthService, AzureDevOpsAuthService>();

// Configure Azure AI Foundry authentication
services.AddSingleton<PKS.Infrastructure.Services.Models.AzureFoundryAuthConfig>();
services.AddHttpClient<IAzureFoundryAuthService, AzureFoundryAuthService>();

// Generic Azure authentication
services.AddSingleton<PKS.Infrastructure.Services.Models.AzureAuthConfig>();
services.AddHttpClient<PKS.Infrastructure.Services.IAzureAuthService, PKS.Infrastructure.Services.AzureAuthService>();

// Azure VM provisioning
services.AddHttpClient<PKS.Infrastructure.Services.IAzureVmService, PKS.Infrastructure.Services.AzureVmService>();

// Azure billing + Cost Management (used by `pks azure usage` and `pks foundry usage`)
services.AddHttpClient<PKS.Infrastructure.Services.IAzureBillingService, PKS.Infrastructure.Services.AzureBillingService>();
services.AddSingleton<PKS.Infrastructure.Services.IAzureVmMetadataService, PKS.Infrastructure.Services.AzureVmMetadataService>();
services.AddSingleton<PKS.Infrastructure.Services.ISshExecutor, PKS.Infrastructure.Services.SshExecutor>();

// Configure Azure File Share provider
services.AddSingleton<PKS.Infrastructure.Services.Models.AzureFileShareAuthConfig>();
services.AddHttpClient<AzureFileShareProvider>();
services.AddSingleton<IFileShareProvider>(sp => sp.GetRequiredService<AzureFileShareProvider>());
services.AddSingleton<FileShareProviderRegistry>();

// Configure Jira integration
services.AddSingleton<PKS.Infrastructure.Services.Models.JiraAuthConfig>();
services.AddHttpClient<IJiraService, JiraService>();

// Configure Confluence integration (reuses Jira auth)
services.AddSingleton<IConfluenceMarkdownConverter, ConfluenceMarkdownConverter>();
services.AddHttpClient<IConfluenceService, ConfluenceService>();

// Register GitHub and Project Identity services
services.AddHttpClient<IGitHubService, GitHubService>();
services.AddSingleton<IProjectIdentityService, ProjectIdentityService>();

// Register GitHub Authentication service with HttpClient
services.AddHttpClient<IGitHubAuthenticationService, GitHubAuthenticationService>();

// Configure Microsoft Graph authentication
services.AddSingleton<PKS.Infrastructure.Services.Models.MsGraphAuthConfig>();
services.AddHttpClient<IMsGraphAuthenticationService, MsGraphAuthenticationService>();
services.AddHttpClient<IMsGraphEmailService, MsGraphEmailService>();
services.AddSingleton<IMsGraphEmailExportService, MsGraphEmailExportService>();

// Register GitHub API client and related services
services.AddHttpClient<IGitHubApiClient, GitHubApiClient>();
services.AddSingleton<IGitHubIssuesService, GitHubIssuesService>();

// Register enhanced GitHub service that integrates authentication
// Note: Using the existing GitHubService registration for IGitHubService
// EnhancedGitHubService implements IGitHubService but needs the full dependency chain
services.AddSingleton<EnhancedGitHubService>();

// Register GitHub Runner services
services.AddSingleton<IRegistryConfigurationService, RegistryConfigurationService>();
services.AddSingleton<ICoolifyConfigurationService, CoolifyConfigurationService>();
services.AddSingleton<ICoolifyLookupService, CoolifyLookupService>();
services.AddSingleton<ICoolifyApiService, CoolifyApiService>();
services.AddSingleton<IRunnerConfigurationService, RunnerConfigurationService>();
services.AddSingleton<IAgenticsRunnerConfigurationService, AgenticsRunnerConfigurationService>();
services.AddSingleton<PKS.Infrastructure.Services.Agentics.IAgenticsAuthService, PKS.Infrastructure.Services.Agentics.AgenticsAuthService>();
services.AddSingleton<PKS.Infrastructure.Services.Agentics.IAgenticsAuthConfigurationService, PKS.Infrastructure.Services.Agentics.AgenticsAuthConfigurationService>();
services.AddSingleton<IFirecrackerRunnerConfigurationService, FirecrackerRunnerConfigurationService>();
services.AddSingleton<IFirecrackerService, FirecrackerService>();
services.AddSingleton<FirecrackerNetworkManager>();
services.AddSingleton<IGitHubActionsService, GitHubActionsService>();
services.AddSingleton<IProcessRunner, ProcessRunner>();
services.AddSingleton<IRunnerContainerService, RunnerContainerService>();
services.AddSingleton<INamedContainerPool, NamedContainerPool>();
services.AddSingleton<IRunnerDaemonService, RunnerDaemonService>();
services.AddSingleton<IJobTokenService, JobTokenService>();
services.AddSingleton<ICoolifyTokenStore, CoolifyTokenStore>();

// Register System Information service
services.AddSingleton<ISystemInformationService, SystemInformationService>();

// Register Template Packaging service
services.AddSingleton<ITemplatePackagingService, TemplatePackagingService>();

// Register Application Insights services
services.AddSingleton<IAppInsightsConfigService, AppInsightsConfigService>();
services.AddHttpClient<IAppInsightsHttpAdapter, DefaultAppInsightsHttpAdapter>();
services.AddSingleton<IAppInsightsQueryService, AppInsightsQueryService>();

// Register Google AI service
services.AddHttpClient<IGoogleAiService, GoogleAiService>();

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
    config.SetApplicationVersion(GetVersion());

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

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerSpawnCommand>("spawn")
            .WithDescription("Spawn a devcontainer in a Docker volume for development")
            .WithExample(new[] { "devcontainer", "spawn" })
            .WithExample(new[] { "devcontainer", "spawn", "/path/to/project" })
            .WithExample(new[] { "devcontainer", "spawn", "--volume-name", "my-project-vol" })
            .WithExample(new[] { "devcontainer", "spawn", "--no-launch-vscode" })
            .WithExample(new[] { "devcontainer", "spawn", "--force" });

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerContainersCommand>("list")
            .WithDescription("List running devcontainers (local or remote)")
            .WithExample(new[] { "devcontainer", "list" })
            .WithExample(new[] { "devcontainer", "list", "--all" });

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerConnectCommand>("connect")
            .WithDescription("Connect to an existing devcontainer via VS Code")
            .WithExample(new[] { "devcontainer", "connect" })
            .WithExample(new[] { "devcontainer", "connect", "<container-id>" })
            .WithExample(new[] { "devcontainer", "connect", "--no-launch-vscode" });

        devcontainer.AddCommand<PKS.Commands.Devcontainer.DevcontainerDestroyCommand>("destroy")
            .WithDescription("Destroy a devcontainer and its associated volumes")
            .WithExample(new[] { "devcontainer", "destroy" })
            .WithExample(new[] { "devcontainer", "destroy", "<container-id>" })
            .WithExample(new[] { "devcontainer", "destroy", "--force" });
    });

    // Add agentics branch command with runner subcommands
    config.AddBranch<AgenticsSettings>("agentics", agentics =>
    {
        agentics.SetDescription("Manage Agentics runners and integration");

        agentics.AddCommand<PKS.Commands.Agentics.AgenticsInitCommand>("init")
            .WithDescription("Log in to agentics.dk via Keycloak device-code flow (one-time per machine)")
            .WithExample(new[] { "agentics", "init" })
            .WithExample(new[] { "agentics", "init", "--server", "agentics.dk" });

        agentics.AddBranch<AgenticsRunnerSettings>("runner", runner =>
        {
            runner.SetDescription("Manage Agentics self-hosted runners");

            runner.AddCommand<AgenticsRunnerRegisterCommand>("register")
                .WithDescription("Register a runner for an owner/project")
                .WithExample(new[] { "agentics", "runner", "register", "myorg/myproject" })
                .WithExample(new[] { "agentics", "runner", "register", "myorg/myproject", "--name", "my-runner" })
                .WithExample(new[] { "agentics", "runner", "register", "myorg/myproject", "--server", "localhost:3000" });

            runner.AddCommand<AgenticsRunnerStartCommand>("start")
                .WithDescription("Start the runner daemon to poll for and execute jobs")
                .WithExample(new[] { "agentics", "runner", "start" });

            runner.AddCommand<AgenticsRunnerCleanupCommand>("cleanup")
                .WithDescription("Remove devcontainers from previous runner instances (see ADR 0002)")
                .WithExample(new[] { "agentics", "runner", "cleanup" })
                .WithExample(new[] { "agentics", "runner", "cleanup", "--dry-run" })
                .WithExample(new[] { "agentics", "runner", "cleanup", "--all" });
        });

        agentics.AddBranch<AgenticsSettings>("task", task =>
        {
            task.SetDescription("Submit tasks to Assembly Lines");

            task.AddCommand<AgenticsTaskSubmitCommand>("submit")
                .WithDescription("Submit a task to an assembly line (for use in CI/CD pipelines)")
                .WithExample(new[] { "agentics", "task", "submit", "--assembly-line-url", "https://agentics.dk/p/owner/project/assembly-lines/stage-id", "--title", "Fix failing tests" })
                .WithExample(new[] { "agentics", "task", "submit", "--assembly-line-url", "https://agentics.dk/p/owner/project/assembly-lines/stage-id", "--title", "CI Failure", "--description", "Build step failed" });
        });
    });

    config.AddBranch<FirecrackerSettings>("firecracker", firecracker =>
    {
        firecracker.SetDescription("Manage Firecracker microVM runners for isolated job execution");

        firecracker.AddCommand<FirecrackerRunnerInitCommand>("init")
            .WithDescription("Initialize Firecracker runner (download kernel, build rootfs, configure)")
            .WithExample(new[] { "firecracker", "init" })
            .WithExample(new[] { "firecracker", "init", "--vcpus", "4", "--mem-mib", "4096" });

        firecracker.AddCommand<FirecrackerTestCommand>("test")
            .WithDescription("Boot a test VM and run smoke tests to verify Firecracker setup")
            .WithExample(new[] { "firecracker", "test" })
            .WithExample(new[] { "firecracker", "test", "--keep-vm" });

        firecracker.AddBranch<FirecrackerRunnerSettings>("runner", runner =>
        {
            runner.SetDescription("Manage Firecracker runner daemon");

            runner.AddCommand<FirecrackerRunnerStartCommand>("start")
                .WithDescription("Start polling for jobs and execute in Firecracker microVMs")
                .WithExample(new[] { "firecracker", "runner", "start", "--server", "agentics.dk" })
                .WithExample(new[] { "firecracker", "runner", "start", "--project", "owner/project" });
        });
    });

    // Add github branch command with auth + runner subcommands
    config.AddBranch<PKS.Commands.GitHub.GitHubSettings>("github", github =>
    {
        github.SetDescription("Manage GitHub authentication and self-hosted runners");

        github.AddCommand<PKS.Commands.GitHub.GitHubInitCommand>("init")
            .WithDescription("Authenticate with GitHub and grant runner push access to a repo")
            .WithExample(new[] { "github", "init" })
            .WithExample(new[] { "github", "init", "https://github.com/owner/repo" })
            .WithExample(new[] { "github", "init", "--force" });

        github.AddCommand<PKS.Commands.GitHub.GitHubStatusCommand>("status")
            .WithDescription("Show GitHub authentication status and git:push capability")
            .WithExample(new[] { "github", "status" })
            .WithExample(new[] { "github", "status", "--verbose" });

        github.AddBranch<PKS.Commands.GitHub.GitHubSettings>("runner", runner =>
        {
            runner.SetDescription("Manage devcontainer-based GitHub Actions runners");

            runner.AddCommand<RunnerRegisterCommand>("register")
                .WithDescription("Register a repository for devcontainer-based runner")
                .WithExample(new[] { "github", "runner", "register", "owner/repo" })
                .WithExample(new[] { "github", "runner", "register", "owner/repo", "--labels", "custom-label" });

            runner.AddCommand<RunnerUnregisterCommand>("unregister")
                .WithDescription("Unregister a repository from the runner")
                .WithExample(new[] { "github", "runner", "unregister", "owner/repo" });

            runner.AddCommand<RunnerListCommand>("list")
                .WithDescription("List registered repositories")
                .WithExample(new[] { "github", "runner", "list" });

            runner.AddCommand<RunnerStartCommand>("start")
                .WithDescription("Start the runner daemon to process workflow jobs")
                .WithExample(new[] { "github", "runner", "start" })
                .WithExample(new[] { "github", "runner", "start", "owner/repo" });

            runner.AddCommand<RunnerStatusCommand>("status")
                .WithDescription("Show runner daemon status and active jobs")
                .WithExample(new[] { "github", "runner", "status" });

            runner.AddCommand<RunnerStopCommand>("stop")
                .WithDescription("Gracefully stop the runner daemon")
                .WithExample(new[] { "github", "runner", "stop" });

            runner.AddCommand<RunnerPruneCommand>("prune")
                .WithDescription("Remove duplicate registrations, keeping only the most recent per repo")
                .WithExample(new[] { "github", "runner", "prune" });
        });
    });

    // Add coolify branch command
    config.AddBranch<PKS.Commands.Coolify.CoolifySettings>("coolify", coolify =>
    {
        coolify.SetDescription("Manage Coolify deployment integration");

        coolify.AddCommand<PKS.Commands.Coolify.CoolifyRegisterCommand>("register")
            .WithDescription("Register a Coolify instance for auto-deployment")
            .WithExample(new[] { "coolify", "register", "https://projects.si14agents.com" });

        coolify.AddCommand<PKS.Commands.Coolify.CoolifyListCommand>("list")
            .WithDescription("List registered Coolify instances")
            .WithExample(new[] { "coolify", "list" });

        coolify.AddCommand<PKS.Commands.Coolify.CoolifyStatusCommand>("status")
            .WithDescription("Test connectivity and show projects with resource health status")
            .WithExample(new[] { "coolify", "status" });
    });

    // Add SSH remote target management
    config.AddBranch<PKS.Commands.Rsync.RsyncSettings>("rsync", rsync =>
    {
        rsync.SetDescription("Manage rsync backup targets (NAS, remote hosts)");

        rsync.AddCommand<PKS.Commands.Rsync.RsyncInitCommand>("init")
            .WithDescription("Register a new rsync backup target")
            .WithExample(["rsync", "init"]);

        rsync.AddCommand<PKS.Commands.Rsync.RsyncListCommand>("list")
            .WithDescription("List registered rsync targets")
            .WithExample(["rsync", "list"]);

        rsync.AddCommand<PKS.Commands.Rsync.RsyncRemoveCommand>("remove")
            .WithDescription("Remove a registered rsync target")
            .WithExample(["rsync", "remove"]);
    });

    config.AddBranch<PKS.Commands.Tools.ToolsSettings>("tools", tools =>
    {
        tools.SetDescription("Tool registry management");

        tools.AddCommand<PKS.Commands.Tools.ToolsPublishCommand>("publish")
            .WithDescription("Generate and write tools-registry Markdown for commands tagged with [ToolRegistryExport]")
            .WithExample(["tools", "publish"]);
    });

    config.AddBranch<PKS.Commands.Ssh.SshSettings>("ssh", ssh =>
    {
        ssh.SetDescription("Manage SSH remote targets for devcontainer deployment");

        ssh.AddCommand<PKS.Commands.Ssh.SshRegisterCommand>("register")
            .WithDescription("Register an SSH target for remote devcontainer deployment")
            .WithExample(new[] { "ssh", "register", "root@projects.si14agents.com", "-i", "./id_rsa" });

        ssh.AddCommand<PKS.Commands.Ssh.SshListCommand>("list")
            .WithDescription("List registered SSH targets")
            .WithExample(new[] { "ssh", "list" });

        ssh.AddCommand<PKS.Commands.Ssh.SshRemoveCommand>("remove")
            .WithDescription("Remove a registered SSH target")
            .WithExample(new[] { "ssh", "remove", "projects.si14agents.com" });

        ssh.AddCommand<PKS.Commands.Ssh.SshConnectCommand>("connect")
            .WithDescription("Open an interactive SSH session to a registered target")
            .WithExample(new[] { "ssh", "connect", "pks-vm-2e93" });
    });

    config.AddBranch("vibecast", vibecast =>
    {
        vibecast.SetDescription("SSH into a registered target and run npx -y vibecast");
        vibecast.SetDefaultCommand<PKS.Commands.Vibecast.VibecastCommand>();
        vibecast.AddCommand<PKS.Commands.Vibecast.VibecastGameCommand>("game")
            .WithDescription("Join a vibegame tournament match — code your bot and battle")
            .WithExample(new[] { "vibecast", "game", "abc123" });
    });

    // Add registry branch command
    config.AddBranch<RegistrySettings>("registry", registry =>
    {
        registry.SetDescription("Manage container registry credentials on this runner");

        registry.AddCommand<RegistryInitCommand>("init")
            .WithDescription("Register a container registry (persists credentials for CI)")
            .WithExample(new[] { "registry", "init", "registry.kjeldager.io" });

        registry.AddCommand<RegistryStatusCommand>("status")
            .WithDescription("List registered registries and check connections");

        registry.AddCommand<RegistryRemoveCommand>("remove")
            .WithDescription("Remove a registered registry");
    });

    // Add Azure DevOps branch command
    config.AddBranch<AdoSettings>("ado", ado =>
    {
        ado.SetDescription("Manage Azure DevOps authentication");

        ado.AddCommand<AdoInitCommand>("init")
            .WithDescription("Authenticate with Azure DevOps via OAuth2")
            .WithExample(new[] { "ado", "init" })
            .WithExample(new[] { "ado", "init", "--force" });

        ado.AddCommand<AdoStatusCommand>("status")
            .WithDescription("Show Azure DevOps authentication status")
            .WithExample(new[] { "ado", "status" });

        ado.AddCommand<AdoGitProxyCommand>("git-proxy")
            .WithDescription("Start the ADO git HTTP proxy (token-injecting, port 7878)")
            .WithExample(new[] { "ado", "git-proxy", "--allow", "Org/Project/Repo" });
    });

    // Add Jira branch command
    config.AddBranch("jira", jira =>
    {
        jira.SetDescription("Manage Jira integration and browse tickets");

        jira.AddCommand<JiraInitCommand>("init")
            .WithDescription("Initialize Jira authentication (API token or OAuth)")
            .WithExample(new[] { "jira", "init" })
            .WithExample(new[] { "jira", "init", "--force" });

        jira.AddCommand<JiraBrowseCommand>("browse")
            .WithDescription("Browse Jira tickets in an interactive tree view")
            .WithExample(new[] { "jira", "browse" })
            .WithExample(new[] { "jira", "browse", "--project", "PROJ" });

        jira.AddCommand<JiraConfigCommand>("config")
            .WithDescription("View or set Jira field mappings")
            .WithExample(new[] { "jira", "config" })
            .WithExample(new[] { "jira", "config", "--ac-field", "customfield_10064" });
    });

    // Add Confluence branch command
    config.AddBranch("confluence", confluence =>
    {
        confluence.SetDescription("Edit Confluence pages locally in markdown with git tracking");

        confluence.AddCommand<ConfluenceInitCommand>("init")
            .WithDescription("Initialize a Confluence workspace in the current directory")
            .WithExample(new[] { "confluence", "init" })
            .WithExample(new[] { "confluence", "init", "--space", "OptiDyna" });

        confluence.AddCommand<ConfluenceCheckoutCommand>("checkout")
            .WithDescription("Sync Confluence pages to local markdown files")
            .WithExample(new[] { "confluence", "checkout" })
            .WithExample(new[] { "confluence", "checkout", "12345678" });

        confluence.AddCommand<ConfluenceCommitCommand>("commit")
            .WithDescription("Push local edits back to Confluence")
            .WithExample(new[] { "confluence", "commit" });

        confluence.AddCommand<ConfluenceDeleteCommand>("delete")
            .WithDescription("Stage a page for deletion (applied on commit)")
            .WithExample(new[] { "confluence", "delete", "12345678" });
    });

    // Add Azure AI Foundry branch command
    config.AddBranch<FoundrySettings>("foundry", foundry =>
    {
        foundry.SetDescription("Manage Azure AI Foundry authentication and model selection");

        foundry.AddCommand<FoundryInitCommand>("init")
            .WithDescription("Sign in to Azure AI Foundry and select default resource/model")
            .WithExample(new[] { "foundry", "init" })
            .WithExample(new[] { "foundry", "init", "--force" })
            .WithExample(new[] { "foundry", "init", "--tenant", "my-tenant-id" });

        foundry.AddCommand<FoundrySelectCommand>("select")
            .WithDescription("Switch Foundry resource or model without re-authenticating")
            .WithExample(new[] { "foundry", "select" });

        foundry.AddCommand<FoundryTokenCommand>("token")
            .WithDescription("Print access token for the configured Foundry resource")
            .WithExample(new[] { "foundry", "token" })
            .WithExample(new[] { "foundry", "token", "--scope", "https://management.azure.com/.default" });

        foundry.AddCommand<FoundryStatusCommand>("status")
            .WithDescription("Show current Foundry authentication status")
            .WithExample(new[] { "foundry", "status" });

        foundry.AddCommand<FoundryProxyCommand>("proxy")
            .WithDescription("Start a local HTTP proxy that swaps a proxy token for a real Azure bearer token")
            .WithExample(new[] { "foundry", "proxy" })
            .WithExample(new[] { "foundry", "proxy", "--port", "8080" })
            .WithExample(new[] { "foundry", "proxy", "--token", "my-secret" });

        foundry.AddCommand<FoundryUsageCommand>("usage")
            .WithDescription("Show cost breakdown for the selected Foundry resource")
            .WithExample(new[] { "foundry", "usage" });
    });

    // Add Azure branch command
    config.AddBranch<PKS.Commands.Azure.AzureSettings>("azure", azure =>
    {
        azure.SetDescription("Manage Azure authentication and resources");
        azure.AddCommand<PKS.Commands.Azure.AzureInitCommand>("init")
            .WithDescription("Authenticate with Azure and select a subscription")
            .WithExample(new[] { "azure", "init" });
        azure.AddCommand<PKS.Commands.Azure.AzureUsageCommand>("usage")
            .WithDescription("Show Azure cost and sponsorship credit balance for a subscription")
            .WithExample(new[] { "azure", "usage" });
    });

    // Add vm branch command
    config.AddBranch<PKS.Commands.Vm.VmSettings>("vm", vm =>
    {
        vm.SetDescription("Manage virtual machines");
        vm.AddCommand<PKS.Commands.Vm.VmInitCommand>("init")
            .WithDescription("Provision a new VM and register it as an SSH target")
            .WithExample(new[] { "vm", "init" });
        vm.AddCommand<PKS.Commands.Vm.VmAutoshutdownCommand>("autoshutdown")
            .WithDescription("Configure auto-shutdown for a VM")
            .WithExample(new[] { "vm", "autoshutdown", "my-vm", "--idle", "30" })
            .WithExample(new[] { "vm", "autoshutdown", "my-vm", "--scheduled", "22:00" })
            .WithExample(new[] { "vm", "autoshutdown", "my-vm", "--disable" });
        vm.AddCommand<PKS.Commands.Vm.VmListCommand>("list")
            .WithDescription("List VMs provisioned with pks vm init")
            .WithExample(new[] { "vm", "list" });
        vm.AddCommand<PKS.Commands.Vm.VmStatusCommand>("status")
            .WithDescription("Show VM status with disk/memory/docker stats and an action menu")
            .WithExample(new[] { "vm", "status" });
        vm.AddCommand<PKS.Commands.Vm.VmDestroyCommand>("destroy")
            .WithDescription("Destroy a VM and all its associated Azure resources")
            .WithExample(new[] { "vm", "destroy" });
    });

    // Add fileshare branch (provider auth management)
    config.AddBranch<FileShareSettings>("fileshare", fs =>
    {
        fs.SetDescription("Manage file share provider credentials");

        fs.AddCommand<FileShareInitCommand>("init")
            .WithDescription("Authenticate with a file share provider")
            .WithExample(["fileshare", "init"])
            .WithExample(["fileshare", "init", "--force"]);

        fs.AddCommand<FileShareStatusCommand>("status")
            .WithDescription("Show authentication status for all file share providers")
            .WithExample(["fileshare", "status"]);
    });

    // Add storage branch (universal agent-safe operations)
    config.AddBranch<StorageSettings>("storage", storage =>
    {
        storage.SetDescription("Universal storage operations — download is agent-safe, upload requires consent");

        storage.AddCommand<StorageListCommand>("list")
            .WithDescription("List storage resources across authenticated providers")
            .WithExample(["storage", "list"]);

        storage.AddCommand<StorageSyncCommand>("sync")
            .WithDescription("Sync files between storage and local directory")
            .WithExample(["storage", "sync", "--direction", "download", "./local"])
            .WithExample(["storage", "sync", "--direction", "upload", "./local"])
            .WithExample(["storage", "sync", "--dry-run"]);

        storage.AddCommand<StorageLsCommand>("ls")
            .WithDescription("List files and directories in a share (agent-safe)")
            .WithExample(["storage", "ls"])
            .WithExample(["storage", "ls", "/users", "--count"])
            .WithExample(["storage", "ls", "--json"]);
    });

    // Add Application Insights branch command
    config.AddBranch<AppInsightsSettings>("appinsights", ai =>
    {
        ai.SetDescription("Manage Application Insights configuration for telemetry queries");

        ai.AddCommand<AppInsightsInitCommand>("init")
            .WithDescription("Configure Application Insights App ID and API key")
            .WithExample(new[] { "appinsights", "init" })
            .WithExample(new[] { "appinsights", "init", "--force" });

        ai.AddCommand<AppInsightsStatusCommand>("status")
            .WithDescription("Show Application Insights configuration and connection status")
            .WithExample(new[] { "appinsights", "status" });
    });

    // Add otel branch for telemetry queries
    config.AddBranch<OtelSettings>("otel", otel =>
    {
        otel.SetDescription("Query structured telemetry data from Application Insights");

        otel.AddCommand<OtelErrorsCommand>("errors")
            .WithDescription("List recent exceptions (most recent first)")
            .WithExample(new[] { "otel", "errors" })
            .WithExample(new[] { "otel", "errors", "my-app", "--since", "6h", "--limit", "50" })
            .WithExample(new[] { "otel", "errors", "--format", "Json" })
            .WithExample(new[] { "otel", "errors", "--operation-id", "abc123" });

        otel.AddCommand<OtelTracesCommand>("traces")
            .WithDescription("List recent requests/traces")
            .WithExample(new[] { "otel", "traces" })
            .WithExample(new[] { "otel", "traces", "--has-error", "--since", "24h" })
            .WithExample(new[] { "otel", "traces", "--format", "Json" });

        otel.AddCommand<OtelLogsCommand>("logs")
            .WithDescription("List structured log entries")
            .WithExample(new[] { "otel", "logs" })
            .WithExample(new[] { "otel", "logs", "--severity", "Error", "--since", "7d" })
            .WithExample(new[] { "otel", "logs", "--trace-id", "abc123", "--format", "Json" });

        otel.AddCommand<OtelSpansCommand>("spans")
            .WithDescription("List spans for a specific trace")
            .WithExample(new[] { "otel", "spans", "--operation-id", "abc123" })
            .WithExample(new[] { "otel", "spans", "--operation-id", "abc123", "--format", "Json" });
    });

    // Add Google AI branch command
    config.AddBranch("google", google =>
    {
        google.SetDescription("Manage Google AI credentials");

        google.AddCommand<GoogleInitCommand>("init")
            .WithDescription("Register a Google AI Studio API key")
            .WithExample(new[] { "google", "init" })
            .WithExample(new[] { "google", "init", "--force" });

        google.AddCommand<GoogleStatusCommand>("status")
            .WithDescription("Show registered Google AI credentials")
            .WithExample(new[] { "google", "status" });
    });

    // Add Microsoft Graph branch command
    config.AddBranch<PKS.Commands.MsGraph.MsGraphSettings>("ms-graph", msgraph =>
    {
        msgraph.SetDescription("Manage Microsoft Graph API authentication");

        msgraph.AddCommand<PKS.Commands.MsGraph.MsGraphRegisterCommand>("register")
            .WithDescription("Authenticate with Microsoft Graph using device code flow")
            .WithExample(new[] { "ms-graph", "register", "--client-id", "your-client-id", "--tenant-id", "your-tenant-id" })
            .WithExample(new[] { "ms-graph", "register" });
    });

    // Add email branch command
    config.AddBranch<PKS.Commands.Email.EmailSettings>("email", email =>
    {
        email.SetDescription("Email management and export");

        email.AddCommand<PKS.Commands.Email.EmailExportCommand>("export")
            .WithDescription("Export emails from Microsoft Graph to markdown files")
            .WithExample(new[] { "email", "export" })
            .WithExample(new[] { "email", "export", "--after", "2026-01-01", "--folder", "inbox" })
            .WithExample(new[] { "email", "export", "-o", "./my-emails", "--max", "100" });
    });

    // Add voice push-to-talk command (heypoul + Azure Speech)
    config.AddBranch<VoiceSettings>("voice", voice =>
    {
        voice.SetDescription("Push-to-talk voice dictation powered by Azure AI Foundry Speech");
        voice.AddCommand<VoiceStartCommand>("start")
            .WithDescription("Start heypoul voice assistant (hold key to record, release to transcribe)")
            .WithExample(new[] { "voice", "start" })
            .WithExample(new[] { "voice", "start", "--key", "100", "--language", "da-DK" });
        voice.AddCommand<VoiceStopCommand>("off")
            .WithDescription("Stop the running heypoul voice assistant")
            .WithExample(new[] { "voice", "off" });
        voice.AddCommand<VoiceShowCommand>("show")
            .WithDescription("Browse past voice dictations and re-inject selected text")
            .WithExample(new[] { "voice", "show" })
            .WithExample(new[] { "voice", "show", "-n", "50" });
        voice.AddCommand<VoiceSettingsCommand>("settings")
            .WithDescription("Open the heypoul settings window (microphone, language, push-to-talk key)")
            .WithExample(new[] { "voice", "settings" });
    });

    // Local AI models — pks model <name> <verb>
    config.AddBranch<PKS.Commands.Model.ModelSettings>("model", model =>
    {
        model.SetDescription("Manage local AI models (download, status, removal)");
        model.AddCommand<PKS.Commands.Model.ModelListCommand>("list")
            .WithDescription("List known and installed AI models")
            .WithExample(new[] { "model", "list" });

        foreach (var entry in PKS.Infrastructure.Models.ModelCatalog.Known)
        {
            var name = entry.Name;
            model.AddBranch<PKS.Commands.Model.ModelSettings>(name, m =>
            {
                m.SetDescription(entry.DisplayName);
                m.AddCommand<PKS.Commands.Model.ModelInitCommand>("init")
                    .WithDescription("Download and install this model")
                    .WithData(name)
                    .WithExample(new[] { "model", name, "init" });
                m.AddCommand<PKS.Commands.Model.ModelStatusCommand>("status")
                    .WithDescription("Show install status of this model")
                    .WithData(name)
                    .WithExample(new[] { "model", name, "status" });
                m.AddCommand<PKS.Commands.Model.ModelUpdateCommand>("update")
                    .WithDescription("Re-install if a newer catalog version exists")
                    .WithData(name)
                    .WithExample(new[] { "model", name, "update" });
                m.AddCommand<PKS.Commands.Model.ModelRemoveCommand>("remove")
                    .WithDescription("Uninstall this model and free disk space")
                    .WithData(name)
                    .WithExample(new[] { "model", name, "remove" });
            });
        }
    });

    // Add TTS command (Azure AI Foundry)
    config.AddCommand<TtsCommand>("tts")
        .WithDescription("Generate speech audio from text using Azure AI Foundry TTS")
        .WithExample(new[] { "tts", "\"Hello world\"" })
        .WithExample(new[] { "tts", "--text-file", "script.txt", "--voice", "nova", "--output", "speech.mp3" })
        .WithExample(new[] { "tts", "\"Announcing our launch\"", "--voice", "shimmer", "--output", "launch.mp3" });

    // Add image generation command
    config.AddCommand<ImageCommand>("image")
        .WithDescription("Generate or augment an image using Google AI")
        .WithExample(new[] { "image", "--list-models" })
        .WithExample(new[] { "image", "\"a dark editorial photograph of a match burning\"" })
        .WithExample(new[] { "image", "--prompt-file", "prompt.txt", "--output", "cover.jpg" })
        .WithExample(new[] { "image", "--input", "bg.jpg", "\"Add title 'My Book' in white serif at the top\"", "--output", "cover-final.jpg" });

    // Add promptwall — render a recent Claude prompt as a social-media image
    config.AddCommand<PromptwallCommand>("promptwall")
        .WithDescription("Render a recent Claude prompt as a social-media image")
        .WithExample(["promptwall"])
        .WithExample(["promptwall", "--all-projects"])
        .WithExample(["promptwall", "--include-reply", "--output", "./out"]);

    // Add marketplace branch command
    config.AddBranch("marketplace", marketplace =>
    {
        marketplace.SetDescription("Manage plugin marketplaces");

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceAddCommand>("add")
            .WithDescription("Add a plugin marketplace and apply its policy")
            .WithExample(["marketplace", "add", "https://marketplace.agentics.dk/ctx/ctx-core"])
            .WithExample(["marketplace", "add", "github:owner/repo", "--enable-all", "--non-interactive"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceListCommand>("list")
            .WithDescription("List registered marketplaces")
            .WithExample(["marketplace", "list"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceShowCommand>("show")
            .WithDescription("Show marketplace details")
            .WithExample(["marketplace", "show", "my-marketplace"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceEnableCommand>("enable")
            .WithDescription("Enable plugins in a marketplace")
            .WithExample(["marketplace", "enable", "my-marketplace", "plugin-a"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceDisableCommand>("disable")
            .WithDescription("Disable plugins in a marketplace")
            .WithExample(["marketplace", "disable", "my-marketplace", "plugin-a"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceRemoveCommand>("remove")
            .WithDescription("Remove a marketplace")
            .WithExample(["marketplace", "remove", "my-marketplace"]);

        marketplace.AddCommand<PKS.Commands.Marketplace.MarketplaceRefreshCommand>("refresh")
            .WithDescription("Refresh marketplace plugin list")
            .WithExample(["marketplace", "refresh", "my-marketplace"]);
    });

    // Add claude commands — spawn devcontainer + analysis
    config.AddBranch<Spectre.Console.Cli.CommandSettings>("claude", claude =>
    {
        claude.SetDescription("Spawn claude in a devcontainer, or analyse Claude Code usage");

        claude.SetDefaultCommand<PKS.Commands.Claude.ClaudeSpawnCommand>();

        claude.AddCommand<PKS.Commands.Claude.ClaudeStatsCommand>("stats")
            .WithDescription("Show response-time performance stats from local session files")
            .WithExample(["claude", "stats"])
            .WithExample(["claude", "stats", "--days", "14"])
            .WithExample(["claude", "stats", "--all-projects"]);

        claude.AddCommand<PKS.Commands.Claude.ClaudeUsageCommand>("usage")
            .WithDescription("Show daily API cost from local Claude Code session files (all projects by default)")
            .WithExample(["claude", "usage"])
            .WithExample(["claude", "usage", "my-project"])
            .WithExample(["claude", "usage", "--days", "14"]);

        claude.AddCommand<PKS.Commands.Claude.ManagedSettings.ClaudeManagedSettingsRenderCommand>("managed-settings")
            .WithDescription("Render managed-settings.json from registered marketplaces")
            .WithExample(["claude", "managed-settings"])
            .WithExample(["claude", "managed-settings", "--output", "/etc/claude-code/managed-settings.json"]);

        claude.AddCommand<PKS.Commands.Claude.ClaudeBackupCommand>("backup")
            .WithDescription("Backup ~/.claude/ (sessions, projects, settings) to registered rsync targets")
            .WithExample(["claude", "backup"]);
    });

    // Add brain commands — personal "brain" built from Claude session history
    // See /home/node/.claude/plans/atomic-mixing-allen.md for the multi-phase design.
    config.AddBranch<PKS.Commands.Brain.BrainSettings>("brain", brain =>
    {
        brain.SetDescription("Personal brain — ingest, extract, and synthesize from your Claude session history");

        brain.AddCommand<PKS.Commands.Brain.BrainInitCommand>("init")
            .WithDescription("Initialize ~/.pks-cli/brain/ and (if in a git repo) ./.pks/brain/")
            .WithExample(["brain", "init"]);

        brain.AddCommand<PKS.Commands.Brain.BrainIngestCommand>("ingest")
            .WithDescription("Deterministic ingest of all Claude session JSONL files (no AI). Phase 1.")
            .WithExample(["brain", "ingest"]);

        brain.AddCommand<PKS.Commands.Brain.BrainExtractCommand>("extract")
            .WithDescription("AI-extract per-session summaries via the editable brain-extract skill. Phase 2.")
            .WithExample(["brain", "extract", "--limit", "1", "--dry-run"])
            .WithExample(["brain", "extract", "--since", "7d", "--model", "haiku"]);

        brain.AddCommand<PKS.Commands.Brain.BrainSynthCommand>("synth")
            .WithDescription("Synthesise per-session extracts into themes.md + bad-habits.md + clusters.json. Phase 3.")
            .WithExample(["brain", "synth", "--dry-run"])
            .WithExample(["brain", "synth", "--no-ai"])
            .WithExample(["brain", "synth", "--max-clusters", "10"]);

        brain.AddCommand<PKS.Commands.Brain.BrainWikiCommand>("wiki")
            .WithDescription("Generate per-cluster wiki pages from synthesis/clusters.json. Phase 4.")
            .WithExample(["brain", "wiki", "--dry-run"])
            .WithExample(["brain", "wiki", "--max-clusters", "5"])
            .WithExample(["brain", "wiki", "--no-ai"]);

        brain.AddCommand<PKS.Commands.Brain.BrainAdrCommand>("adr")
            .WithDescription("Distil architectural clusters into ADRs (Status/Context/Decision/Consequences). Phase 5.")
            .WithExample(["brain", "adr", "--dry-run"])
            .WithExample(["brain", "adr", "--max-adrs", "5"])
            .WithExample(["brain", "adr", "--include-tag", "rsc", "--include-tag", "monorepo"]);

        brain.AddCommand<PKS.Commands.Brain.BrainRefreshCommand>("refresh")
            .WithDescription("Bring the whole brain up to date: ingest → extract → synth → wiki → adr in one go")
            .WithExample(["brain", "refresh", "--dry-run"])
            .WithExample(["brain", "refresh", "-y"])
            .WithExample(["brain", "refresh", "--no-ai"]);

        brain.AddCommand<PKS.Commands.Brain.BrainStatusCommand>("status")
            .WithDescription("Show what the brain knows so far (projects, sessions, prompts, tools, errors)")
            .WithExample(["brain", "status"]);

        brain.AddCommand<PKS.Commands.Brain.BrainSearchCommand>("search")
            .WithDescription("Grep across the brain firehoses (prompts, tools, files, errors) and extracts")
            .WithExample(["brain", "search", "streamId"])
            .WithExample(["brain", "search", "auth", "--in", "extracts", "--limit", "5"])
            .WithExample(["brain", "search", "Keycloak", "--since", "7d"]);

        brain.AddBranch("skill", skill =>
        {
            skill.SetDescription("Inspect and edit the brain skills (extract / synth / wiki prompts)");
            skill.AddCommand<PKS.Commands.Brain.BrainSkillListCommand>("list")
                .WithDescription("List all brain skills and where each resolves from (embedded vs user override)")
                .WithExample(["brain", "skill", "list"]);
            skill.AddCommand<PKS.Commands.Brain.BrainSkillInitCommand>("init")
                .WithDescription("Copy a skill's embedded default to ~/.claude/skills/<name>/SKILL.md so you can edit it")
                .WithExample(["brain", "skill", "init", "brain-extract"])
                .WithExample(["brain", "skill", "init", "brain-wiki-page", "--force"]);
            skill.AddCommand<PKS.Commands.Brain.BrainSkillShowCommand>("show")
                .WithDescription("Print the currently-resolved body of a skill to stdout")
                .WithExample(["brain", "skill", "show", "brain-extract"]);
        });
    });

    // Add git branch command (credential helpers)
    config.AddBranch("git", git =>
    {
        git.SetDescription("Git credential helpers");

        git.AddCommand<GitAskPassCommand>("askpass")
            .WithDescription("Git credential helper for Azure DevOps (GIT_ASKPASS)")
            .WithExample(new[] { "git", "askpass", "--install" })
            .WithExample(new[] { "git", "askpass", "Password for 'https://dev.azure.com':" });
    });

    // Add hooks branch command with subcommands
    config.AddBranch<HooksSettings>("hooks", hooks =>
    {
        hooks.SetDescription("Manage Claude Code hooks integration");
        hooks.SetDefaultCommand<HooksMenuCommand>();

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
    config.AddCommand<PKS.Commands.Vm.VmScheduleCommand>("schedule")
        .WithDescription("Interactively configure scheduled start/stop for a VM")
        .WithExample(new[] { "schedule" });

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

// Set up OpenTelemetry tracing if an OTLP endpoint is configured (e.g. injected by Aspire).
// This makes runner spans visible in the Aspire dashboard alongside Next.js server traces.
using var tracerProvider = SetupTracing();
using var meterProvider = SetupMetrics();

// Strip meta-flags before Spectre parses args (it doesn't know about them)
var filteredArgs = args
    .Where(a => !a.Equals("--no-logo", StringComparison.OrdinalIgnoreCase)
             && !a.Equals("--debug", StringComparison.OrdinalIgnoreCase))
    .ToArray();

return await app.RunAsync(filteredArgs);

static TracerProvider? SetupTracing()
{
    // Only activate when an OTLP endpoint is available (Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT).
    var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (string.IsNullOrEmpty(otlpEndpoint)) return null;

    // pks-cli runs via tmux send-keys rather than being directly spawned by Aspire,
    // so it never receives OTEL_EXPORTER_OTLP_CERTIFICATE. Bypass TLS validation for
    // localhost endpoints (Aspire devcontainer uses an ephemeral self-signed cert).
    var isLocalhost = otlpEndpoint.Contains("localhost") || otlpEndpoint.Contains("127.0.0.1");

    return Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("pks-cli", serviceVersion: GetVersion()))
        .AddSource(PKS.Commands.Agentics.Runner.AgenticsRunnerStartCommand.ActivitySourceName)
        .AddHttpClientInstrumentation(o =>
        {
            // Suppress polling heartbeats — they fire every few seconds, produce no signal,
            // and flood the trace dashboard. Only trace requests that represent real work.
            o.FilterHttpRequestMessage = req =>
                req.RequestUri?.AbsolutePath.EndsWith("/runners/jobs") != true;
        })
        .AddOtlpExporter(opts =>
        {
            if (isLocalhost)
            {
                // HttpClientFactory is used for both gRPC and HTTP/protobuf in OTel .NET SDK 1.6+.
                // DangerousAcceptAnyServerCertificateValidator is safe here: localhost-only, devcontainer.
                // Explicitly request HTTP/2 so the underlying client works for both gRPC (requires HTTP/2)
                // and http/protobuf (compatible with HTTP/2 or HTTP/1.1).
                opts.HttpClientFactory = () =>
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                    var client = new HttpClient(handler)
                    {
                        DefaultRequestVersion = System.Net.HttpVersion.Version20,
                        DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
                    };
                    return client;
                };
            }
        })
        .Build();
}

static MeterProvider? SetupMetrics()
{
    // Only activate when an OTLP endpoint is available (Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT).
    var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
    if (string.IsNullOrEmpty(otlpEndpoint)) return null;

    // pks-cli runs via tmux send-keys rather than being directly spawned by Aspire,
    // so it never receives OTEL_EXPORTER_OTLP_CERTIFICATE. Bypass TLS validation for
    // localhost endpoints (Aspire devcontainer uses an ephemeral self-signed cert).
    var isLocalhost = otlpEndpoint.Contains("localhost") || otlpEndpoint.Contains("127.0.0.1");

    return Sdk.CreateMeterProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService("pks-cli", serviceVersion: GetVersion()))
        .AddMeter(PKS.Commands.Agentics.Runner.AgenticsRunnerStartCommand.MeterName)
        .AddOtlpExporter(opts =>
        {
            if (isLocalhost)
            {
                // HttpClientFactory is used for both gRPC and HTTP/protobuf in OTel .NET SDK 1.6+.
                // DangerousAcceptAnyServerCertificateValidator is safe here: localhost-only, devcontainer.
                // Explicitly request HTTP/2 so the underlying client works for both gRPC (requires HTTP/2)
                // and http/protobuf (compatible with HTTP/2 or HTTP/1.1).
                opts.HttpClientFactory = () =>
                {
                    var handler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    };
                    var client = new HttpClient(handler)
                    {
                        DefaultRequestVersion = System.Net.HttpVersion.Version20,
                        DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
                    };
                    return client;
                };
            }
        })
        .Build();
}

static string GetVersion()
{
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? assembly.GetName().Version?.ToString()
                  ?? "unknown";
    return version;
}

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

    // Display version information
    var version = GetVersion();
    var runtimeVersion = Environment.Version.ToString();
    AnsiConsole.MarkupLine($"[cyan]Version {version}[/]");
    AnsiConsole.MarkupLine($"[dim].NET Runtime {runtimeVersion}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]Type 'dotnet dnx pks-cli -- --help' to get started with your agentic development journey![/]");
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

        // Skip for git askpass (GIT_ASKPASS must have zero extra output)
        var isGitAskPass = commandArgs.Length > 2 &&
                           commandArgs[1].Equals("git", StringComparison.OrdinalIgnoreCase) &&
                           commandArgs[2].Equals("askpass", StringComparison.OrdinalIgnoreCase);
        if (isGitAskPass)
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
