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
using PKS.Commands.Ado;
using PKS.Commands.Foundry;
using PKS.Commands.Jira;
using PKS.Commands.Registry;
using PKS.Commands.Google;
using PKS.Commands.Image;
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

// Skip banner for hooks commands with --json flag OR when it's a hook event command
var hasJsonFlag = commandArgs.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("-j", StringComparison.OrdinalIgnoreCase));

var isHookEventCommand = commandArgs.Length > 2 &&
                        commandArgs[1].Equals("hooks", StringComparison.OrdinalIgnoreCase) &&
                        new[] { "pre-tool-use", "post-tool-use", "user-prompt-submit", "stop" }
                            .Contains(commandArgs[2], StringComparer.OrdinalIgnoreCase);

// Display welcome banner with fancy ASCII art (unless we should skip it)
if (!isMcpStdio && !isGitAskPass && !(isHooksCommand && (hasJsonFlag || isHookEventCommand)))
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
services.AddSingleton<PKS.Infrastructure.Services.ISshTargetConfigurationService, PKS.Infrastructure.Services.SshTargetConfigurationService>();
services.AddSingleton<PKS.Infrastructure.Services.ISshCommandRunner, PKS.Infrastructure.Services.SshCommandRunner>();
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

// Configure Jira integration
services.AddSingleton<PKS.Infrastructure.Services.Models.JiraAuthConfig>();
services.AddHttpClient<IJiraService, JiraService>();

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

// Register GitHub Runner services
services.AddSingleton<IRegistryConfigurationService, RegistryConfigurationService>();
services.AddSingleton<ICoolifyConfigurationService, CoolifyConfigurationService>();
services.AddSingleton<ICoolifyLookupService, CoolifyLookupService>();
services.AddSingleton<ICoolifyApiService, CoolifyApiService>();
services.AddSingleton<IRunnerConfigurationService, RunnerConfigurationService>();
services.AddSingleton<IAgenticsRunnerConfigurationService, AgenticsRunnerConfigurationService>();
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

    // Add github branch command with runner subcommands
    config.AddBranch<PKS.Commands.GitHub.GitHubSettings>("github", github =>
    {
        github.SetDescription("Manage GitHub integration and self-hosted runners");

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

    // Add image generation command
    config.AddCommand<ImageCommand>("image")
        .WithDescription("Generate or augment an image using Google AI")
        .WithExample(new[] { "image", "--list-models" })
        .WithExample(new[] { "image", "\"a dark editorial photograph of a match burning\"" })
        .WithExample(new[] { "image", "--prompt-file", "prompt.txt", "--output", "cover.jpg" })
        .WithExample(new[] { "image", "--input", "bg.jpg", "\"Add title 'My Book' in white serif at the top\"", "--output", "cover-final.jpg" });

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

return await app.RunAsync(args);

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
