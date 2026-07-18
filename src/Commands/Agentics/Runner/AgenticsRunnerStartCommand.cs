using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Chat;
using PKS.Infrastructure.Services.AgenticsProxy;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Start the runner daemon to poll for and execute jobs
/// </summary>
public class AgenticsRunnerStartCommand : Command<AgenticsRunnerStartCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAuthenticationService _githubAuth;
    private readonly IAzureFoundryAuthService _foundryAuthService;
    private readonly AzureFoundryAuthConfig _foundryConfig;
    private readonly AgentChatProviderFactory _chatProviderFactory;
    private readonly IAnsiConsole _console;
    private readonly IRunnerExecutionCapabilityProbe _capabilityProbe;

    /// <summary>Phase 4 (SSH handoff) collaborators. Trailing-optional (default null) rather than
    /// retrofitted into every existing call site: DI still injects the registered singletons here at
    /// runtime (see DevcontainerSpawnerService's ISshCommandRunner? for precedent), while the two
    /// pre-existing test fixtures that positionally construct this command with exactly 9 args keep
    /// compiling unchanged. Null (as in those tests) simply means "the degraded-start SSH-handoff
    /// offer never fires" -- everything else behaves exactly as before Phase 4.</summary>
    private readonly PKS.Infrastructure.Services.ISshTargetConfigurationService? _sshTargetConfig;
    private readonly IAgenticsRunnerSshHandoffService? _sshHandoffService;

    /// <summary>Phase 5 (credential forwarding) collaborators. Same trailing-optional pattern as the
    /// Phase 4 pair above -- when null (as in the two 9-arg test fixtures), the post-handoff
    /// credential-volume warning and the opt-in forwarding offer both silently skip rather than
    /// throwing, so nothing that already constructs this command with fewer args breaks.</summary>
    private readonly PKS.Infrastructure.Services.Security.IActionGuard? _guard;
    private readonly PKS.Infrastructure.Services.Security.ITotpSeedStore? _totpStore;
    private readonly PKS.Infrastructure.IConfigurationService? _configurationService;

    /// <summary>Job ids already logged as "declined -- devcontainer spawning unavailable".
    /// Rate-limits the pre-claim-refusal grey line to once per job id instead of once per
    /// poll cycle -- the same queued job keeps coming back every cycle until Phase 2's
    /// server-side `needs` filtering ships (see docs/remote-runner-targets-plan.md D2) or the
    /// operator intervenes.</summary>
    private readonly HashSet<string> _declinedSpawnJobIds = new();

    /// <summary>Last capability set logged to the console, so the poll loop only prints
    /// "Runner capabilities: ..." when it actually changes (e.g. Docker comes back) instead
    /// of every single cycle now that capabilities are recomputed each iteration.</summary>
    private List<string>? _lastLoggedCapabilities;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Kind A chat capability string (external/alp-spec/2026-03-30-draft/spec/13-chat.md).
    /// Mirrors the Server's task-dispatch.ts CHAT_SESSION_CAPABILITY constant.</summary>
    private const string ChatSessionCapability = "chat-session:v1";

    /// <summary>Devcontainer-hosted sub-agent capability (plan `snappy-wandering-mochi` Phase 2). Like
    /// chat-session:v1 this carries no JobType of its own -- it dispatches through the ordinary
    /// alp_operator spawn path unchanged, just without AGENTICS_CHAT_SESSION=1, so the Operator opens
    /// a plain long-lived vibecast/Claude session in the project's own devcontainer instead of dialing
    /// the ALP Chat Channel. Mirrors the Server's task-dispatch.ts DEVCONTAINER_SESSION_CAPABILITY constant.</summary>
    private const string DevAgentSessionCapability = "devcontainer-session:v1";

    /// <summary>ActivitySource name used by the runner. Referenced by Program.cs when building the TracerProvider.</summary>
    public const string ActivitySourceName = "pks-cli.agentics.runner";
    private static readonly System.Diagnostics.ActivitySource _activitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>Meter name used by the runner. Referenced by Program.cs when building the MeterProvider.</summary>
    public const string MeterName = "pks-cli.agentics.runner";
    private static readonly System.Diagnostics.Metrics.Meter _meter = new(MeterName, "1.0.0");
    private static readonly System.Diagnostics.Metrics.Counter<long> _pollCounter =
        _meter.CreateCounter<long>("runner.polls", unit: "{polls}",
            description: "Number of job poll attempts. Use this to verify the runner is alive.");

    /// <summary>Global monotonic counter so debug captures sort correctly across concurrent jobs.</summary>
    private static int _captureSeq = 0;

    // Container reuse is tracked via Docker labels. We require BOTH a fingerprint match AND
    // a runner-instance match before reusing a warm container. Runner-instance bounds the
    // container's lifetime to this runner-process — restart the runner and the next job spawns
    // a fresh container with new bind mounts (so per-job sockets / OTLP / etc. all stay valid).
    // See docs/adr/0002-runner-container-lifetime.md for the reasoning.
    private static readonly string _runnerInstanceId = Guid.NewGuid().ToString("N")[..16];

    // Track active job resources for cleanup on shutdown
    private readonly List<ActiveJobContext> _activeJobs = new();
    private readonly object _activeJobsLock = new();

    // Track in-flight job executions so the poll loop can keep claiming new jobs (e.g. an
    // ALP task dispatch) instead of blocking on a long-lived one (e.g. a chat_llm session
    // held open for the lifetime of a chat). Drained (not abandoned) on shutdown.
    private readonly List<Task> _runningJobTasks = new();
    private readonly object _runningJobTasksLock = new();

    private void TrackRunningJob(Task jobTask)
    {
        lock (_runningJobTasksLock)
        {
            _runningJobTasks.Add(jobTask);
            _runningJobTasks.RemoveAll(t => t.IsCompleted);
        }
    }

    private async Task DrainRunningJobTasksAsync()
    {
        List<Task> pending;
        lock (_runningJobTasksLock)
        {
            pending = _runningJobTasks.Where(t => !t.IsCompleted).ToList();
        }
        if (pending.Count == 0) return;

        _console.MarkupLine($"[yellow]Waiting for {pending.Count} in-flight job(s) to finish...[/]");
        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Warning: one or more in-flight jobs ended with an error during shutdown: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private record ActiveJobContext(
        string JobId,
        string VibecastTmuxSession,
        string? StreamingSession,
        string? ControlSocket,
        string? WorkTreePath,
        string? VibecastHome);

    public class Settings : AgenticsRunnerSettings
    {
        [CommandOption("--polling-interval <SECONDS>")]
        [Description("Polling interval in seconds (default: 10)")]
        [DefaultValue(10)]
        public int PollingInterval { get; set; } = 10;

        [CommandOption("--inprocess")]
        [Description("Execute jobs in-process instead of spawning devcontainers (for testing)")]
        public bool InProcess { get; set; }

        [CommandOption("--worktree")]
        [Description("(--inprocess only) Use a git worktree of the current repo as the job workspace. Without this flag, a fresh git clone (or empty dir) is used instead.")]
        public bool Worktree { get; set; }

        [CommandOption("--work-dir <PATH>")]
        [Description("Base work directory (default: .agentics/_work)")]
        public string? WorkDir { get; set; }

        [CommandOption("--vibecast-binary <PATH>")]
        [Description("Path to vibecast binary (default: uses VIBECAST_BINARY env or 'npx vibecast')")]
        public string? VibecastBinary { get; set; }

        [CommandOption("--project <owner-project>")]
        [Description("Project to run for in owner/project format. Auto-registers if not already registered. Without this, uses first saved registration.")]
        public string? Project { get; set; }

        [CommandOption("--server <SERVER>")]
        [Description("Agentics server URL (falls back to AGENTIC_SERVER env, then agentics.dk). Used when auto-registering with --project.")]
        public string? Server { get; set; }

        [CommandOption("--git-user-name <NAME>")]
        [Description("Git user.name to configure inside the devcontainer (default: si-14x)")]
        public string? GitUserName { get; set; }

        [CommandOption("--git-user-email <EMAIL>")]
        [Description("Git user.email to configure inside the devcontainer (default: si-14x@agentics.dk)")]
        public string? GitUserEmail { get; set; }

        [CommandOption("--chat-llm-backend-url <URL>")]
        [Description("OpenAI-compatible chat-completions backend base URL (e.g. http://localhost:11434/v1) for chat-llm:v1 Jobs (default: uses CHAT_LLM_BACKEND_URL env). Declaring this enables the chat-llm:v1 capability -- see external/alp-spec/2026-03-30-draft/spec/13-chat.md.")]
        public string? ChatLlmBackendUrl { get; set; }

        [CommandOption("--chat-llm-backend-key <KEY>")]
        [Description("API key sent to the chat-llm:v1 backend (default: uses CHAT_LLM_BACKEND_KEY env). Never sent to or stored by the Server -- forwarded only to the configured backend, per 13-chat.md's Kind B credential invariant. Ignored when --chat-llm-backend-url is not set (the AgentChatProviderFactory-resolved path manages its own credentials).")]
        public string? ChatLlmBackendKey { get; set; }

        [CommandOption("--chat-llm-model <MODEL>")]
        [Description("Model id for chat-llm:v1 Jobs when no --chat-llm-backend-url override is set (default: uses CHAT_LLM_MODEL env, else 'gpt-5.5'). Resolved via the same AgentChatProviderFactory 'pks agent' uses (agent.models.<id> in ~/.pks-cli/settings.json, then built-in defaults), so an already-authorized `pks foundry init` session or stored Anthropic/Azure OpenAI key is used automatically -- no manual backend URL/key needed.")]
        public string? ChatLlmModel { get; set; }

        [CommandOption("--chat-llm-verbose")]
        [Description("Log every chat-llm:v1 Chat Channel frame (chat.completion.request/chunk/done/error, chat.models.request/response, chat.end) to this console as it's sent/received, for debugging the chat pipeline. Frame text is markup-escaped and truncated -- it can include the user's own chat content, so avoid this on a shared/recorded terminal if that matters.")]
        public bool ChatLlmVerbose { get; set; }

        [CommandOption("--configure")]
        [Description("Re-run the interactive capability/chat-model configuration prompts even if this registration already has a persisted profile from a previous run. Ignored on a non-interactive console (never blocks).")]
        public bool Configure { get; set; }
    }

    public AgenticsRunnerStartCommand(
        IAgenticsRunnerConfigurationService configService,
        IDevcontainerSpawnerService spawnerService,
        IHttpClientFactory httpClientFactory,
        IGitHubAuthenticationService githubAuth,
        IAzureFoundryAuthService foundryAuthService,
        AzureFoundryAuthConfig foundryConfig,
        AgentChatProviderFactory chatProviderFactory,
        IAnsiConsole console,
        IRunnerExecutionCapabilityProbe capabilityProbe,
        PKS.Infrastructure.Services.ISshTargetConfigurationService? sshTargetConfig = null,
        IAgenticsRunnerSshHandoffService? sshHandoffService = null,
        PKS.Infrastructure.Services.Security.IActionGuard? guard = null,
        PKS.Infrastructure.Services.Security.ITotpSeedStore? totpStore = null,
        PKS.Infrastructure.IConfigurationService? configurationService = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _githubAuth = githubAuth ?? throw new ArgumentNullException(nameof(githubAuth));
        _foundryAuthService = foundryAuthService ?? throw new ArgumentNullException(nameof(foundryAuthService));
        _foundryConfig = foundryConfig ?? throw new ArgumentNullException(nameof(foundryConfig));
        _chatProviderFactory = chatProviderFactory ?? throw new ArgumentNullException(nameof(chatProviderFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _capabilityProbe = capabilityProbe ?? throw new ArgumentNullException(nameof(capabilityProbe));
        _sshTargetConfig = sshTargetConfig;
        _sshHandoffService = sshHandoffService;
        _guard = guard;
        _totpStore = totpStore;
        _configurationService = configurationService;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner();

            // ── OTEL startup diagnostics ──────────────────────────────────────────────
            {
                var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
                var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
                var resourceAttrs = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
                if (!string.IsNullOrEmpty(otlpEndpoint))
                {
                    _console.MarkupLine($"[dim]OTEL exporter: [cyan]{otlpEndpoint.EscapeMarkup()}[/][/]");
                    if (!string.IsNullOrEmpty(serviceName))
                        _console.MarkupLine($"[dim]OTEL service:  [cyan]{serviceName.EscapeMarkup()}[/][/]");
                    if (!string.IsNullOrEmpty(resourceAttrs))
                        _console.MarkupLine($"[dim]OTEL resource: [cyan]{resourceAttrs.EscapeMarkup()}[/][/]");

                    // Emit a startup span — visible immediately in the Aspire dashboard and confirms OTEL is working.
                    using var startSpan = _activitySource.StartActivity("runner.start");
                    startSpan?.SetTag("runner.version",
                        System.Reflection.Assembly.GetExecutingAssembly()
                            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                            is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
                            ? attrs[0].InformationalVersion : "unknown");
                    startSpan?.SetTag("otel.endpoint", otlpEndpoint);
                }
                else
                {
                    _console.MarkupLine("[dim]OTEL: no OTEL_EXPORTER_OTLP_ENDPOINT — tracing disabled[/]");
                }
            }
            // ─────────────────────────────────────────────────────────────────────────

            // Resolve registration: --project auto-registers if needed, otherwise use first saved
            AgenticsRunnerRegistration registration;
            if (!string.IsNullOrEmpty(settings.Project))
            {
                registration = await ResolveOrRegisterAsync(settings.Project, settings.Server, settings.Verbose);
            }
            else
            {
                var registrations = await _configService.ListRegistrationsAsync();
                if (registrations.Count == 0)
                {
                    DisplayError("No runner registrations found. Use --project owner/project to auto-register, or run 'pks agentics runner register <owner/project>' first.");
                    return 1;
                }
                registration = registrations[0];
            }

            if (settings.Verbose)
            {
                DisplayInfo($"Using registration: {registration.Name} ({registration.Id})");
                DisplayInfo($"Owner: {registration.Owner}/{registration.Project}");
                DisplayInfo($"Server: {registration.Server}");
                DisplayInfo($"Polling interval: {settings.PollingInterval}s");
            }

            // Probe Docker/spawn availability BEFORE the two blocking preflights below: the
            // GitHub device-code preflight (hard `return 1` on failure) and the
            // GitCredentialServer construction (Kestrel ListenUnixSocket on a Path.GetTempPath()
            // path, which can hang/throw on a Windows box without Docker Desktop). Neither is
            // needed to serve git_push/git_distribute/chat_llm jobs -- those never touch the
            // spawner, only ExecuteSpawnModeAsync does. Skipping both when spawn mode is
            // unavailable is what lets a Docker-less runner reach "Polling every Ns..." instead
            // of stalling or hard-failing at startup. See docs/remote-runner-targets-plan.md
            // Phase 1 (this ordering fix is the load-bearing part of that phase).
            var spawnCapabilityStatus = settings.InProcess
                ? null
                : await _capabilityProbe.GetStatusAsync(CancellationToken.None);
            var spawnModeAvailable = settings.InProcess || (spawnCapabilityStatus?.DockerAvailable ?? false);

            if (!settings.InProcess && !spawnModeAvailable)
            {
                var reason = spawnCapabilityStatus?.Reason ?? "Docker availability check did not run";
                if (_console.Profile.Capabilities.Interactive)
                {
                    var panel = new Panel(
                        $"[yellow]Devcontainer spawning is unavailable on this machine.[/]\n" +
                        $"[dim]Reason: {reason.EscapeMarkup()}[/]\n\n" +
                        "This runner will advertise reduced capabilities (no [bold]alp_operator[/], " +
                        "[bold]chat-session:v1[/], [bold]devcontainer-session:v1[/]) and will leave any " +
                        "devcontainer job it is offered [bold]queued[/] for a capable runner instead of " +
                        "claiming and failing it.\n" +
                        "[dim]git_push / git_distribute / chat-llm:v1 jobs are unaffected.[/]")
                        .Header("[yellow]Degraded start[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderColor(Color.Yellow);
                    _console.Write(panel);

                    // Phase 4 (SSH handoff, docs/remote-runner-targets-plan.md): Docker is
                    // unavailable here, but if the operator has registered SSH targets (`pks ssh
                    // register` / `pks vm init`), offer to run this project's runner on one of them
                    // instead of continuing locally with reduced capabilities. Only offered when both
                    // Phase 4 collaborators were actually injected (see the constructor note) and at
                    // least one target is registered.
                    if (_sshTargetConfig != null && _sshHandoffService != null)
                    {
                        var sshTargets = await _sshTargetConfig.ListTargetsAsync();
                        if (sshTargets.Count > 0 && await OfferSshHandoffAsync(registration, sshTargets))
                        {
                            // The handoff itself started the runner remotely -- this local process
                            // has nothing left to do.
                            return 0;
                        }
                    }
                }
                else
                {
                    // Non-interactive: never block on a prompt -- just say so and continue with
                    // reduced capabilities (checked via the injectable _console.Profile, not the
                    // static System.Console.IsInputRedirected).
                    _console.MarkupLine($"[yellow]Devcontainer spawning unavailable ({reason.EscapeMarkup()}) — starting with reduced capabilities; devcontainer jobs will be left queued for a capable runner.[/]");
                }
            }

            // GitHub authentication pre-flight: only run when the project actually
            // points at github.com AND spawn mode is available (see above -- this preflight
            // exists to support ExecuteSpawnModeAsync via the GitCredentialServer below, not
            // git_push/git_distribute, which resolve their own credentials independently).
            // Self-hosted projects (repo on the agentics server) never need GitHub credentials,
            // so prompting would be both unnecessary and surprising. The runner queries the
            // server to find out.
            var requiresGitHub = false;
            if (!settings.InProcess && spawnModeAvailable)
            {
                requiresGitHub = await ProjectRequiresGitHubAsync(registration);
                if (!requiresGitHub && settings.Verbose)
                {
                    DisplayInfo("Project does not use GitHub — skipping GitHub auth preflight.");
                }
            }
            if (!settings.InProcess && spawnModeAvailable && requiresGitHub)
            {
                var isAuthenticated = await _githubAuth.IsAuthenticatedAsync();

                if (!isAuthenticated)
                {
                    _console.MarkupLine("[yellow]No valid GitHub token found. Attempting refresh...[/]");
                    var refreshed = await _githubAuth.RefreshTokenAsync();
                    if (refreshed != null)
                    {
                        _console.MarkupLine("[green]Token refreshed.[/]");
                        isAuthenticated = true;
                    }
                }

                if (!isAuthenticated)
                {
                    _console.MarkupLine("[yellow]Token refresh failed. Starting GitHub device-code login...[/]");
                    _console.WriteLine();

                    var deviceCode = await _githubAuth.InitiateDeviceCodeFlowAsync();
                    _console.MarkupLine($"[yellow]Open:[/]  [link]{deviceCode.VerificationUri}[/]");
                    _console.MarkupLine($"[yellow]Code:[/]  [bold cyan]{deviceCode.UserCode}[/]");
                    _console.WriteLine();
                    _console.MarkupLine("[cyan]Waiting for GitHub authorization...[/]");

                    var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);
                    var pollInterval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 5));
                    PKS.Infrastructure.Services.Models.GitHubDeviceAuthStatus? authResult = null;

                    while (DateTime.UtcNow < expiresAt)
                    {
                        await Task.Delay(pollInterval);
                        authResult = await _githubAuth.PollForAuthenticationAsync(deviceCode.DeviceCode);

                        if (authResult.IsAuthenticated) break;
                        if (authResult.Error == "slow_down")
                            pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5));
                        else if (authResult.Error != "authorization_pending")
                            break;
                    }

                    if (authResult?.IsAuthenticated == true)
                    {
                        await _githubAuth.StoreTokenAsync(new PKS.Infrastructure.Services.Models.GitHubStoredToken
                        {
                            AccessToken = authResult.AccessToken!,
                            RefreshToken = authResult.RefreshToken,
                            Scopes = authResult.Scopes,
                            CreatedAt = DateTime.UtcNow,
                            ExpiresAt = authResult.ExpiresAt,
                            IsValid = true,
                            LastValidated = DateTime.UtcNow
                        });
                        _console.MarkupLine("[green]GitHub login successful.[/]");
                        isAuthenticated = true;
                    }
                    else
                    {
                        var detail = authResult?.ErrorDescription ?? authResult?.Error ?? "authorization timed out";
                        DisplayError($"GitHub authentication failed: {detail}");
                        return 1;
                    }
                }
            }

            _console.WriteLine();
            DisplayInfo($"Starting runner daemon for [cyan]{registration.Owner}/{registration.Project}[/]");
            DisplayInfo("Press Ctrl+C to stop.");
            _console.WriteLine();

            GitCredentialServer? credentialServer = null;
            if (!settings.InProcess && spawnModeAvailable)
            {
                // Start credential server (serves locally stored device-code OAuth token). This is
                // the second blocking preflight hoisted behind the capability probe (see above) --
                // StartAsync() calls Kestrel's ListenUnixSocket on a Path.GetTempPath() path, which
                // can hang/throw on a Docker-less Windows box. Only ExecuteSpawnModeAsync ever reads
                // credentialServer.SocketPath, so it's safe to leave unconstructed (null) whenever
                // spawn mode is unavailable -- git_push/git_distribute/chat_llm resolve credentials
                // independently and never touch it.
                credentialServer = new GitCredentialServer(_githubAuth, registration.Id);
                await credentialServer.StartAsync();

                if (settings.Verbose)
                {
                    DisplayInfo($"Credential server started at: {credentialServer.SocketPath}");
                }
            }
            else if (settings.InProcess)
            {
                DisplayInfo("Running in [yellow]--inprocess[/] mode (no devcontainer spawning)");
            }

            try
            {

                // Set up cancellation (handle both SIGINT via Ctrl+C and SIGTERM via Aspire/process exit)
                using var cts = new CancellationTokenSource();
                System.Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    DisplayInfo("Shutdown requested (SIGINT)...");
                    cts.Cancel();
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        DisplayInfo("Process exit signal received (SIGTERM)...");
                        cts.Cancel();
                        // Block briefly to allow cleanup to run
                        CleanupAllActiveJobsAsync().GetAwaiter().GetResult();
                    }
                };

                // Runner configuration flow (Phase 3, docs/remote-runner-targets-plan.md): decide
                // which capabilities/chat-models this registration advertises. First run (no
                // persisted profile) or an explicit --configure prompts interactively; every
                // subsequent start is silent and just reuses the persisted profile. A
                // non-interactive console never blocks on a prompt -- it always falls back to the
                // persisted profile, or "auto" (null profile, probe/factory decide) if none exists.
                if (_console.Profile.Capabilities.Interactive && (settings.Configure || registration.Profile == null))
                {
                    registration.Profile = await RunInteractiveConfigureAsync(
                        registration, settings, spawnCapabilityStatus, cts.Token);
                    await _configService.AddRegistrationAsync(registration);
                }
                else if (settings.Configure && !_console.Profile.Capabilities.Interactive)
                {
                    _console.MarkupLine("[yellow]--configure requested but this console is non-interactive — using the persisted profile (or auto) instead.[/]");
                }

                var chatLlmBackendUrl = ResolveChatLlmBackendUrl(settings.ChatLlmBackendUrl);
                var chatLlmBackendKey = ResolveChatLlmBackendKey(settings.ChatLlmBackendKey);
                // Explicit --chat-llm-model flag / CHAT_LLM_MODEL env always wins; the persisted
                // profile's DefaultChatModel is only the fallback before the hardcoded "gpt-5.5".
                var chatLlmModelId = ResolveChatLlmModelId(settings.ChatLlmModel, registration.Profile?.DefaultChatModel);
                var chatLlmVerbose = settings.ChatLlmVerbose;

                // Polling loop. Capabilities are recomputed every iteration inside
                // PollAndDispatchOnceAsync (behind the capability probe's 60s memo) rather than
                // once here at startup, so a Docker daemon that DIES mid-run stops advertising
                // spawn capabilities without requiring a restart -- see
                // docs/remote-runner-targets-plan.md Phase 1.
                //
                // The reverse (a daemon that comes BACK mid-run) deliberately does NOT re-enable
                // spawn work: credentialServer and the GitHub device-code preflight are one-shot
                // startup work, so a runner that started degraded stays degraded for its process
                // lifetime and must be restarted. PollAndDispatchOnceAsync enforces that by passing
                // `spawnEnabled: credentialServer != null` into ComputeCapabilitiesAsync, so we
                // never advertise a spawn capability we would then decline every job for.
                var jobsProcessed = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        jobsProcessed += await PollAndDispatchOnceAsync(
                            registration, settings, credentialServer,
                            chatLlmBackendUrl, chatLlmBackendKey, chatLlmModelId, chatLlmVerbose,
                            cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _console.MarkupLine($"[red]Polling error:[/] {ex.Message.EscapeMarkup()}");
                        _pollCounter.Add(1,
                            new KeyValuePair<string, object?>("owner", registration.Owner),
                            new KeyValuePair<string, object?>("project", registration.Project),
                            new KeyValuePair<string, object?>("result", "error"));
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(settings.PollingInterval), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                _console.WriteLine();

                // Wait for any backgrounded jobs (e.g. an in-flight chat_llm session) to finish,
                // then clean up all active spawn-mode job resources.
                await DrainRunningJobTasksAsync();
                await CleanupAllActiveJobsAsync();

                DisplaySuccess($"Runner daemon stopped. Jobs processed: {jobsProcessed}");

            }
            finally
            {
                if (credentialServer != null)
                    await credentialServer.DisposeAsync();
            }
            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Runner daemon failed: {ex.Message}");
            if (settings.Verbose)
                _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Finds an existing local registration for the given owner/project, or auto-registers
    /// against the server and saves it. This lets 'start --project owner/proj' be self-contained.
    /// </summary>
    private async Task ReportJobResultAsync(
        AgenticsRunnerRegistration registration,
        string result,
        string? error,
        CancellationToken ct)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registration.Token);
            var body = new { jobResult = result, error };
            await httpClient.PatchAsJsonAsync(
                $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/{registration.Id}",
                body, ct);
        }
        catch
        {
            // Best-effort — don't fail the runner loop if reporting fails
        }
    }

    private async Task<AgenticsRunnerRegistration> ResolveOrRegisterAsync(
        string ownerProject, string? serverOverride, bool verbose)
    {
        var parts = ownerProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException($"--project must be in owner/project format, got: '{ownerProject}'");

        var owner = parts[0];
        var project = parts[1];

        // Check if we already have a saved registration for this project
        var registrations = await _configService.ListRegistrationsAsync();
        var existing = registrations.FirstOrDefault(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // If --server was explicitly provided and differs from the stored URL, update it
            if (!string.IsNullOrEmpty(serverOverride))
            {
                var normalizedServer = serverOverride.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                       serverOverride.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? serverOverride.TrimEnd('/')
                    : $"http://{serverOverride}";

                if (!string.Equals(existing.Server, normalizedServer, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Server = normalizedServer;
                    await _configService.AddRegistrationAsync(existing);
                    DisplayInfo($"Updated server URL for {owner}/{project}: {normalizedServer}");
                }
            }

            if (verbose)
                DisplayInfo($"Found existing registration for {owner}/{project} (id: {existing.Id})");
            return existing;
        }

        // No saved registration — auto-register
        DisplayInfo($"No saved registration for [cyan]{owner}/{project}[/], registering now...");

        var serverHost = serverOverride
            ?? Environment.GetEnvironmentVariable("AGENTICS_SERVER")
            ?? Environment.GetEnvironmentVariable("AGENTIC_SERVER")
            ?? "agentics.dk";

        string serverUrl;
        if (serverHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            serverHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = serverHost.TrimEnd('/');
        }
        else
        {
            var scheme = serverHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                         serverHost.StartsWith("127.0.0.1")
                ? "http"
                : "https";
            serverUrl = $"{scheme}://{serverHost}";
        }

        var runnerName = System.Net.Dns.GetHostName();

        using var httpClient = new HttpClient();
        var requestBody = new { name = runnerName, labels = BuildDefaultRunnerLabels() };
        var httpResponse = await httpClient.PostAsJsonAsync(
            $"{serverUrl}/api/owners/{owner}/projects/{project}/runners",
            requestBody);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Auto-registration failed ({(int)httpResponse.StatusCode}): {errorBody}");
        }

        var json = await httpResponse.Content.ReadAsStringAsync();
        var resp = System.Text.Json.JsonSerializer.Deserialize<RegisterRunnerResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse registration response");

        var registration = new AgenticsRunnerRegistration
        {
            Id = resp.Id ?? Guid.NewGuid().ToString(),
            Name = resp.Name ?? runnerName,
            Token = resp.Token ?? "",
            Owner = owner,
            Project = project,
            Server = serverUrl,
            RegisteredAt = DateTime.UtcNow
        };

        await _configService.AddRegistrationAsync(registration);
        DisplayInfo($"[green]Registered runner '{registration.Name}' for {owner}/{project}[/]");
        return registration;
    }

    /// <summary>
    /// Default job-targeting labels sent at registration when the operator hasn't configured any
    /// (Phase 3, work item 6 -- registration used to send <c>Array.Empty&lt;string&gt;()</c>
    /// unconditionally, which meant a runner could never be targeted by label). "self-hosted"
    /// matches the convention already used by RunnerDaemonService's (unrelated, GitHub Actions)
    /// self-hosted-runner labels; the OS name lets a job request "windows"/"macos"/"linux"
    /// specifically. Duplicated in AgenticsRunnerRegisterCommand.cs rather than extracted, matching
    /// this codebase's existing pattern of small per-command helpers over shared utility classes.
    /// </summary>
    private static string[] BuildDefaultRunnerLabels()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        return new[] { "self-hosted", os };
    }

    /// <summary>
    /// Offers to hand this project's runner off to a registered SSH target instead of continuing
    /// locally with reduced capabilities (Phase 4, docs/remote-runner-targets-plan.md, work item 7).
    /// Only called when interactive, both Phase 4 collaborators are injected, and at least one SSH
    /// target is registered. Returns true (and has already started the remote runner) when the
    /// operator went through with a successful handoff; returns false (declined, or the handoff
    /// failed) for every other outcome, in which case the caller falls through to the ordinary
    /// reduced-capability local start.
    /// </summary>
    internal async Task<bool> OfferSshHandoffAsync(AgenticsRunnerRegistration registration, List<PKS.Infrastructure.Services.SshTarget> sshTargets)
    {
        _console.WriteLine();
        if (!_console.Confirm("[cyan]Hand off this runner to a registered SSH target instead?[/]", defaultValue: true))
            return false;

        var target = await PKS.Commands.Ssh.SshTargetSelection.PickAsync(_console, sshTargets, null, "[cyan]Select SSH target:[/]");
        if (target == null) return false;

        SshProbeResult? probe = null;
        string? probeError = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Probing {target.Host}...", async _ =>
            {
                try { probe = await _sshHandoffService!.ProbeAsync(target); }
                catch (Exception ex) { probeError = ex.Message; }
            });

        if (probeError != null)
        {
            DisplayError($"Could not probe {target.Host}: {probeError}");
            return false;
        }

        _console.MarkupLine($"[cyan1]Docker:[/]  {(probe!.DockerAvailable ? "[green]available[/]" : "[red]unavailable[/]")}");
        _console.MarkupLine($"[cyan1]tmux:[/]    {(probe.TmuxAvailable ? $"[green]{probe.TmuxVersion.EscapeMarkup()}[/]" : "[red]unavailable[/]")}");
        _console.MarkupLine($"[cyan1]dotnet:[/]  {(probe.DotnetAvailable ? $"[green]{probe.DotnetVersion.EscapeMarkup()}[/]" : "[red]unavailable[/]")}");
        _console.MarkupLine($"[cyan1]dnx:[/]     {(probe.DnxAvailable ? "[green]available[/]" : "[red]unavailable[/]")}");

        if (!probe.IsReady)
        {
            _console.MarkupLine("[yellow]Target is not fully ready for a handoff.[/]");
            if (!_console.Confirm("[yellow]Proceed anyway?[/]", defaultValue: false))
                return false;
        }

        var defaultName = SanitizeSuggestedRunnerName(target.Label ?? target.Host);
        var runnerName = _console.Prompt(new TextPrompt<string>("[cyan]Runner name[/]").DefaultValue(defaultName));

        SshHandoffResult? result = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Handing off to {target.Host}...", async ctx =>
            {
                result = await _sshHandoffService!.HandoffAsync(
                    target, registration.Owner, registration.Project, registration.Server, runnerName,
                    onProgress: msg => ctx.Status(msg.EscapeMarkup()));
            });

        if (result!.Success)
        {
            // Record the handoff on the LOCAL registration. HandoffAsync stamps SshTargetLabel on a
            // brand-new registration object that is serialized and scp'd to the remote box only --
            // nothing writes it back here. Without this, SshHandoffCommandHelpers.ResolveAsync (which
            // matches purely on Profile.SshTargetLabel in the local config) finds no match and every
            // one of `pks agentics runner status|logs|stop|claude-login <target>` prints
            // "No project has been handed off to '<target>'" -- including the exact command the
            // success message below tells the operator to run.
            var targetLabel = target.Label ?? target.Host;
            registration.Profile ??= new RunnerProfile();
            registration.Profile.SshTargetLabel = targetLabel;
            registration.Profile.ConfiguredAt = DateTime.UtcNow;
            await _configService.AddRegistrationAsync(registration);

            DisplaySuccess($"Runner '{runnerName}' is online on {target.Host} ({result.Elapsed.TotalSeconds:0}s). " +
                $"Check on it any time with: pks agentics runner status {targetLabel}");

            await WarnIfClaudeCredentialVolumeMissingAsync(target, registration.Owner, registration.Project);
            await OfferCredentialForwardingAsync(target);

            return true;
        }

        DisplayError($"Handoff failed: {result.FailureReason}");
        if (!string.IsNullOrEmpty(result.RemoteTmuxOutput))
        {
            _console.MarkupLine("[dim]Remote tmux output:[/]");
            _console.WriteLine(result.RemoteTmuxOutput);
        }
        return false;
    }

    /// <summary>
    /// Phase 5 work item 1 (docs/remote-runner-targets-plan.md): warns when the remote doesn't yet
    /// have the default project-scoped <c>pks-claude-*</c> credential volume -- without it, the
    /// first headless devcontainer spawn there will stall waiting on an interactive Claude OAuth
    /// login nobody is attached to see. Best-effort: a probe failure right after a fresh handoff
    /// (e.g. the target is momentarily busy) is silently skipped rather than treated as an error --
    /// this is advisory, not a gate. Work item 4: this is the "degraded, not broken" path staying
    /// documented in the command's own output -- see also `pks agentics runner status`.
    /// </summary>
    private async Task WarnIfClaudeCredentialVolumeMissingAsync(PKS.Infrastructure.Services.SshTarget target, string owner, string project)
    {
        if (_sshHandoffService == null) return;

        bool? present;
        try { present = await _sshHandoffService.DetectClaudeCredentialVolumeAsync(target, owner, project); }
        catch { return; }

        if (present != false) return;

        _console.WriteLine();
        _console.MarkupLine(
            $"[yellow]No Claude credentials volume found on {target.Host.EscapeMarkup()} for {owner.EscapeMarkup()}/{project.EscapeMarkup()}.[/]");
        _console.MarkupLine(
            "[dim]A headless devcontainer spawn there will stall waiting for an interactive OAuth login. " +
            $"Populate it first: [bold]pks agentics runner claude-login {(target.Label ?? target.Host).EscapeMarkup()}[/][/]");
    }

    /// <summary>
    /// Phase 5 work item 3 (docs/remote-runner-targets-plan.md, decision D3): opt-in, per-file
    /// credential forwarding. The GitHub token and Foundry credentials are offered as two separate
    /// consents -- declining one never silently declines the other -- and each raw already-serialized
    /// value is copied verbatim from this machine's own <c>~/.pks-cli/settings.json</c> rather than
    /// re-serialized (the two files use different JSON naming conventions internally; copying the
    /// raw string is the only way to guarantee the remote's own auth services can still parse it).
    /// Skipped entirely (no prompt at all) when nothing is stored locally to forward, or when the
    /// SSH-handoff/config collaborators weren't injected.
    /// </summary>
    private async Task OfferCredentialForwardingAsync(PKS.Infrastructure.Services.SshTarget target)
    {
        if (_sshHandoffService == null || _configurationService == null) return;

        var githubTokenRaw = await _configurationService.GetAsync("github.auth.token");
        var foundryCredsRaw = await _configurationService.GetAsync("foundry.auth.credentials");
        if (string.IsNullOrEmpty(githubTokenRaw) && string.IsNullOrEmpty(foundryCredsRaw)) return;

        _console.WriteLine();
        if (!_console.Confirm(
                "[cyan]Forward locally stored credentials to this target so it can advertise git:push / Foundry chat capabilities?[/]",
                defaultValue: false))
        {
            // Work item 4: declining is not an error -- the remote just advertises fewer
            // capabilities (git:push / chat-llm's Foundry models won't appear; see
            // AdvertiseCapabilities/GetAvailableCapabilitiesAsync's own credential checks). Say so
            // explicitly rather than leaving the operator to infer it from a missing capability later.
            _console.MarkupLine("[dim]Skipped. This target will run degraded, not broken -- it simply won't advertise git:push or Foundry chat capabilities without these credentials.[/]");
            return;
        }

        var factorEnrolled = _totpStore != null && await _totpStore.IsEnrolledAsync();

        if (!string.IsNullOrEmpty(githubTokenRaw))
            await ForwardOneCredentialAsync(target, "GitHub token", factorEnrolled, "github.auth.token", githubTokenRaw);
        else
            _console.MarkupLine("[dim]No GitHub token stored locally -- nothing to forward for git:push.[/]");

        if (!string.IsNullOrEmpty(foundryCredsRaw))
            await ForwardOneCredentialAsync(target, "Foundry credentials", factorEnrolled, "foundry.auth.credentials", foundryCredsRaw);
        else
            _console.MarkupLine("[dim]No Foundry credentials stored locally -- nothing to forward for Foundry chat.[/]");
    }

    private async Task ForwardOneCredentialAsync(
        PKS.Infrastructure.Services.SshTarget target, string fileLabel, bool factorEnrolled, string configKey, string configValue)
    {
        var prompt = PKS.Infrastructure.Services.Runner.CredentialForwardConsent.BuildPrompt(fileLabel, factorEnrolled);
        if (!_console.Confirm(prompt, defaultValue: false))
            return;

        if (_guard != null)
        {
            try
            {
                await _guard.RequireAsync(new PKS.Infrastructure.Services.Security.ActionRequest(
                    PKS.Infrastructure.Services.Security.ActionIds.RunnerCredentialForward,
                    $"Forward {fileLabel} to {target.Host}"));
            }
            catch (PKS.Infrastructure.Services.Security.ActionGuardDeniedException ex)
            {
                _console.MarkupLine($"[red]Forwarding {fileLabel.EscapeMarkup()} denied:[/] {ex.Message.EscapeMarkup()}");
                return;
            }
        }

        var error = await _sshHandoffService!.ForwardConfigValueAsync(target, configKey, configValue);
        if (error != null)
            _console.MarkupLine($"[red]Failed to forward {fileLabel.EscapeMarkup()}:[/] {error.EscapeMarkup()}");
        else
            _console.MarkupLine($"[green]{fileLabel.EscapeMarkup()} forwarded to {target.Host.EscapeMarkup()} (0600).[/]");
    }

    private static string SanitizeSuggestedRunnerName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Interactive first-run (or --configure) prompt flow (Phase 3, docs/remote-runner-targets-plan.md):
    /// lets the operator pick which capabilities to advertise and which chat models to expose, then
    /// persists the choice as a <see cref="RunnerProfile"/> so every subsequent start is silent. Only
    /// ever called when <c>_console.Profile.Capabilities.Interactive</c> is true (checked by the
    /// caller) -- never blocks a non-interactive console.
    /// </summary>
    private async Task<RunnerProfile> RunInteractiveConfigureAsync(
        AgenticsRunnerRegistration registration,
        Settings settings,
        RunnerExecutionCapabilityStatus? spawnCapabilityStatus,
        CancellationToken ct)
    {
        _console.WriteLine();
        _console.MarkupLine("[bold cyan]Runner configuration[/] [dim](first run for this registration -- re-run any time with --configure)[/]");

        // ── Capabilities ─────────────────────────────────────────────────────────────────
        // Spectre.Console 0.47's MultiSelectionPrompt has no per-item "disabled" concept, so a
        // capability the phase-1 probe says is unavailable right now is shown as an informational
        // line above the prompt (with the probe's reason) instead of as an unselectable choice --
        // it simply isn't offered as a choice at all. chat-llm:v1 / git:push / git-distribute are
        // always offered: their real availability is nuanced (resolved per-model / per-credential)
        // and is re-checked live every poll regardless of what the operator selects here (see
        // ComputeCapabilitiesAsync's capabilityOverride intersection).
        var dockerAvailable = settings.InProcess || (spawnCapabilityStatus?.DockerAvailable ?? false);
        var availableCapabilities = new List<string>();
        if (settings.InProcess || dockerAvailable)
            availableCapabilities.Add("alp_operator");
        if (dockerAvailable)
        {
            availableCapabilities.Add(ChatSessionCapability);
            availableCapabilities.Add(DevAgentSessionCapability);
        }
        availableCapabilities.Add("chat-llm:v1");
        availableCapabilities.Add("git:push");
        availableCapabilities.Add("git-distribute");

        if (!dockerAvailable && !settings.InProcess)
        {
            var reason = spawnCapabilityStatus?.Reason ?? "Docker availability check did not run";
            _console.MarkupLine($"[dim]alp_operator / {ChatSessionCapability} / {DevAgentSessionCapability} are unavailable right now ({reason.EscapeMarkup()}) and are not offered below.[/]");
        }

        var capabilityPrompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]Tick the capabilities this runner should advertise (space to toggle, enter to confirm):[/]")
            .Required()
            .AddChoices(availableCapabilities);

        var previouslySelectedCapabilities = registration.Profile?.Capabilities;
        foreach (var choice in availableCapabilities)
        {
            // Pre-tick from the existing profile if there is one; otherwise default to "everything
            // currently available" so a first-run operator who just hits enter gets today's
            // no-profile (auto) behavior.
            if (previouslySelectedCapabilities is null ||
                previouslySelectedCapabilities.Contains(choice, StringComparer.OrdinalIgnoreCase))
            {
                capabilityPrompt.Select(choice);
            }
        }

        var selectedCapabilities = _console.Prompt(capabilityPrompt);

        // ── Chat models ──────────────────────────────────────────────────────────────────
        List<string> selectedModels = new();
        string? defaultModel = null;
        IReadOnlyList<string> availableModels = Array.Empty<string>();
        try
        {
            availableModels = await _chatProviderFactory.ListAvailableModelsAsync(ct);
        }
        catch
        {
            // No resolvable chat provider at all (no Foundry session, no stored/env key) --
            // fall through with an empty list and skip the chat-model prompts below.
        }

        if (availableModels.Count == 0)
        {
            _console.MarkupLine("[dim]No chat models currently resolve on this machine (no Foundry session / API key configured) -- skipping chat model selection.[/]");
        }
        else
        {
            var modelPrompt = new MultiSelectionPrompt<string>()
                .Title("[cyan]Tick the chat models this runner should expose for chat-llm:v1 jobs (space to toggle, enter to confirm):[/]")
                .Required()
                .AddChoices(availableModels);

            var previouslySelectedModels = registration.Profile?.ChatModels;
            foreach (var choice in availableModels)
            {
                if (previouslySelectedModels is null ||
                    previouslySelectedModels.Contains(choice, StringComparer.OrdinalIgnoreCase))
                {
                    modelPrompt.Select(choice);
                }
            }

            selectedModels = _console.Prompt(modelPrompt);

            if (selectedModels.Count == 1)
            {
                defaultModel = selectedModels[0];
            }
            else if (selectedModels.Count > 1)
            {
                var previousDefault = registration.Profile?.DefaultChatModel;
                var defaultPrompt = new SelectionPrompt<string>()
                    .Title("[cyan]Pick the default chat model (used when a request doesn't specify one):[/]")
                    .AddChoices(selectedModels);
                if (previousDefault != null && selectedModels.Contains(previousDefault, StringComparer.OrdinalIgnoreCase))
                {
                    // SelectionPrompt has no direct "pre-select" API like MultiSelectionPrompt.Select --
                    // reorder so the previous default is offered first instead.
                    defaultPrompt = new SelectionPrompt<string>()
                        .Title("[cyan]Pick the default chat model (used when a request doesn't specify one):[/]")
                        .AddChoices(selectedModels.OrderByDescending(m =>
                            string.Equals(m, previousDefault, StringComparison.OrdinalIgnoreCase)));
                }
                defaultModel = _console.Prompt(defaultPrompt);
            }
        }

        var profile = new RunnerProfile
        {
            Capabilities = selectedCapabilities,
            ChatModels = selectedModels.Count > 0 ? selectedModels : null,
            DefaultChatModel = defaultModel,
            // Labels/SshTargetLabel are untouched by this flow (Phase 3 doesn't prompt for them) --
            // carry forward whatever was already persisted so a reconfigure doesn't silently drop
            // a Phase-4 SSH-target assignment or an operator-set label list.
            Labels = registration.Profile?.Labels,
            SshTargetLabel = registration.Profile?.SshTargetLabel,
            ConfiguredAt = DateTime.UtcNow,
        };

        _console.MarkupLine($"[green]Configuration saved.[/] Capabilities: [cyan]{string.Join(", ", selectedCapabilities)}[/]");
        if (selectedModels.Count > 0)
            _console.MarkupLine($"Chat models: [cyan]{string.Join(", ", selectedModels)}[/] (default: [cyan]{defaultModel}[/])");
        _console.WriteLine();

        return profile;
    }

    /// <summary>
    /// Computes the capability strings this runner instance supports.
    /// "alp_operator"    — always present (can run vibecast/claude jobs).
    /// "chat-session:v1" — always present alongside "alp_operator": Kind A chat Jobs
    ///                     (external/alp-spec/2026-03-30-draft/spec/13-chat.md) spawn the exact same
    ///                     Operator/devcontainer path as any other Station Job, so any runner that can
    ///                     do that can also open a Kind A chat session -- nothing extra to check for.
    /// "devcontainer-session:v1" — always present alongside "chat-session:v1", same reasoning: a
    ///                     devcontainer-hosted sub-agent session (plan `snappy-wandering-mochi`) is the
    ///                     same Operator/devcontainer spawn path, just without the Chat Channel dial.
    /// "chat-llm:v1"     — present when either an explicit local chat-llm backend has been configured
    ///                     (<c>--chat-llm-backend-url</c> / CHAT_LLM_BACKEND_URL, forwarded to verbatim)
    ///                     or AgentChatProviderFactory can resolve <c>chatLlmModelId</c> to a usable
    ///                     provider (a stored Foundry session from `pks foundry init`, or an
    ///                     agent.models.* / ANTHROPIC_API_KEY / AZURE_OPENAI_API_KEY credential —
    ///                     the same resolution `pks agent` already uses). Kind B chat Jobs are bare (no
    ///                     devcontainer): the runner forwards chat-completions turns to whichever of
    ///                     these it resolved, so declaring the capability without either would let the
    ///                     Server dispatch a Job this runner can't actually serve.
    /// "git:push"       — present when a GitHub token is stored (so git push won't prompt).
    /// "git-distribute" — present alongside "git:push" (source distribution needs the same credentials).
    /// </summary>
    /// <summary>
    /// Computes the capability strings this runner advertises on its next poll (Defect A fix,
    /// docs/remote-runner-targets-plan.md Phase 1). <c>internal</c> so tests can call it directly
    /// against a mocked <see cref="IRunnerExecutionCapabilityProbe"/> without driving the whole
    /// poll loop -- see tests/Services/Runner/RunnerCapabilityProbeTests.cs.
    /// </summary>
    /// <param name="capabilityOverride">
    /// <see cref="RunnerProfile.Capabilities"/> from the persisted profile (Phase 3), or null.
    /// When set, the live-probed set below is intersected with this list -- an operator override
    /// can only narrow what gets advertised, never force-advertise something the probe says this
    /// machine cannot actually serve right now. Optional/trailing so the three existing Phase 1
    /// gating tests (which call this with exactly 4 named args) keep compiling unchanged.
    /// </param>
    internal async Task<List<string>> ComputeCapabilitiesAsync(
        bool inProcess, string? chatLlmBackendUrl, string chatLlmModelId, CancellationToken ct,
        List<string>? capabilityOverride = null,
        bool spawnEnabled = true)
    {
        var caps = new List<string>();

        // Devcontainer-spawn capabilities are gated on Docker actually being reachable, not
        // advertised unconditionally. --inprocess never spawns a devcontainer at all
        // (ExecuteInProcessAsync runs in a git worktree), so the probe is skipped entirely in
        // that mode -- both to avoid pinging Docker every poll cycle for no reason, and because
        // RunnerCapabilityProbeTests asserts the probe is never invoked when inProcess=true.
        //
        // spawnEnabled is the STRUCTURAL half of the gate (the call site passes
        // `settings.InProcess || credentialServer != null`). The GitCredentialServer and the
        // GitHub device-code preflight are both one-shot startup work; when spawn mode was
        // unavailable at startup neither ran, so ExecuteSpawnModeAsync can NEVER succeed for the
        // rest of this process even if dockerd comes back and the live probe turns green. Without
        // this term the runner would advertise alp_operator again, be handed every station job,
        // and decline 100% of them at the pre-claim check in PollAndDispatchOnceAsync -- leaving
        // those jobs queued forever while both server-side safety nets (noOnlineRunnerWarning and
        // StaleJobMonitor) see a capable-looking online runner and stay silent. Restart the runner
        // to pick Docker back up.
        var dockerAvailable = false;
        if (!inProcess && spawnEnabled)
        {
            var status = await _capabilityProbe.GetStatusAsync(ct);
            dockerAvailable = status.DockerAvailable;
        }

        // alp_operator: the only capability string already advertised unconditionally by every
        // field runner AND semantically meaning "I can run an operator + devcontainer" (see the
        // plan's capability table) -- --inprocess satisfies this without Docker.
        if (inProcess || dockerAvailable)
            caps.Add("alp_operator");

        // chat-session:v1 (Kind A chat) and devcontainer-session:v1 both run inside a spawned
        // devcontainer, so --inprocess does NOT retain them (decision D1) -- only a real Docker
        // daemon does.
        if (dockerAvailable)
        {
            caps.Add(ChatSessionCapability);
            caps.Add(DevAgentSessionCapability);
        }

        if (!string.IsNullOrEmpty(chatLlmBackendUrl))
        {
            caps.Add("chat-llm:v1");
        }
        else
        {
            try
            {
                await _chatProviderFactory.ResolveAsync(chatLlmModelId, ct);
                caps.Add("chat-llm:v1");
            }
            catch
            {
                // No authorized backend for chatLlmModelId (no Foundry session, no stored/env key) --
                // don't advertise a capability this runner can't actually serve.
            }
        }

        // Always ask the auth service directly rather than assuming a preflight established the
        // invariant. The startup GitHub device-code preflight is skipped entirely when spawn mode
        // is unavailable (see ExecuteAsync -- `!settings.InProcess && spawnModeAvailable`), so on a
        // Docker-less machine no token is ever obtained or refreshed. Hardcoding `true` here would
        // advertise git:push / git-distribute unconditionally; ExecuteGitPushJobAsync CLAIMS the job
        // (POST runners/generate-jitconfig) before PrepareGitCredentialsAsync ever looks at the
        // token, so the job would be claimed and then fail on push -- exactly the claim-then-fail
        // defect the honest-capabilities work exists to eliminate.
        var hasGitCredentials = await _githubAuth.IsAuthenticatedAsync();

        // Also accept credentials served via a working GIT_ASKPASS in the environment
        // (e.g. VS Code devcontainer injects a gho_ token via socket).
        if (!hasGitCredentials)
        {
            var askpass = Environment.GetEnvironmentVariable("GIT_ASKPASS");
            if (!string.IsNullOrEmpty(askpass) && File.Exists(askpass))
            {
                try
                {
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(3));
                    var psi = new ProcessStartInfo(askpass) { RedirectStandardOutput = true, RedirectStandardError = true };
                    psi.ArgumentList.Add("Password:");
                    psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                    var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var output = await proc.StandardOutput.ReadToEndAsync(cts2.Token);
                        await proc.WaitForExitAsync(cts2.Token);
                        hasGitCredentials = proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
                    }
                }
                catch
                {
                    // GIT_ASKPASS not functional — ignore
                }
            }
        }

        if (hasGitCredentials)
            caps.Add("git:push");

        // Source distribution: requires git credentials (clone source + push target).
        // pks-cli handles the target-side credentials internally (e.g. ADO PAT setup).
        if (hasGitCredentials)
            caps.Add("git-distribute");

        // Operator override (RunnerProfile.Capabilities, Phase 3): narrow, never widen. Re-applied
        // every poll (not just once at configure time) so a capability the operator opted into stays
        // honest if the underlying probe later says it's unavailable (e.g. Docker stops mid-run).
        if (capabilityOverride is { Count: > 0 })
            caps = caps.Where(c => capabilityOverride.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

        return caps;
    }

    /// <summary>
    /// Asks the server whether the project this runner is registered for needs GitHub
    /// access. Falls back to <c>true</c> on any error so we keep the historical behavior
    /// of prompting (better to ask once than fail a job mid-clone).
    /// </summary>
    private async Task<bool> ProjectRequiresGitHubAsync(AgenticsRunnerRegistration registration)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", registration.Token);
            var url = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/repo-info";
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return true;
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("requiresGitHub", out var v) && v.GetBoolean();
        }
        catch
        {
            return true;
        }
    }

    private async Task<RunnerJob?> PollForJobAsync(AgenticsRunnerRegistration registration, IReadOnlyList<string> capabilities, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var response = await client.PostAsJsonAsync(
            $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs",
            new { capabilities },
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        var pollResponse = JsonSerializer.Deserialize<PollResponse>(json, JsonOptions);
        if (pollResponse?.Jobs == null || pollResponse.Jobs.Count == 0)
            return null;

        return pollResponse.Jobs[0];
    }

    /// <summary>
    /// One poll-loop iteration: recompute capabilities (behind the probe's 60s memo -- Phase 1,
    /// docs/remote-runner-targets-plan.md), poll the server for a job, and dispatch it. Extracted
    /// out of <see cref="ExecuteAsync"/>'s <c>while</c> loop so it can be exercised directly in
    /// tests (see tests/Commands/Agentics/AgenticsRunnerDegradedStartTests.cs) without driving the
    /// real infinite loop / CancelKeyPress plumbing. Returns the number of jobs this iteration
    /// completed or dispatched (0 or 1) -- mirrors the original inline <c>jobsProcessed++</c>
    /// bookkeeping (the spawn-mode branch never incremented it either; unchanged here).
    /// </summary>
    internal async Task<int> PollAndDispatchOnceAsync(
        AgenticsRunnerRegistration registration,
        Settings settings,
        GitCredentialServer? credentialServer,
        string? chatLlmBackendUrl,
        string? chatLlmBackendKey,
        string chatLlmModelId,
        bool chatLlmVerbose,
        CancellationToken ct)
    {
        // spawnEnabled: only advertise devcontainer-spawn capabilities we are structurally able to
        // serve. credentialServer is constructed once at startup and only when spawn mode was
        // available then; a null one means ExecuteSpawnModeAsync can never run in this process, so
        // the live Docker probe must not be allowed to re-add alp_operator / chat-session:v1 /
        // devcontainer-session:v1 behind its back.
        var capabilities = await ComputeCapabilitiesAsync(
            settings.InProcess, chatLlmBackendUrl, chatLlmModelId, ct, registration.Profile?.Capabilities,
            spawnEnabled: settings.InProcess || credentialServer != null);
        if (_lastLoggedCapabilities == null || !_lastLoggedCapabilities.SequenceEqual(capabilities))
        {
            _console.MarkupLine($"[dim]Runner capabilities: {string.Join(", ", capabilities)}[/]");
            _lastLoggedCapabilities = capabilities;
        }

        var jobsProcessed = 0;
        var job = await PollForJobAsync(registration, capabilities, ct);

        if (job != null)
        {
            _console.MarkupLine($"[green]Job received:[/] {job.Id}");
            if (!string.IsNullOrEmpty(job.AgentDef?.Traceparent))
                _console.MarkupLine($"[dim]trace: {job.AgentDef.Traceparent}[/]");

            // Restore parent trace context so this runner span is a child of the
            // server-side span that dispatched the job (visible in Aspire dashboard).
            System.Diagnostics.ActivityContext parentCtx = default;
            if (!string.IsNullOrEmpty(job.AgentDef?.Traceparent))
                System.Diagnostics.ActivityContext.TryParse(job.AgentDef.Traceparent, null, isRemote: true, out parentCtx);
            using var jobActivity = _activitySource.StartActivity(
                "runner.execute_job", System.Diagnostics.ActivityKind.Consumer, parentCtx);
            jobActivity?.SetTag("agentics.job_id", job.Id);
            jobActivity?.SetTag("agentics.task_id", job.AgentDef?.TaskId);
            jobActivity?.SetTag("agentics.assembly_line_id", job.AgentDef?.AssemblyLineId);
            jobActivity?.SetTag("agentics.mode", settings.InProcess ? "inprocess" : "spawn");

            if (job.AgentDef?.JobType == "git_push" && job.AgentDef?.GitPushPayload != null)
            {
                await ExecuteGitPushJobAsync(registration, job, settings, ct);
                jobsProcessed++;
                _console.MarkupLine($"[green]Git push job completed.[/]");
            }
            else if (job.AgentDef?.JobType == "git_distribute" && job.AgentDef?.DistributePayload != null)
            {
                await ExecuteGitDistributeJobAsync(registration, job, settings, ct);
                jobsProcessed++;
                _console.MarkupLine($"[green]Git distribute job completed.[/]");
            }
            else if (job.AgentDef?.JobType == "chat_llm")
            {
                // Kind B (chat-llm:v1, external/alp-spec/2026-03-30-draft/spec/13-chat.md): a bare
                // Job -- no devcontainer, no Operator. The Runner itself dials the Chat Channel and
                // forwards chat-completions turns to the locally configured backend. This holds a
                // long-lived connection for the entire chat session, so it runs on its own
                // background Task instead of being awaited here -- otherwise an open chat tab would
                // starve the poll loop and block every other job (e.g. an ALP floor dispatch) for
                // as long as the chat stays open. jobActivity's span is started fresh inside the
                // background task so its lifetime matches the actual execution, not this iteration.
                var chatJob = job;
                var chatTraceparent = chatJob.AgentDef?.Traceparent;
                var chatJobTask = Task.Run(async () =>
                {
                    System.Diagnostics.ActivityContext chatParentCtx = default;
                    if (!string.IsNullOrEmpty(chatTraceparent))
                        System.Diagnostics.ActivityContext.TryParse(chatTraceparent, null, isRemote: true, out chatParentCtx);
                    using var chatJobActivity = _activitySource.StartActivity(
                        "runner.execute_job", System.Diagnostics.ActivityKind.Consumer, chatParentCtx);
                    chatJobActivity?.SetTag("agentics.job_id", chatJob.Id);
                    chatJobActivity?.SetTag("agentics.task_id", chatJob.AgentDef?.TaskId);
                    chatJobActivity?.SetTag("agentics.assembly_line_id", chatJob.AgentDef?.AssemblyLineId);
                    chatJobActivity?.SetTag("agentics.mode", "chat_llm");

                    try
                    {
                        await ExecuteChatLlmJobAsync(registration, chatJob, chatLlmBackendUrl, chatLlmBackendKey, chatLlmModelId, chatLlmVerbose, ct);
                        _console.MarkupLine($"[green]Chat-llm job completed.[/] {chatJob.Id}");
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown while a chat session was in flight -- DrainRunningJobTasksAsync already waits for us.
                    }
                    catch (Exception ex)
                    {
                        _console.MarkupLine($"[red]Chat-llm job failed:[/] {chatJob.Id} {ex.Message.EscapeMarkup()}");
                    }
                }, ct);
                TrackRunningJob(chatJobTask);
                jobsProcessed++;
            }
            else if (settings.InProcess)
            {
                await ExecuteInProcessAsync(registration, job, settings, ct);
                jobsProcessed++;
                _console.MarkupLine($"[green]InProcess job completed.[/]");
            }
            else
            {
                // Client-side pre-claim refusal (Defect A/D2 fix, docs/remote-runner-targets-plan.md
                // Phase 1). THIS IS PERMANENT INFRASTRUCTURE, NOT TRANSITIONAL -- do not delete once
                // Phase 2 ships server-side `needs` filtering. Per decision D2, Phase 2 only teaches
                // `dispatchStationJob` to emit `needs: ['alp_operator']`; the ~13 direct-`createRun`
                // paths (task-create.ts, run/actions.ts, review/submit/actions.ts, mcp/operations.ts,
                // runs/route.ts, distribution-store.ts, ...) keep `needs: []` forever and stay
                // claimable by ANY runner regardless of advertised capabilities -- and every
                // pre-Phase-2 field runner never advertised `needs` either. This refusal is the only
                // thing stopping a Docker-less runner from claiming (POST
                // .../runners/generate-jitconfig) an ordinary station job it has no way to run and
                // then failing it. The poll endpoint itself never claims (jobs/route.ts only reads),
                // so simply never calling generate-jitconfig here leaves the job `queued` for a
                // runner that can actually serve it.
                //
                // credentialServer is null whenever spawn mode was unavailable at startup (see
                // ExecuteAsync); re-check the live probe too so a Docker daemon that dies mid-run is
                // caught even when credentialServer was constructed.
                var spawnStatus = credentialServer != null ? await _capabilityProbe.GetStatusAsync(ct) : null;
                if (credentialServer == null || spawnStatus is not { DockerAvailable: true })
                {
                    var reason = spawnStatus?.Reason ?? "devcontainer spawning was unavailable at startup";
                    if (_declinedSpawnJobIds.Add(job.Id))
                    {
                        _console.MarkupLine($"[grey]Declining job {job.Id.EscapeMarkup()} — devcontainer spawning unavailable ({reason.EscapeMarkup()}); leaving it queued for a capable runner.[/]");
                    }
                }
                else
                {
                    await ExecuteSpawnModeAsync(registration, job, settings, credentialServer, ct);
                }
            }
        }
        else
        {
            if (settings.Verbose)
                _console.MarkupLine($"[dim]{DateTime.UtcNow:HH:mm:ss} No jobs available, waiting {settings.PollingInterval}s...[/]");
        }

        _pollCounter.Add(1,
            new KeyValuePair<string, object?>("owner", registration.Owner),
            new KeyValuePair<string, object?>("project", registration.Project),
            new KeyValuePair<string, object?>("result", job != null ? "job_found" : "empty"));

        return jobsProcessed;
    }

    /// <summary>
    /// Full lifecycle for devcontainer spawn mode:
    ///   claim → in_progress → spawn container → exec agent → completed
    /// </summary>
    private async Task ExecuteSpawnModeAsync(
        AgenticsRunnerRegistration registration,
        RunnerJob job,
        Settings settings,
        GitCredentialServer credentialServer,
        CancellationToken ct)
    {
        // Start AgenticsProxy + OtlpProxy. Per ADR 0003, socket dirs are stable per-task so the
        // warm container's bind mount remains valid across job cycles within the same task.
        // Falls back to per-job dirs for legacy/jobless dispatches with no taskId.
        var taskKeyForSockets = job.AgentDef?.TaskId is { Length: > 0 } tid ? $"task-{tid}" : null;
        var agenticsProxyOptions = await BuildAgenticsProxyOptionsAsync(job, ct);
        var agenticsSocketDir = taskKeyForSockets != null
            ? Path.Combine(Path.GetTempPath(), $"pks-agentics-{taskKeyForSockets}")
            : null;
        await using var agenticsProxy = await AgenticsProxy.StartAsync(
            agenticsProxyOptions, _foundryAuthService,
            createSocket: true,
            socketDirOverride: agenticsSocketDir,
            ct: ct);
        _console.MarkupLine($"[dim]AgenticsProxy listening on port {agenticsProxy.Port} ({agenticsProxyOptions.AllowedHosts.Count} allowed host(s))[/]");

        // OtlpProxy: same per-task stable dir pattern. Container-side TCP→Unix bridge (in start.sh)
        // forwards localhost:4318 to /var/run/pks-otlp/otlp.sock.
        var otlpAnalysisBaseUrl = $"{new Uri(registration.Server).Scheme}://{new Uri(registration.Server).Host}{(new Uri(registration.Server).IsDefaultPort ? "" : $":{new Uri(registration.Server).Port}")}";
        var otlpSocketDir = taskKeyForSockets != null
            ? Path.Combine(Path.GetTempPath(), $"pks-otlp-{taskKeyForSockets}")
            : null;
        await using var otlpProxy = await OtlpProxy.StartAsync(
            analysisBaseUrl: otlpAnalysisBaseUrl,
            jobId: job.Id,
            createSocket: true,
            socketDirOverride: otlpSocketDir,
            ct: ct);
        _console.MarkupLine($"[dim]OtlpProxy listening on host port {otlpProxy.Port} (socket: {otlpProxy.SocketPath})[/]");

        // Per-job git proxy for marketplace plugin clones. The container's
        // `git config insteadOf` lines route private marketplace URLs through
        // this port; the daemon injects the right Bearer per upstream so the
        // marketplace token never enters the container.
        await using var gitProxy = new PKS.Infrastructure.Services.GitProxy.GitProxyDaemon();
        var hasMarketplaceTokens = job.AgentDef?.MarketplaceTokens?.Count > 0;
        if (hasMarketplaceTokens)
        {
            await gitProxy.StartAsync(ct: ct);
            foreach (var mp in job.AgentDef!.MarketplaceTokens!)
            {
                // Empty Bearer is valid: public marketplaces that only need URL
                // rewriting (canonical → live tunnel in dev) register without
                // auth — the proxy forwards untouched. See GitProxyDaemon.
                if (string.IsNullOrEmpty(mp.Authority)) continue;
                var upstream = !string.IsNullOrEmpty(mp.UpstreamPrefix) ? mp.UpstreamPrefix! : $"https://{mp.Authority}/";
                gitProxy.Register(upstream, new PKS.Infrastructure.Services.GitProxy.StaticBearerTokenSource(mp.Bearer ?? string.Empty));
            }
            _console.MarkupLine($"[dim]GitProxy listening on port {gitProxy.Port} ({job.AgentDef!.MarketplaceTokens!.Count} marketplace authority/ies)[/]");
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";

        // 1. Claim the job so findQueuedJobs stops returning it
        _console.MarkupLine($"[cyan]Claiming job {job.Id}...[/]");
        var claimResp = await client.PostAsJsonAsync(
            $"{baseUrl}/runners/generate-jitconfig",
            new { jobId = job.Id, name = registration.Name ?? "spawn-runner" },
            ct);

        if (!claimResp.IsSuccessStatusCode)
        {
            _console.MarkupLine($"[yellow]Could not claim job {job.Id} (already claimed?), skipping.[/]");
            return;
        }

        var claimJson = await claimResp.Content.ReadAsStringAsync(ct);
        var claimData = JsonSerializer.Deserialize<JsonElement>(claimJson, JsonOptions);
        var runId = claimData.GetProperty("runId").GetString()!;

        // 2. Check for a warm container via Docker labels (survives pks-cli restarts).
        // Per ADR 0003 the fingerprint includes taskId, so each task gets its own dedicated
        // container. taskId may be empty for ad-hoc/legacy dispatches → falls back to project-scoped.
        var storedToken = await _githubAuth.GetStoredTokenAsync();
        var spawnOptions = BuildSpawnOptions(job, credentialServer.SocketPath, registration, storedToken?.AccessToken);
        var taskId = job.AgentDef?.TaskId;
        spawnOptions.AgenticsProxySocketDir = agenticsProxy.SocketDir;
        spawnOptions.OtlpProxySocketDir = otlpProxy.SocketDir;

        // Patch the claude config volume to a stable name so credentials persist. Scope (task /
        // project / runner) controls how broadly the credentials are shared — see ADR 0004.
        var (patchedFiles, claudeVolumeName) = PatchDevcontainerVolumes(
            spawnOptions.InlineDevcontainerFiles, registration.Owner, registration.Project,
            taskId, job.AgentDef?.ClaudeCredentialsScope);
        spawnOptions.InlineDevcontainerFiles = patchedFiles;
        var claudeScope = job.AgentDef?.ClaudeCredentialsScope ?? "project";
        _console.MarkupLine($"[dim]Claude credentials volume: [cyan]{claudeVolumeName.EscapeMarkup()}[/] (scope: {claudeScope})[/]");

        // Fingerprint = (project + task + devcontainer config + template). Per ADR 0003 each task
        // gets its own container; retries/continues of the SAME task reuse it. Different stations
        // of the same task swap plugins inside the container, not by recreating it.
        var fingerprint = ComputeDevcontainerFingerprint(
            registration.Owner,
            registration.Project,
            taskId,
            spawnOptions.InlineDevcontainerFiles,
            spawnOptions.DevcontainerTemplate);

        // Stamp the container with labels so we can rediscover warm containers across job
        // dispatches inside this runner process. runner-instance binds the container's reusability
        // to this runner-process — see ADR 0002.
        spawnOptions.IdLabels = new Dictionary<string, string>
        {
            ["pks.agentics.owner"] = registration.Owner,
            ["pks.agentics.project"] = registration.Project,
            ["pks.agentics.fingerprint"] = fingerprint,
            ["pks.agentics.runner-instance"] = _runnerInstanceId,
        };
        if (!string.IsNullOrEmpty(taskId))
            spawnOptions.IdLabels["pks.agentics.task"] = taskId;

        // Plugins are no longer a Docker volume per ADR 0003 — they're files inside the container
        // at $HOME/.alp/plugins/, swapped on every job dispatch (after the container is up).
        var hasPlugins = job.AgentDef?.Plugins?.Count > 0;
        var hasAgents = job.AgentDef?.Agents?.Count > 0;
        var pluginContainerPaths = new List<string>();

        string containerId;
        // Require BOTH fingerprint AND runner-instance to match — see ADR 0002.
        var warmId = await FindContainerByLabelsAsync(
            $"pks.agentics.fingerprint={fingerprint}",
            $"pks.agentics.runner-instance={_runnerInstanceId}");
        if (warmId != null)
        {
            _console.MarkupLine($"[green]Reusing warm container:[/] {warmId[..12]} (same runner instance, devcontainer unchanged)");
            containerId = warmId;
        }
        else
        {
            // Detect orphaned containers from a previous runner instance — log a hint, don't auto-remove.
            var orphanId = await FindContainerByLabelAsync($"pks.agentics.fingerprint={fingerprint}");
            if (orphanId != null)
            {
                _console.MarkupLine($"[grey]Skipping warm container {orphanId[..12]} from previous runner instance — run [italic]pks agentics runner cleanup[/] to remove orphans[/]");
            }

            // 3. Spawn the devcontainer (clones repo, runs devcontainer up)
            var logPath = Path.Combine(Path.GetTempPath(), $"pks-runner-{job.Id}-build.log");
            _console.MarkupLine($"[dim]Build log (streaming): {logPath}[/]");
            _console.MarkupLine($"[dim]  Windows: Get-Content -Wait \"{logPath}\"[/]");
            _console.MarkupLine($"[dim]  Linux:   tail -f \"{logPath}\"[/]");
            spawnOptions.BuildLogPath = logPath;

            // Background tailer streams the build log to the server so the UI can render it
            // live inside the "Picked up by …" timeline card.
            using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tailTask = TailBuildLogAsync(client, baseUrl, runId, job.Id, logPath, tailCts.Token);

            // Map spawner progress messages to coarse stages and POST one heartbeat per
            // transition (de-duped) so the UI can show "cloning repo → pulling image → …".
            string? lastStage = null;
            var spawnResult = await _spawnerService.SpawnLocalAsync(spawnOptions, msg =>
            {
                _console.MarkupLine($"[dim]{msg.EscapeMarkup()}[/]");
                var stage = MapProgressMessageToStage(msg);
                if (stage != null && stage != lastStage)
                {
                    lastStage = stage;
                    _ = PostJobProgressAsync(client, baseUrl, runId, job.Id, stage, msg, ct);
                }
            });

            // Stop tailing — the spawn is done so the build log won't grow further.
            tailCts.Cancel();
            try { await tailTask; } catch { /* tail flushes on cancel */ }

            if (!spawnResult.Success || spawnResult.ContainerId == null)
            {
                var errorMsg = string.Join("; ", new[] { spawnResult.Message }
                    .Concat(spawnResult.Errors)
                    .Where(s => !string.IsNullOrEmpty(s)));
                _console.MarkupLine($"[red]Devcontainer spawn failed:[/] {spawnResult.Message.EscapeMarkup()}");

                // Second hint line for the mid-job-Docker-death case (docs/remote-runner-targets-plan.md
                // Phase 1): re-probe rather than assume -- most spawn failures are build/config errors
                // unrelated to Docker itself, so only say "Docker died" when the probe agrees right now.
                var postFailureStatus = await _capabilityProbe.GetStatusAsync(ct);
                if (!postFailureStatus.DockerAvailable)
                {
                    _console.MarkupLine($"[yellow]Hint:[/] [dim]Docker is no longer reachable ({postFailureStatus.Reason.EscapeMarkup()}) — this looks like Docker went away mid-job rather than a build/config error. Future jobs will be left queued for a capable runner instead of claimed and failed.[/]");
                }

                await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
                await ReportJobResultAsync(registration, "failed", errorMsg, ct);
                return;
            }

            containerId = spawnResult.ContainerId;
            _console.MarkupLine($"[green]Container ready:[/] {containerId[..12]} (labelled for reuse)");
            await PostJobProgressAsync(client, baseUrl, runId, job.Id, "provisioning_done", "container ready", ct);
        }

        // 4. PATCH to in_progress now that the container is running
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

        // 4b. Install plugins INSIDE the container at $HOME/.alp/plugins (per ADR 0003).
        // Clean slate every job so station transitions get the right tool belt without
        // recreating the container. No Docker volume → no "volume in use" cleanup races.
        if (hasPlugins || hasAgents)
        {
            // sh -c only (ExecInContainerAsync wraps with /bin/sh -c). $HOME
            // expands inside the inner shell which is fine. POSIX shell glob
            // /.* would match . and .. which mkdir refuses, hence the redirect.
            await _spawnerService.ExecInContainerAsync(containerId,
                "rm -rf $HOME/.alp/plugins/* $HOME/.alp/plugins/.* 2>/dev/null; mkdir -p $HOME/.alp/plugins",
                timeoutSeconds: 30);

            if (hasPlugins)
            {
                // Route every registered marketplace authority through the
                // host-side git proxy. We use the Docker bridge gateway IP
                // (172.17.0.1) rather than host.docker.internal because the
                // latter only resolves on Docker Desktop, not on plain Linux
                // Docker — and the agentics runner runs on Linux Docker. Same
                // pattern as the ADO proxy (ClaudeSpawnCommand.cs:164).
                const string hostGatewayIp = "172.17.0.1";
                if (hasMarketplaceTokens)
                {
                    foreach (var mp in job.AgentDef!.MarketplaceTokens!)
                    {
                        if (string.IsNullOrEmpty(mp.Authority)) continue;
                        var rewriteTo = $"http://{hostGatewayIp}:{gitProxy.Port}/";
                        var rewriteFrom = $"https://{mp.Authority}/";
                        // Warm-container reuse: a previous job installed a rule with
                        // a stale (dead) proxy port for the same insteadOf target.
                        // Git picks the first matching url.<rewriteTo>.insteadOf and
                        // tries to connect — fails. Strip every prior rule that maps
                        // FROM the same canonical URL before adding the new one.
                        // sh -c only — outer wraps it.
                        await _spawnerService.ExecInContainerAsync(containerId,
                            "git config --global --get-regexp '^url\\..*\\.insteadOf$' 2>/dev/null | " +
                            $"awk -v t='{rewriteFrom}' '$2==t {{print $1}}' | " +
                            "while read k; do git config --global --unset-all \"$k\"; done; true",
                            timeoutSeconds: 10);
                        await _spawnerService.ExecInContainerAsync(containerId,
                            $"git config --global url.'{rewriteTo}'.insteadOf '{rewriteFrom}'",
                            timeoutSeconds: 10);
                        _console.MarkupLine($"  [dim]git rewrite: {rewriteFrom} → {rewriteTo}[/]");
                    }
                }

                // Plugin clone failures cascade into hard-to-diagnose downstream
                // problems (agents that depend on the plugin look for files
                // that aren't there, Claude wanders trying to sign in to fix
                // them, etc). Fail the job immediately so the operator sees the
                // root cause in the timeline instead of a 60-minute timeout.
                var cloneFailures = new List<string>();
                foreach (var plugin in job.AgentDef!.Plugins!)
                {
                    var pluginUrl = plugin.SourceUrl;
                    var pluginName = plugin.Name;
                    if (string.IsNullOrEmpty(pluginUrl) || string.IsNullOrEmpty(pluginName))
                    {
                        cloneFailures.Add($"{pluginName ?? "(unnamed)"}: missing url or name");
                        continue;
                    }
                    var dest = $"$HOME/.alp/plugins/{pluginName}";
                    // sh -c only — outer ExecInContainerAsync handles the shell wrap.
                    var cloneRes = await _spawnerService.ExecInContainerAsync(containerId,
                        $"git clone --depth 1 \"{pluginUrl}\" \"{dest}\" 2>&1",
                        timeoutSeconds: 60);
                    if (cloneRes.ExitCode != 0)
                    {
                        var detail = cloneRes.Output.Trim();
                        _console.MarkupLine($"[red]Failed to clone plugin {pluginName}: {detail.EscapeMarkup()}[/]");
                        cloneFailures.Add($"{pluginName}: {detail}");
                        continue;
                    }
                    pluginContainerPaths.Add(dest);
                    _console.MarkupLine($"  [green]✓[/] cloned {pluginName} → {dest}");
                }

                if (cloneFailures.Count > 0)
                {
                    var summary = $"Plugin clone failed for {cloneFailures.Count} plugin(s): " +
                                  string.Join("; ", cloneFailures.Select(f => f.Length > 200 ? f[..200] + "…" : f));
                    _console.MarkupLine($"[red]{summary.EscapeMarkup()}[/]");
                    _console.MarkupLine("[red]Aborting job — agents may depend on these plugins and would otherwise fail silently.[/]");
                    await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
                    await ReportJobResultAsync(registration, "failed", summary, ct);
                    return;
                }
            }

            if (hasAgents)
            {
                // Mirror the existing layout: a synthetic plugin dir with .claude-plugin/plugin.json
                // and agents/{agentId}.md. Vibecast picks it up as just another plugin.
                // Resolve $HOME upfront so the rest of the calls use absolute paths
                // (CopyFileToContainerAsync sends a TAR archive over the Docker socket and
                // doesn't go through a shell, so no env-var expansion happens there).
                var homeResult = await _spawnerService.ExecInContainerAsync(containerId,
                    "printf %s \"$HOME\"", timeoutSeconds: 10);
                if (!homeResult.Success || string.IsNullOrWhiteSpace(homeResult.Output))
                    throw new InvalidOperationException($"Failed to resolve $HOME in container {containerId}: exit={homeResult.ExitCode}");
                var homeDir = homeResult.Output.Trim();
                var agentDir = $"{homeDir}/.alp/plugins/agents-{job.Id}";
                var pluginJson = $$"""{"name":"task-agents","version":"1.0.0","description":"Agents for job {{job.Id}}"}""";
                await _spawnerService.CopyFileToContainerAsync(containerId,
                    $"{agentDir}/.claude-plugin/plugin.json",
                    System.Text.Encoding.UTF8.GetBytes(pluginJson),
                    ct: ct);
                foreach (var agent in job.AgentDef!.Agents!)
                {
                    if (string.IsNullOrEmpty(agent.Id) || string.IsNullOrEmpty(agent.Content)) continue;
                    await _spawnerService.CopyFileToContainerAsync(containerId,
                        $"{agentDir}/agents/{agent.Id}.md",
                        System.Text.Encoding.UTF8.GetBytes(agent.Content),
                        ct: ct);
                }
                pluginContainerPaths.Add(agentDir);
                _console.MarkupLine($"  [green]✓[/] wrote {job.AgentDef.Agents!.Count} agent file(s) → {agentDir}");
            }
        }

        // 5. Determine workspace folder and AGENTIC_SERVER for inside the container
        var repoName = spawnOptions.ProjectName;
        var workspaceFolder = $"/workspaces/{repoName}";

        // localhost on the host is not localhost inside the container.
        // host.docker.internal only resolves on Docker Desktop (mac/win); on plain
        // Linux Docker / dind we use the bridge gateway IP — same pattern as the
        // git proxy's hostGatewayIp above. AGENTICS_CONTAINER_HOST overrides for
        // custom bridge subnets.
        var serverUri = new Uri(registration.Server);
        var containerHostIp = Environment.GetEnvironmentVariable("AGENTICS_CONTAINER_HOST")
            ?? (OperatingSystem.IsLinux() ? "172.17.0.1" : "host.docker.internal");
        var hostForContainer = serverUri.Host is "localhost" or "127.0.0.1"
            ? containerHostIp
            : serverUri.Host;
        var agenticServerForContainer = $"{hostForContainer}:{serverUri.Port}";

        // 6. Set up vibecast working directory in the container.
        // We cannot use /tmp here: the dind devcontainer feature mounts /tmp as
        // tmpfs so the inner Docker daemon has scratch space, and the host
        // daemon's archive endpoint (PUT /containers/{id}/archive) cannot see
        // into tmpfs mounts — it only reads/writes the overlay layer. That
        // makes `docker exec` see files we put in /tmp but the subsequent
        // ExtractArchiveToContainer (used by CopyFileToContainerAsync) returns
        // 404 "Could not find the file …". Stick to $HOME, which is on the
        // overlay and visible to both APIs.
        // ExecInContainerAsync wraps with `/bin/sh -c "..."` internally, so
        // shell variables expand inside the inner shell. We resolve $HOME up
        // front so we can pass an absolute path to the archive endpoint (which
        // does not go through a shell).
        var resolvedHome = await _spawnerService.ExecInContainerAsync(containerId,
            "printf %s \"$HOME\"", timeoutSeconds: 10);
        if (!resolvedHome.Success || string.IsNullOrWhiteSpace(resolvedHome.Output))
        {
            var msg = $"Failed to resolve $HOME in container {containerId[..Math.Min(12, containerId.Length)]} (exit={resolvedHome.ExitCode}): " +
                      $"{(resolvedHome.Error ?? resolvedHome.Output ?? "").Trim()}. " +
                      "Container may be stopped/exited. Try removing the warm container and retrying.";
            _console.MarkupLine($"[red]{msg.EscapeMarkup()}[/]");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
            await ReportJobResultAsync(registration, "failed", msg, ct);
            return;
        }
        var homeDirAbs = resolvedHome.Output.Trim();
        var vibecastHome = $"{homeDirAbs}/.vibecast-jobs/{job.Id}";
        var promptFile = $"{vibecastHome}/initial-prompt.txt";
        var jobPrompt = job.AgentDef?.Prompt ?? "";
        var mkdirRes = await _spawnerService.ExecInContainerAsync(containerId,
            $"mkdir -p {vibecastHome} && test -d {vibecastHome} && echo OK",
            timeoutSeconds: 30);
        if (!mkdirRes.Success || mkdirRes.ExitCode != 0 || !mkdirRes.Output.Contains("OK"))
        {
            var detail = (mkdirRes.Error ?? mkdirRes.Output ?? "").Trim();
            var msg = $"mkdir -p {vibecastHome} failed in container {containerId[..Math.Min(12, containerId.Length)]} (exit={mkdirRes.ExitCode}): {detail}. " +
                      "Container may be stopped/exited, or $HOME may not be writable. Try removing the warm container and retrying.";
            _console.MarkupLine($"[red]{msg.EscapeMarkup()}[/]");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
            await ReportJobResultAsync(registration, "failed", msg, ct);

            return;
        }

        // Local helper to write arbitrary file content into the container.
        // Uses the Docker `extract archive` endpoint (TAR over the daemon socket) via
        // CopyFileToContainerAsync — no argv size limit, no shell quoting concerns.
        // The previous base64-in-bash-argv transport silently truncated above kernel ARG_MAX
        // (~2 MB) and produced empty prompt files that vibecast then cat'd into Claude.
        async Task WriteContainerFileAsync(string path, string content, int timeoutSeconds = 30, int mode = 420 /* 0o644 */)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            await _spawnerService.CopyFileToContainerAsync(containerId, path, bytes, mode: mode, ct);
        }

        if (!string.IsNullOrEmpty(jobPrompt))
        {
            _console.MarkupLine($"[cyan]Writing prompt to {promptFile}...[/]");
            await WriteContainerFileAsync(promptFile, jobPrompt);
        }

        // 6b. Materialize user-uploaded task assets into {workspace}/.agentics/assets/
        await SyncTaskAssetsAsync(
            client,
            job.AgentDef?.TaskAssets,
            jobWorkTree: null,
            containerId: containerId,
            workspaceFolderInContainer: workspaceFolder,
            ct);

        // 6c. Materialize the line's static .agentics/specs/ contracts into the worktree so
        //     station prompts can `Read .agentics/specs/<contract>.md`. These files are part of
        //     the line definition, not authored by any worker — without this they never exist in
        //     the container and the station degrades to guessing the schema.
        if (job.AgentDef?.AgenticsSpecFiles is { Count: > 0 } specFiles)
        {
            foreach (var (relPath, content) in specFiles)
            {
                // Guard against path traversal — relPath is a trusted server value but keep it scoped.
                var safeRel = relPath.Replace("\\", "/").TrimStart('/');
                if (safeRel.Contains("..")) continue;
                await WriteContainerFileAsync($"{workspaceFolder}/{safeRel}", content);
            }
            _console.MarkupLine($"  [green]✓[/] materialized {specFiles.Count} spec file(s) → {workspaceFolder}/.agentics/specs/");
        }

        // 7. Build and write a launch script into the container to avoid shell quoting issues
        var vibecastTmux = $"vibecast-{job.Id[..8]}";
        var defaultAppendPrompt = "When you have completed the assigned task, use the stop_broadcast MCP tool " +
            "with a message summarizing what you accomplished and conclusion success. " +
            "If you encounter an unrecoverable error, call stop_broadcast with conclusion failure and describe the issue.";
        var appendPrompt = !string.IsNullOrWhiteSpace(job.AgentDef?.AppendSystemPrompt)
            ? job.AgentDef.AppendSystemPrompt + "\n\n" + defaultAppendPrompt
            : defaultAppendPrompt;
        if (job.AgentDef?.GitignoreLines?.Count > 0)
        {
            var lines = string.Join("\n", job.AgentDef.GitignoreLines.Select(l => $"  {l}"));
            appendPrompt = $"Ensure the project's .gitignore file contains the following lines (add them if missing):\n{lines}\n\n" + appendPrompt;
        }
        var stageGitUrl = job.AgentDef?.StageGitUrl ?? "";
        var stageGitToken = job.AgentDef?.StageGitToken ?? "";
        var stageDir = $"{vibecastHome}/stage";

        // Rebase stage git URL onto the container-accessible server (same host/port as AGENTIC_SERVER).
        // The stage git server IS the agentic server, so we reuse hostForContainer + serverUri.Port.
        if (!string.IsNullOrEmpty(stageGitUrl))
        {
            var stageUri = new Uri(stageGitUrl);
            var rebased = new UriBuilder(stageUri)
            {
                Host = hostForContainer,
                Port = serverUri.Port,
                Scheme = serverUri.Scheme,
            };
            stageGitUrl = rebased.Uri.ToString();
        }

        // Write appendSystemPrompt to a file (matches in-process behaviour). Avoids any shell
        // escaping concerns regardless of what's in the markdown.
        var appendPromptFile = $"{vibecastHome}/append-system-prompt.txt";
        await WriteContainerFileAsync(appendPromptFile, appendPrompt);

        // SubagentStart hook: inject SubagentPromptAppendix into every spawned subagent.
        // Written to a file the hook reads at runtime; env var below points at it.
        string? subagentSuffixFile = null;
        if (!string.IsNullOrWhiteSpace(job.AgentDef?.SubagentPromptAppendix))
        {
            subagentSuffixFile = $"{vibecastHome}/subagent-prompt-suffix.txt";
            await WriteContainerFileAsync(subagentSuffixFile, job.AgentDef.SubagentPromptAppendix);
        }

        // Pre-write .claude/ config so Claude Code (1) anchors project-root detection at the
        // workspace and doesn't walk past it, and (2) doesn't TUI-prompt for permission on every
        // settings.local.json write. Mirrors the in-process pre-flight at line 2071-2128.
        var workspaceClaudeDir = $"{workspaceFolder}/.claude";
        await WriteContainerFileAsync($"{workspaceClaudeDir}/settings.json", "{}\n");
        await WriteContainerFileAsync($"{workspaceClaudeDir}/settings.local.json", """
            {
              "permissions": {
                "allow": [
                  "Write(.claude/**)",
                  "Edit(.claude/**)",
                  "Bash(mkdir**)"
                ]
              },
              "enableAllProjectMcpServers": true
            }
            """);
        await WriteContainerFileAsync($"{workspaceClaudeDir}/.gitignore",
            "# Runner-injected — never commit\nsettings.local.json\n");
        await WriteContainerFileAsync($"{workspaceFolder}/CLAUDE.md", $"""
            # Job Environment

            This is a runner job container. Your workspace is `{workspaceFolder}`.
            All files must be created under `{workspaceFolder}` — do not write to parent directories.
            """);

        var launchScript = $"{vibecastHome}/start.sh";
        var scriptLines = new System.Text.StringBuilder();
        scriptLines.AppendLine("#!/bin/bash");
        // Unset TMUX so vibecast doesn't inherit the outer container tmux session context.
        // If TMUX is set, vibecast's ttyd inherits it and "tmux attach" refuses to nest sessions,
        // causing the broadcast relay to see nothing instead of the Claude Code window.
        scriptLines.AppendLine("unset TMUX");
        // Ensure user-local bin is on PATH so claude is found (e.g. /home/node/.local/bin)
        scriptLines.AppendLine("export PATH=\"$HOME/.local/bin:/home/node/.local/bin:/usr/local/bin:/usr/bin:/bin:$PATH\"");
        // Locale + terminal capabilities. In-process mode inherits these from the runner host
        // shell; spawn mode runs inside a fresh container where they're empty by default. Without
        // LANG/LC_ALL set to a UTF-8 locale, libc falls back to "C" (ASCII-only) and Unicode block
        // characters in Claude's banner / spinners render as "_". C.UTF-8 is universally available
        // in Debian/Alpine/etc base images (en_US.UTF-8 often isn't generated). xterm-256color +
        // COLORTERM=truecolor + FORCE_COLOR=3 turn on full ANSI color in Claude/syntax highlighting.
        scriptLines.AppendLine("export LANG=${LANG:-C.UTF-8}");
        scriptLines.AppendLine("export LC_ALL=${LC_ALL:-C.UTF-8}");
        scriptLines.AppendLine("export TERM=${TERM:-xterm-256color}");
        scriptLines.AppendLine("export COLORTERM=${COLORTERM:-truecolor}");
        scriptLines.AppendLine("export FORCE_COLOR=${FORCE_COLOR:-3}");
        scriptLines.AppendLine($"export VIBECAST_HOME={vibecastHome}");
        scriptLines.AppendLine($"export AGENTICS_SERVER={agenticServerForContainer}");
        scriptLines.AppendLine($"export AGENTIC_SERVER={agenticServerForContainer}"); // deprecated, kept for backwards compat
        scriptLines.AppendLine($"export AGENTICS_PROJECT={registration.Owner}/{registration.Project}");
        scriptLines.AppendLine($"export AGENTICS_JOB_ID={job.Id}");
        scriptLines.AppendLine($"export AGENTICS_TOKEN='{registration.Token}'");
        scriptLines.AppendLine($"export AGENTICS_OWNER='{registration.Owner}'");
        scriptLines.AppendLine($"export AGENTICS_PROJECT_NAME='{registration.Project}'");
        var agenticsBaseUrl = $"{serverUri.Scheme}://{serverUri.Host}{(serverUri.IsDefaultPort ? "" : $":{serverUri.Port}")}";
        scriptLines.AppendLine($"export AGENTICS_BASE_URL='{agenticsBaseUrl}'");
        // Enable keyboard input from viewers (PIN = first 8 chars of job ID)
        var keyboardPin = job.Id[..8].ToUpperInvariant();
        scriptLines.AppendLine($"export VIBECAST_KEYBOARD_PIN={keyboardPin}");
        scriptLines.AppendLine("export VIBECAST_DEBUG=1");

        // Forward Anthropic provider env from the runner host into the job so the
        // spawned claude can use a real API key or the pks-agent-gateway LLM sim
        // (ANTHROPIC_API_KEY=sim-*) instead of the OAuth credentials volume. A
        // localhost ANTHROPIC_BASE_URL is rebased onto the container-reachable
        // host, same as the stage git URL above.
        var anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var anthropicBaseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        if (!string.IsNullOrEmpty(anthropicKey))
            scriptLines.AppendLine($"export ANTHROPIC_API_KEY='{anthropicKey}'");
        if (!string.IsNullOrEmpty(anthropicBaseUrl))
        {
            if (Uri.TryCreate(anthropicBaseUrl, UriKind.Absolute, out var anthropicUri)
                && anthropicUri.Host is "localhost" or "127.0.0.1")
            {
                anthropicBaseUrl = new UriBuilder(anthropicUri) { Host = containerHostIp }
                    .Uri.ToString().TrimEnd('/');
            }
            scriptLines.AppendLine($"export ANTHROPIC_BASE_URL='{anthropicBaseUrl}'");
        }

        // Forward the OpenAI API key from the runner host so a codex Operator
        // (VIBECAST_AGENT=codex) can authenticate via env-key auth. vibecast's config-seed
        // (StartStream Prepare) tolerates a missing ~/.codex/auth.json precisely because this
        // env-key path may cover it. Harmless for claude (ignored). NOTE: a ChatGPT-login
        // (auth.json) codex account would instead need that file copied into the container —
        // an alternative not wired here (would bill against the subscription, not the API).
        var openaiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(openaiKey))
            scriptLines.AppendLine($"export OPENAI_API_KEY='{openaiKey}'");

        // If a vibecast binary was embedded at build time (local dev only, -p:EmbedVibecast=true),
        // extract it into the container and point VIBECAST_BIN at it so the hooks also pick it up.
        // This lets us test local vibecast changes without publishing to npm.
        var embeddedVibecastPath = await TryInjectEmbeddedVibecastAsync(containerId);
        if (embeddedVibecastPath != null)
        {
            scriptLines.AppendLine($"export VIBECAST_BIN={embeddedVibecastPath}");
            _console.MarkupLine($"[yellow]Using embedded vibecast binary: {embeddedVibecastPath}[/]");
        }

        // Only pass initial prompt file when prompt is non-empty (matches in-process mode behaviour)
        if (!string.IsNullOrEmpty(jobPrompt))
            scriptLines.AppendLine($"export VIBECAST_INITIAL_PROMPT_FILE={promptFile}");
        if (stageGitUrl.Length > 0)
        {
            scriptLines.AppendLine($"export STAGE_GIT_URL={stageGitUrl}");
            scriptLines.AppendLine($"export STAGE_GIT_TOKEN={stageGitToken}");
            scriptLines.AppendLine($"export STAGE_DIR={stageDir}");
        }
        // Use FILE form (matches in-process). Bullet-proof against any prompt content; no shell
        // parsing of the markdown happens.
        scriptLines.AppendLine($"export VIBECAST_APPEND_SYSTEM_PROMPT_FILE={appendPromptFile}");

        // Tell vibecast it's running as a job (used for behaviour gating in vibecast itself).
        scriptLines.AppendLine("export AGENTICS_JOB_MODE=1");

        // Kind A chat-session:v1 (external/alp-spec/2026-03-30-draft/spec/13-chat.md): tells the
        // container's vibecast (Operator) to open its Chat Channel for this job's main pane. vibecast
        // gates this behind AGENTICS_CHAT_SESSION=1 alongside the AGENTICS_JOB_ID already exported
        // above — see maybeStartChatChannel in external/vibecast/internal/stream/stream.go.
        if (job.IsChatSession)
            scriptLines.AppendLine("export AGENTICS_CHAT_SESSION=1");

        // Resume Claude session on retry-after-timeout. Spawn-mode used to silently drop this.
        if (!string.IsNullOrEmpty(job.AgentDef?.ResumeSessionId))
            scriptLines.AppendLine($"export VIBECAST_RESUME_SESSION_ID={job.AgentDef.ResumeSessionId}");

        // SubagentStart hook reads this file when set (subagent prompt appendix).
        if (subagentSuffixFile is not null)
            scriptLines.AppendLine($"export SUBAGENT_PROMPT_SUFFIX_FILE={subagentSuffixFile}");

        // Operator: stop Claude Code from spawning long-lived background tasks (used by
        // stations that hit subagent-stall issues with background work).
        if (job.AgentDef?.OperatorConfig?.DisableBackgroundTasks == true)
            scriptLines.AppendLine("export CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1");

        // Expose pre-cloned plugins via VIBECAST_EXTRA_PLUGINS so vibecast passes --plugin-dir
        // to Claude for each one. Plugins are cloned by the Runner (pks-cli) before container
        // spawn and mounted at $HOME/.alp/plugins via a dedicated Docker volume — see PreparePluginVolumeAsync.
        if (pluginContainerPaths.Count > 0)
        {
            scriptLines.AppendLine($"export VIBECAST_EXTRA_PLUGINS=\"{string.Join(":", pluginContainerPaths)}\"");
        }

        // AgenticsProxy socket (bind-mounted from host): agents use curl --unix-socket to reach proxy
        scriptLines.AppendLine("export AGENTICS_PROXY_SOCKET=/var/run/pks-agentics/proxy.sock");
        scriptLines.AppendLine($"export AGENTICS_PROXY_TOKEN=\"{agenticsProxyOptions.BootstrapToken}\"");

        // OTLP bridge: vibecast/Claude expect a TCP HTTP endpoint, but our OtlpProxy is reachable
        // only via a Unix socket bind-mounted at /var/run/pks-otlp/otlp.sock. A tiny Node TCP→Unix
        // bridge listens on 127.0.0.1:4318 and forwards each connection to the socket.
        // NOTE: must NOT live under /tmp — dind remounts /tmp as tmpfs, and Docker's archive
        // endpoint silently fails to write there (see ADR 0006). $vibecastHome is overlay-backed.
        var otlpBridgePath = $"{vibecastHome}/otlp-bridge.js";
        const string otlpBridgeJs = @"const net = require('net');
const TCP_PORT = 4318;
const SOCK = '/var/run/pks-otlp/otlp.sock';
const server = net.createServer((client) => {
  const upstream = net.createConnection(SOCK);
  client.pipe(upstream);
  upstream.pipe(client);
  upstream.on('error', () => { try { client.destroy(); } catch (_) {} });
  client.on('error', () => { try { upstream.destroy(); } catch (_) {} });
});
server.on('error', (e) => { console.error('otlp-bridge:', e.message); process.exit(1); });
server.listen(TCP_PORT, '127.0.0.1', () => console.log('otlp-bridge: 127.0.0.1:' + TCP_PORT + ' -> ' + SOCK));
";
        await WriteContainerFileAsync(otlpBridgePath, otlpBridgeJs);
        scriptLines.AppendLine($"node {otlpBridgePath} > {vibecastHome}/otlp-bridge.log 2>&1 &");

        // OTEL env vars — mirror in-process mode but point at the in-container bridge
        var otelResourceAttrs = $"job.id={job.Id},run.id={runId}";
        if (!string.IsNullOrEmpty(job.AgentDef?.TaskId))
            otelResourceAttrs += $",task.id={job.AgentDef.TaskId}";
        if (!string.IsNullOrEmpty(job.AgentDef?.AssemblyLineId))
            otelResourceAttrs += $",assembly_line.id={job.AgentDef.AssemblyLineId}";
        const string otelBase = "http://localhost:4318";
        scriptLines.AppendLine($"export OTEL_EXPORTER_OTLP_ENDPOINT=\"{otelBase}\"");
        scriptLines.AppendLine($"export OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{otelBase}/v1/traces\"");
        scriptLines.AppendLine($"export OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=\"{otelBase}/v1/logs\"");
        scriptLines.AppendLine($"export OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=\"{otelBase}/v1/metrics\"");
        scriptLines.AppendLine("export OTEL_EXPORTER_OTLP_INSECURE=true");
        scriptLines.AppendLine("export OTEL_EXPORTER_OTLP_PROTOCOL=\"http/json\"");
        scriptLines.AppendLine("export OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=\"http/json\"");
        scriptLines.AppendLine("export OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=\"http/json\"");
        scriptLines.AppendLine("export OTEL_SERVICE_NAME=\"vibecast-job\"");
        scriptLines.AppendLine($"export OTEL_RESOURCE_ATTRIBUTES=\"{otelResourceAttrs}\"");
        scriptLines.AppendLine("export CLAUDE_CODE_ENABLE_TELEMETRY=1");
        scriptLines.AppendLine("export CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1");
        scriptLines.AppendLine("export OTEL_TRACES_EXPORTER=otlp");
        scriptLines.AppendLine("export OTEL_LOGS_EXPORTER=otlp");
        scriptLines.AppendLine("export OTEL_TRACES_EXPORT_INTERVAL=5000");
        scriptLines.AppendLine("export OTEL_LOGS_EXPORT_INTERVAL=5000");

        // Note: Claude's first-run gates (theme picker, login-method picker, OAuth URL +
        // code paste, workspace trust) are handled by vibecast's pane-grep detectors so
        // env-var providers (ANTHROPIC_API_KEY, ANTHROPIC_BASE_URL, CLAUDE_CODE_USE_BEDROCK)
        // can still suppress them upstream. See vibecast/internal/broadcast/broadcast.go.

        // Propagate W3C trace context for log correlation with the server-side trace.
        if (!string.IsNullOrEmpty(job.AgentDef?.Traceparent))
        {
            scriptLines.AppendLine($"export TRACEPARENT={job.AgentDef.Traceparent}");
            var traceId = job.AgentDef.Traceparent.Split('-').ElementAtOrDefault(1) ?? "";
            scriptLines.AppendLine($"export AGENTICS_TRACE_ID={traceId}");
        }

        // Auto-git: tell vibecast to block Claude from stopping with uncommitted changes
        if (job.AgentDef?.AutoGit == true)
        {
            scriptLines.AppendLine("export AGENTICS_AUTO_GIT=1");
            if (!string.IsNullOrWhiteSpace(job.AgentDef.CommitMessageTemplate))
            {
                // Escape single quotes in the template before embedding in the shell export
                var escapedHint = job.AgentDef.CommitMessageTemplate.Replace("'", "'\\''");
                scriptLines.AppendLine($"export AGENTICS_COMMIT_MESSAGE_HINT='{escapedHint}'");
            }
            if (!string.IsNullOrWhiteSpace(job.AgentDef.ProjectRepoToken))
                scriptLines.AppendLine($"export AGENTICS_REPO_TOKEN='{job.AgentDef.ProjectRepoToken}'");
        }

        // Operator: auto-approve image uploads so headless stations don't require TUI interaction
        if (job.AgentDef?.OperatorConfig?.AutoApproveImageUploads == true)
        {
            scriptLines.AppendLine("export VIBECAST_AUTO_APPROVE_IMAGES=1");
        }

        // Operator: per-station capability config (model / effort). The runner only passes the
        // resolved values through; vibecast owns the mapping to `claude --model`/`--effort`,
        // including the specific-model → tier fallback. See vibecast internal/stream/stream.go.
        if (!string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Model))
            scriptLines.AppendLine($"export VIBECAST_CLAUDE_MODEL='{job.AgentDef!.OperatorConfig!.Model}'");
        if (!string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.ModelTier))
            scriptLines.AppendLine($"export VIBECAST_CLAUDE_MODEL_TIER='{job.AgentDef!.OperatorConfig!.ModelTier}'");
        if (!string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Effort))
            scriptLines.AppendLine($"export VIBECAST_CLAUDE_EFFORT='{job.AgentDef!.OperatorConfig!.Effort}'");
        // Operator: which coding-agent CLI vibecast runs (claude|codex|pi). Resolved server-side
        // (station ?? line default); unset → vibecast defaults to claude. vibecast owns the
        // per-agent launch/config-seed — see internal/agent + StartStream's Prepare seam.
        if (!string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Agent))
            scriptLines.AppendLine($"export VIBECAST_AGENT='{job.AgentDef!.OperatorConfig!.Agent}'");

        if (!string.IsNullOrEmpty(job.AgentDef?.BroadcastId))
        {
            scriptLines.AppendLine($"export BROADCAST_ID={job.AgentDef.BroadcastId}");
        }

        var gitUserName = settings.GitUserName ?? "si-14x";
        var gitUserEmail = settings.GitUserEmail ?? "si-14x@agentics.dk";
        scriptLines.AppendLine($"git config --global user.name \"{gitUserName}\"");
        scriptLines.AppendLine($"git config --global user.email \"{gitUserEmail}\"");
        scriptLines.AppendLine($"cd {workspaceFolder}");

        // initBranch: create a task-scoped branch before Claude starts so work is isolated
        if (job.AgentDef?.InitBranch == true && !string.IsNullOrEmpty(job.AgentDef.TaskId))
        {
            var branchName = $"task/{job.AgentDef.TaskId}";
            scriptLines.AppendLine($"git checkout -b {branchName} 2>/dev/null || git checkout {branchName}");
        }

        // Remove any stale .mcp.json left by an older vibecast session (plugin dir handles MCP now).
        // Tools like `aspire agent init` run during the session and will recreate it correctly.
        scriptLines.AppendLine("rm -f .mcp.json");

        // Devagent session (plan `snappy-wandering-mochi` Phase 3): wire Claude's `agent-share
        // channel` stdio bridge to the project's dedicated inbox so the light chat's
        // delegate_to_devagent tool can push tasks in and get replies back via complete().
        // AGENT_SHARE_SERVER/AGENT_SHARE_TOKEN are read by channel.go's channelAuth() — env
        // takes precedence over any stored login. VIBECAST_CLAUDE_CHANNEL tells vibecast to
        // add --dangerously-load-development-channels server:agent-share to the claude invocation.
        // Gated only on DevAgentChannel presence, not IsDevAgentSession/needs: www-site sends this
        // channel on ordinary alp_operator jobs too, without declaring the (unclaimed) capability.
        if (job.AgentDef?.DevAgentChannel is { } devAgentChannel &&
            !string.IsNullOrEmpty(devAgentChannel.McpUrl) && !string.IsNullOrEmpty(devAgentChannel.Token))
        {
            // Same distribution channel as vibecast itself (docs/cli-install) — installs to
            // ~/.local/bin (already on PATH in the pks-fullstack base image) if not already present.
            scriptLines.AppendLine("which agent-share >/dev/null 2>&1 || curl -fsSL https://agentics.dk/install/agent-share.sh | bash");
            var a2aBaseUrl = System.Text.RegularExpressions.Regex.Replace(devAgentChannel.McpUrl, "/mcp/?$", "");
            var mcpJson = JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["agent-share"] = new
                    {
                        command = "agent-share",
                        args = new[] { "channel" },
                        env = new Dictionary<string, string>
                        {
                            ["AGENT_SHARE_SERVER"] = a2aBaseUrl,
                            ["AGENT_SHARE_TOKEN"] = devAgentChannel.Token
                        }
                    }
                }
            });
            scriptLines.AppendLine($"cat > .mcp.json <<'AGENT_SHARE_MCP_JSON_EOF'\n{mcpJson}\nAGENT_SHARE_MCP_JSON_EOF");
            scriptLines.AppendLine("export VIBECAST_CLAUDE_CHANNEL=server:agent-share");

            // The .mcp.json-declared server's interactive "New MCP server found ..." trust
            // prompt is pre-approved by the `enableAllProjectMcpServers` key already baked into
            // .claude/settings.local.json above (written unconditionally, before this block runs,
            // for every job — not just ones with a devAgentChannel).
        }

        // Write a targeted .claude/.gitignore so only job/session-scoped files are excluded from git.
        // A blanket "*" would also suppress MCP configs written by tools like `aspire agent init`.
        scriptLines.AppendLine("mkdir -p .claude");
        scriptLines.AppendLine("[ -f .claude/.gitignore ] || printf '%s\\n' '# Runner-injected — never commit' 'settings.local.json' > .claude/.gitignore");
        scriptLines.AppendLine("exec ${VIBECAST_BIN:-npx --yes vibecast}");

        // Launch script — mode 0o755 (493 decimal) so it's executable.
        var scriptBytes = System.Text.Encoding.UTF8.GetBytes(scriptLines.ToString().Replace("\r\n", "\n"));
        await _spawnerService.CopyFileToContainerAsync(containerId, launchScript, scriptBytes,
            mode: 493 /* 0o755 */, ct: ct);

        // Agents are delivered via --plugin-dir (VIBECAST_EXTRA_PLUGINS) — see WriteAgentPluginDirInVolumeAsync above.

        // 7a. Pre-flight: verify tmux, npx, and agentic server reachability
        var preFlight = await _spawnerService.ExecInContainerAsync(containerId,
            "bash -c 'which tmux && echo tmux-ok || echo tmux-missing'", timeoutSeconds: 10);
        var npxCheck = await _spawnerService.ExecInContainerAsync(containerId,
            "bash -c 'which npx && npx --version && echo npx-ok || echo npx-missing'", timeoutSeconds: 10);
        var reachCheck = await _spawnerService.ExecInContainerAsync(containerId,
            $"bash -c 'curl -sf --max-time 5 {serverUri.Scheme}://{agenticServerForContainer}/api/healthz -o /dev/null && echo reachable || echo unreachable'",
            timeoutSeconds: 15);
        _console.MarkupLine($"[grey]tmux: {preFlight.Output.Trim()}[/]");
        _console.MarkupLine($"[grey]npx:  {npxCheck.Output.Trim()}[/]");
        _console.MarkupLine($"[grey]agentic server ({agenticServerForContainer}): {reachCheck.Output.Trim()}[/]");
        if (preFlight.Output.Contains("tmux-missing") || npxCheck.Output.Contains("npx-missing"))
        {
            _console.MarkupLine("[red]Missing required tool in container (tmux or npx). Cannot start vibecast.[/]");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
            await ReportJobResultAsync(registration, "failed", "missing tmux or npx in container", ct);
            return;
        }

        _console.MarkupLine($"[cyan]Starting vibecast in container tmux session '{vibecastTmux}'...[/]");
        _console.MarkupLine($"[dim]Keyboard PIN (for web UI input): {keyboardPin}[/]");
        var tmuxStart = await _spawnerService.ExecInContainerAsync(containerId,
            $"tmux new-session -d -s {vibecastTmux} -x 120 -y 48 " +
            $"bash -c '{launchScript} 2>&1 | tee {vibecastHome}/vibecast.log'",
            timeoutSeconds: 30);
        if (!string.IsNullOrWhiteSpace(tmuxStart.Error))
            _console.MarkupLine($"[grey]tmux start: {tmuxStart.Error.Trim()}[/]");

        // 8. Wait for vibecast control socket, printing tmux pane output every 5s for visibility
        var controlSocket = $"{vibecastHome}/.vibecast/control.sock";
        _console.MarkupLine($"[cyan]Waiting for control socket...[/]");
        // Track which onboarding prompts we've already surfaced so we don't re-print every 5s tail.
        var shownOnboardingIds = new HashSet<string>();
        var onboardingLineRe = new System.Text.RegularExpressions.Regex(
            @"\[onboarding-prompt\]\s+kind=(?<kind>\S+)\s+provider=(?<provider>\S+)\s+questionId=(?<qid>\S+)\s+url=(?<url>\S+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        void SurfaceOnboardingPrompts(string logChunk)
        {
            foreach (System.Text.RegularExpressions.Match m in onboardingLineRe.Matches(logChunk))
            {
                var qid = m.Groups["qid"].Value;
                if (!shownOnboardingIds.Add(qid)) continue;
                var kind = m.Groups["kind"].Value;
                var provider = m.Groups["provider"].Value;
                var url = m.Groups["url"].Value;
                _console.WriteLine();
                _console.Write(new Spectre.Console.Panel(
                    $"[bold]Provider:[/] {provider.EscapeMarkup()}    [bold]Kind:[/] {kind.EscapeMarkup()}\n\n" +
                    $"[bold]Open this URL in your browser:[/]\n[link={url}]{url.EscapeMarkup()}[/]\n\n" +
                    $"After signing in, paste the returned code on the task page in the dashboard.")
                {
                    Header = new Spectre.Console.PanelHeader("[yellow]⚠ Agent paused — sign-in required[/]"),
                    Border = Spectre.Console.BoxBorder.Double,
                    BorderStyle = new Spectre.Console.Style(Spectre.Console.Color.Yellow),
                });
                _console.WriteLine();
            }
        }

        var socketReady = false;
        for (var i = 0; i < 60; i++)
        {
            var check = await _spawnerService.ExecInContainerAsync(containerId,
                $"test -S {controlSocket} && echo yes || echo no", timeoutSeconds: 5);
            if (check.Output.Contains("yes")) { socketReady = true; break; }

            // Every 5s print last few lines from vibecast log so we can see what's happening
            if (i % 5 == 4)
            {
                var log = await _spawnerService.ExecInContainerAsync(containerId,
                    $"tail -5 {vibecastHome}/vibecast.log 2>/dev/null || echo '(no log yet)'", timeoutSeconds: 5);
                _console.MarkupLine($"[grey]vibecast log (t+{i + 1}s):[/] {log.Output.Trim().EscapeMarkup()}");
                SurfaceOnboardingPrompts(log.Output);

                // Also check if the tmux session is still alive
                var alive = await _spawnerService.ExecInContainerAsync(containerId,
                    $"tmux has-session -t {vibecastTmux} 2>/dev/null && echo alive || echo exited", timeoutSeconds: 5);
                if (alive.Output.Contains("exited"))
                {
                    _console.MarkupLine("[red]vibecast tmux session exited prematurely.[/]");
                    var fullLog = await _spawnerService.ExecInContainerAsync(containerId,
                        $"cat {vibecastHome}/vibecast.log 2>/dev/null || echo '(empty)'", timeoutSeconds: 5);
                    _console.MarkupLine($"[grey]Full vibecast log:[/]\n{fullLog.Output.EscapeMarkup()}");
                    break;
                }
            }

            await Task.Delay(1000, ct);
        }
        if (!socketReady)
        {
            _console.MarkupLine("[red]Control socket not ready — vibecast failed to start.[/]");
            var fullLog = await _spawnerService.ExecInContainerAsync(containerId,
                $"cat {vibecastHome}/vibecast.log 2>/dev/null || echo '(empty)'", timeoutSeconds: 5);
            _console.MarkupLine($"[grey]vibecast log:[/]\n{fullLog.Output.EscapeMarkup()}");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
            await ReportJobResultAsync(registration, "failed", "vibecast control socket not ready", ct);
            return;
        }
        await Task.Delay(1000, ct); // let control server fully initialise

        // 9. Trigger start-stream via control socket
        _console.MarkupLine("[cyan]Triggering start-stream...[/]");
        await _spawnerService.ExecInContainerAsync(containerId,
            $"curl -sf --unix-socket {controlSocket} " +
            $"-X POST http://localhost/start-stream " +
            $"-H 'Content-Type: application/json' " +
            $"-d '{{\"promptSharing\":true,\"shareProjectInfo\":true}}'",
            timeoutSeconds: 15);

        // 10. Get sessionId/broadcastId and link to task. Link as soon as sessionId is known
        // (any phase) — vibecast detectors that fire BEFORE phase=live (theme/login pickers,
        // OAuth) need the task↔session linkage so their alp_pane / onboarding_external posts
        // can find the task. Wait for phase=live separately for start-stream confirmation.
        string? sessionIdValue = null;
        string? broadcastIdValue = null;
        bool sessionLinked = false;
        async Task TryLinkSessionToTaskAsync()
        {
            if (sessionLinked || sessionIdValue == null) return;
            if (job.AgentDef?.TaskId == null || job.AgentDef?.AssemblyLineId == null) return;
            try
            {
                var linkReq = new HttpRequestMessage(HttpMethod.Post,
                    $"{baseUrl}/assembly-lines/{job.AgentDef.AssemblyLineId}/tasks/{job.AgentDef.TaskId}/streams");
                linkReq.Content = JsonContent.Create(new { sessionId = sessionIdValue, broadcastId = broadcastIdValue });
                await client.SendAsync(linkReq, ct);
                sessionLinked = true;
                _console.MarkupLine($"[green]Stream linked to task {job.AgentDef.TaskId} (sessionId={sessionIdValue})[/]");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Failed to link stream to task: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        bool sawLive = false;
        for (var i = 0; i < 30; i++)
        {
            var status = await _spawnerService.ExecInContainerAsync(containerId,
                $"curl -sf --unix-socket {controlSocket} http://localhost/status",
                timeoutSeconds: 5);
            // Extract sessionId/broadcastId regardless of phase so onboarding questions
            // posted by vibecast BEFORE phase=live can find the task by sessionId.
            if (sessionIdValue == null)
            {
                var sessionIdMatch = System.Text.RegularExpressions.Regex.Match(status.Output, "\"sessionId\":\"([^\"]+)\"");
                if (sessionIdMatch.Success)
                {
                    sessionIdValue = sessionIdMatch.Groups[1].Value;
                    var broadcastIdMatch = System.Text.RegularExpressions.Regex.Match(status.Output, "\"broadcastId\":\"([^\"]+)\"");
                    if (broadcastIdMatch.Success) broadcastIdValue = broadcastIdMatch.Groups[1].Value;
                    await TryLinkSessionToTaskAsync(); // link immediately so server can route alp_pane to the task
                }
            }
            if (status.Output.Contains("\"phase\":\"live\"")) { sawLive = true; break; }
            await Task.Delay(1000, ct);
        }
        if (!sawLive)
            _console.MarkupLine($"[yellow]phase=live not seen in 30s; sessionId={(sessionIdValue ?? "(none)")} continuing[/]");

        // Print vibecast log so far to help diagnose connection issues
        var vibecastLogSnapshot = await _spawnerService.ExecInContainerAsync(containerId,
            $"cat {vibecastHome}/vibecast.log 2>/dev/null || echo '(empty)'", timeoutSeconds: 5);
        if (!string.IsNullOrWhiteSpace(vibecastLogSnapshot.Output))
            _console.MarkupLine($"[grey]vibecast log:[/]\n{vibecastLogSnapshot.Output.Trim().EscapeMarkup()}");

        if (sessionIdValue != null)
        {
            _console.MarkupLine($"[green]Streaming live! sessionId: {sessionIdValue}[/]");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct, sessionIdValue, broadcastIdValue);
            // Linkage already happened eagerly inside the polling loop above (TryLinkSessionToTaskAsync).

            // Seed the activity bar with the initial prompt so viewers see it immediately,
            // even before Claude Code processes it (hooks only fire once Claude is running).
            if (!string.IsNullOrEmpty(jobPrompt))
            {
                try
                {
                    var metaUrl = $"{registration.Server.TrimEnd('/')}/api/lives/metadata";
                    var metaReq = new HttpRequestMessage(HttpMethod.Post, metaUrl);
                    metaReq.Content = JsonContent.Create(new
                    {
                        type = "metadata",
                        subtype = "prompt",
                        sessionId = sessionIdValue,
                        prompt = jobPrompt,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    });
                    await client.SendAsync(metaReq, ct);
                    _console.MarkupLine("[dim]Initial prompt seeded to activity bar.[/]");
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[yellow]Failed to seed initial prompt: {ex.Message.EscapeMarkup()}[/]");
                }
            }

            // Store task and system prompts on the session so the activity log can display them.
            try
            {
                var sessionPatchUrl = $"{registration.Server.TrimEnd('/')}/api/lives/sessions/{sessionIdValue}";
                var sessionPatchReq = new HttpRequestMessage(HttpMethod.Patch, sessionPatchUrl);
                sessionPatchReq.Content = JsonContent.Create(new
                {
                    taskPrompt = string.IsNullOrEmpty(jobPrompt) ? null : jobPrompt,
                    systemPrompt = string.IsNullOrEmpty(appendPrompt) ? null : appendPrompt,
                });
                await client.SendAsync(sessionPatchReq, ct);
                _console.MarkupLine("[dim]Prompts stored on session.[/]");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Failed to store prompts on session: {ex.Message.EscapeMarkup()}[/]");
            }
        }
        else
        {
            _console.MarkupLine("[yellow]Streaming session not detected, continuing anyway...[/]");
        }

        // 11. Wait for job to complete (tmux session ends, idle timeout, or max timeout)
        var idleTimeoutMs = (job.AgentDef?.IdleTimeoutMinutes ?? 5) * 60_000;
        var maxTimeout = TimeSpan.FromMinutes(job.AgentDef?.MaxTimeoutMinutes ?? 60);
        _console.MarkupLine($"[cyan]Waiting up to {maxTimeout.TotalMinutes:0}min (idle: {idleTimeoutMs / 60000}min)...[/]");

        var startTime = DateTime.UtcNow;
        while (DateTime.UtcNow - startTime < maxTimeout && !ct.IsCancellationRequested)
        {
            // Check tmux session
            var tmuxCheck = await _spawnerService.ExecInContainerAsync(containerId,
                $"tmux has-session -t {vibecastTmux} 2>/dev/null && echo running || echo done",
                timeoutSeconds: 5);
            if (tmuxCheck.Output.Contains("done"))
            {
                _console.MarkupLine("[green]Vibecast session ended — job complete.[/]");
                break;
            }

            // Check .task-complete signal file
            var signalCheck = await _spawnerService.ExecInContainerAsync(containerId,
                $"test -f {workspaceFolder}/.task-complete && echo yes || echo no",
                timeoutSeconds: 5);
            if (signalCheck.Output.Contains("yes"))
            {
                _console.MarkupLine("[green]Task completion signal detected.[/]");
                break;
            }

            // Poll activity API (runner is on host, server is accessible directly)
            if (sessionIdValue != null)
            {
                try
                {
                    var actUrl = $"{registration.Server.TrimEnd('/')}/api/lives/activity?sessionId={sessionIdValue}&idleThresholdMs={idleTimeoutMs}";
                    var actResp = await client.GetAsync(actUrl, ct);
                    if (actResp.IsSuccessStatusCode)
                    {
                        var actData = JsonSerializer.Deserialize<JsonElement>(
                            await actResp.Content.ReadAsStringAsync(ct), JsonOptions);
                        if (actData.TryGetProperty("isActive", out var isActive) && !isActive.GetBoolean())
                        {
                            var idleSec = actData.TryGetProperty("idleSinceMs", out var ms) ? ms.GetInt64() / 1000 : 0;
                            _console.MarkupLine($"[yellow]Agent idle for {idleSec}s — completing job.[/]");
                            break;
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            // Tail vibecast.log so onboarding prompts that fire AFTER socket-ready (e.g. OAuth
            // gate during Claude's first launch) get surfaced in this pks-runner pane via
            // SurfaceOnboardingPrompts. Helper is idempotent — already-shown IDs are skipped.
            try
            {
                var logTail = await _spawnerService.ExecInContainerAsync(containerId,
                    $"tail -50 {vibecastHome}/vibecast.log 2>/dev/null", timeoutSeconds: 5);
                SurfaceOnboardingPrompts(logTail.Output);
            }
            catch { /* best-effort */ }

            var elapsed = DateTime.UtcNow - startTime;
            if ((int)elapsed.TotalSeconds % 60 < 30)
                _console.MarkupLine($"[dim]{elapsed.Minutes}m elapsed...[/]");

            await Task.Delay(30_000, ct);
        }

        // 12. Stop vibecast and clean up
        _console.MarkupLine("[cyan]Stopping vibecast...[/]");
        await _spawnerService.ExecInContainerAsync(containerId,
            $"curl -sf --unix-socket {controlSocket} -X POST http://localhost/stop-broadcast || true",
            timeoutSeconds: 10);
        await Task.Delay(2000, ct);
        await _spawnerService.ExecInContainerAsync(containerId,
            $"tmux kill-session -t {vibecastTmux} 2>/dev/null || true",
            timeoutSeconds: 10);

        // 13. PATCH to completed
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "success", ct);
        await ReportJobResultAsync(registration, "success", null, ct);
        // No plugin-volume teardown — per ADR 0003 plugins live as files inside the container,
        // wiped + repopulated on the next job dispatch.
    }

    /// <summary>
    /// Creates a Docker volume named <c>alp-plugins-{jobId}</c>, clones each declared plugin
    /// into it via a short-lived alpine/git container, and returns the container-side paths
    /// (<c>$HOME/.alp/plugins/{pluginId}</c>) to expose via <c>VIBECAST_EXTRA_PLUGINS</c>.
    ///
    /// Cloning happens here (Runner process) so marketplace URLs such as <c>localhost:40145</c>
    /// are reachable — the Runner has access to the host network, the devcontainer does not.
    /// The volume must already exist.
    /// </summary>
    private async Task<List<string>> ClonePluginsIntoVolumeAsync(
        IEnumerable<PluginRef> plugins, string volumeName, CancellationToken ct)
    {
        var containerPaths = new List<string>();
        foreach (var plugin in plugins)
        {
            var destInVolume = $"/plugins/{plugin.Id}";
            _console.MarkupLine($"[dim]Cloning plugin {plugin.Id} from {plugin.SourceUrl.EscapeMarkup()}[/]");
            var cloneResult = await RunCaptureAsync("docker", new[]
            {
                "run", "--rm",
                "--mount", $"type=volume,source={volumeName},target=/plugins",
                "alpine/git", "clone", "--depth=1", plugin.SourceUrl, destInVolume
            }, ct);

            if (cloneResult.ExitCode == 0)
            {
                containerPaths.Add($"$HOME/.alp/plugins/{plugin.Id}");
                _console.MarkupLine($"[green]  ✓ {plugin.Id}[/]");
            }
            else
            {
                _console.MarkupLine($"[yellow]Could not clone plugin {plugin.Id}: {cloneResult.Stderr.Trim().EscapeMarkup()}[/]");
            }
        }

        return containerPaths;
    }

    /// <summary>
    /// Writes a dynamic agent plugin directory into an existing Docker volume so vibecast can
    /// pass it as <c>--plugin-dir</c> to Claude. Agents are delivered as a plugin dir rather than
    /// being written to the workspace <c>.claude/agents/</c> folder, keeping agent installation
    /// consistent with the regular plugin mechanism.
    ///
    /// Plugin dir structure inside the volume:
    /// <code>
    ///   agents-{jobId}/
    ///   ├── .claude-plugin/plugin.json
    ///   └── agents/
    ///       ├── {agentId}.md
    ///       └── ...
    /// </code>
    /// </summary>
    private async Task<string?> WriteAgentPluginDirInVolumeAsync(
        IList<AgentRef> agents, string volumeName, string jobId, CancellationToken ct)
    {
        var dirName = $"agents-{jobId}";
        var pluginJson = $$"""{"name":"task-agents","version":"1.0.0","description":"Agents for job {{jobId}}"}""";
        var pluginJsonB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pluginJson));

        // Build a shell script that creates the dir structure and writes all agent files
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"mkdir -p /plugins/{dirName}/.claude-plugin /plugins/{dirName}/agents");
        sb.AppendLine($"printf '%s' '{pluginJsonB64}' | base64 -d > /plugins/{dirName}/.claude-plugin/plugin.json");
        foreach (var agent in agents)
        {
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(agent.Content));
            sb.AppendLine($"printf '%s' '{b64}' | base64 -d > /plugins/{dirName}/agents/{agent.Id}.md");
        }

        var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(sb.ToString().Replace("\r\n", "\n")));
        var result = await RunCaptureAsync("docker", new[]
        {
            "run", "--rm",
            "--mount", $"type=volume,source={volumeName},target=/plugins",
            "alpine", "sh", "-c", $"printf '%s' '{scriptB64}' | base64 -d | sh"
        }, ct);

        if (result.ExitCode != 0)
        {
            _console.MarkupLine($"[yellow]Could not write agent plugin dir into volume: {result.Stderr.Trim().EscapeMarkup()}[/]");
            return null;
        }

        _console.MarkupLine($"[cyan]Written {agents.Count} agent(s) as plugin dir {dirName} in volume {volumeName}[/]");

        return $"$HOME/.alp/plugins/{dirName}";
    }

    /// <summary>
    /// In in-process mode, writes agent files to a local plugin directory that pks-cli and
    /// vibecast share via the filesystem. Returns the local path to add to VIBECAST_EXTRA_PLUGINS.
    ///
    /// Plugin dir structure:
    /// <code>
    ///   {workDir}/plugins/{jobId}-agents/
    ///   ├── .claude-plugin/plugin.json
    ///   └── agents/
    ///       └── {agentId}.md
    /// </code>
    /// </summary>
    private string? CreateAgentPluginDirLocally(
        IList<AgentRef> agents, string jobId, string workDir)
    {
        try
        {
            var pluginDir = Path.Combine(workDir, "plugins", $"{jobId}-agents");
            var claudePluginDir = Path.Combine(pluginDir, ".claude-plugin");
            var agentsDir = Path.Combine(pluginDir, "agents");
            Directory.CreateDirectory(claudePluginDir);
            Directory.CreateDirectory(agentsDir);

            var pluginJson = $$"""{"name":"task-agents","version":"1.0.0","description":"Agents for job {{jobId}}"}""";
            File.WriteAllText(Path.Combine(claudePluginDir, "plugin.json"), pluginJson);

            foreach (var agent in agents)
                File.WriteAllText(Path.Combine(agentsDir, $"{agent.Id}.md"), agent.Content);

            _console.MarkupLine($"[cyan]Written {agents.Count} agent(s) as plugin dir (in-process): {pluginDir.EscapeMarkup()}[/]");

            return pluginDir;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Could not create local agent plugin dir: {ex.Message.EscapeMarkup()}[/]");
            return null;
        }
    }

    /// <summary>
    /// Removes the ALP plugin volume after the job completes.
    /// </summary>
    private async Task RemovePluginVolumeAsync(string volumeName)
    {
        var result = await RunCaptureAsync("docker", new[] { "volume", "rm", volumeName }, CancellationToken.None);
        if (result.ExitCode == 0)
            _console.MarkupLine($"[dim]Removed ALP plugin volume: {volumeName}[/]");
        else
            _console.MarkupLine($"[yellow]Could not remove plugin volume {volumeName}: {result.Stderr.Trim().EscapeMarkup()}[/]");
    }

    /// <summary>
    /// In in-process mode, clones plugins directly to the local filesystem under
    /// <c>.agentics/_work/plugins/{jobId}/</c> where both pks-cli and vibecast share the same FS.
    /// </summary>
    private async Task<List<string>> ClonePluginsLocallyAsync(
        IEnumerable<PluginRef> plugins, string jobId, string workDir, CancellationToken ct)
    {
        var pluginsDir = Path.Combine(workDir, "plugins", jobId);
        Directory.CreateDirectory(pluginsDir);
        var paths = new List<string>();
        foreach (var plugin in plugins)
        {
            var dest = Path.Combine(pluginsDir, plugin.Id);
            _console.MarkupLine($"[dim]Cloning plugin {plugin.Id} (in-process) from {plugin.SourceUrl.EscapeMarkup()}[/]");
            var result = await RunCaptureAsync("git",
                new[] { "clone", "--depth=1", plugin.SourceUrl, dest }, ct);
            if (result.ExitCode == 0)
            {
                paths.Add(dest);
                _console.MarkupLine($"[green]  cloned {plugin.Id} to {dest.EscapeMarkup()}[/]");
            }
            else
            {
                _console.MarkupLine($"[yellow]Could not clone plugin {plugin.Id}: {result.Stderr.Trim().EscapeMarkup()}[/]");
            }
        }
        return paths;
    }

    /// <summary>
    /// Runs an external command and captures stdout, stderr, and exit code.
    /// </summary>
    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunCaptureAsync(
        string executable, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null)
            return (string.Empty, "Failed to start process", 1);

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (stdout.Trim(), stderr.Trim(), proc.ExitCode);
    }

    /// <summary>
    /// Synchronizes user-uploaded task assets into the workspace at <c>.agentics/assets/</c>
    /// before vibecast/Claude starts. Runs every dispatch (replace-each-time semantics) so
    /// assets added between stations are picked up. The assets folder is gitignored so it
    /// doesn't end up in commits.
    ///
    /// Two materialization paths:
    /// - <b>In-process / --worktree mode</b>: <paramref name="jobWorkTree"/> is a host path.
    ///   Files are written directly with <see cref="File.WriteAllBytesAsync"/>.
    /// - <b>Container mode</b>: the workspace is a Docker volume mounted in the running
    ///   devcontainer. Bytes are downloaded on the host then pushed in via <c>docker cp</c>;
    ///   <c>mkdir</c> and gitignore append happen via <see cref="DevcontainerSpawnerService.ExecInContainerAsync"/>.
    /// </summary>
    private async Task SyncTaskAssetsAsync(
        HttpClient client,
        IList<TaskAssetDef>? assets,
        string? jobWorkTree,
        string? containerId,
        string? workspaceFolderInContainer,
        CancellationToken ct)
    {
        if (assets == null || assets.Count == 0) return;

        var inContainer = containerId != null && workspaceFolderInContainer != null;
        var inProcess = jobWorkTree != null;
        if (!inContainer && !inProcess) return;

        _console.MarkupLine($"[cyan]Syncing {assets.Count} task asset(s) to .agentics/assets/...[/]");

        // Download all bytes once on the host (HttpClient already carries the runner Bearer token)
        var staged = new List<(TaskAssetDef Meta, byte[] Bytes)>();
        foreach (var asset in assets)
        {
            try
            {
                using var resp = await client.GetAsync(asset.Url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _console.MarkupLine($"[yellow]  ✗ {asset.FileName}: HTTP {(int)resp.StatusCode}[/]");
                    continue;
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                staged.Add((asset, bytes));
                _console.MarkupLine($"[green]  ✓ fetched {asset.FileName} ({bytes.Length} bytes)[/]");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]  ✗ {asset.FileName}: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        if (staged.Count == 0)
        {
            _console.MarkupLine("[yellow]No task assets fetched successfully — skipping workspace sync.[/]");
            return;
        }

        var manifestObj = staged.Select(s => new
        {
            fileName = s.Meta.FileName,
            mimeType = s.Meta.MimeType,
            size = (long)s.Bytes.Length,
            url = s.Meta.Url,
        });
        var manifestJson = JsonSerializer.Serialize(manifestObj, new JsonSerializerOptions { WriteIndented = true });

        if (inProcess)
        {
            var assetsDir = Path.Combine(jobWorkTree!, ".agentics", "assets");
            if (Directory.Exists(assetsDir))
            {
                try { Directory.Delete(assetsDir, recursive: true); } catch { /* best-effort */ }
            }
            Directory.CreateDirectory(assetsDir);

            foreach (var (meta, bytes) in staged)
            {
                await File.WriteAllBytesAsync(Path.Combine(assetsDir, meta.FileName), bytes, ct);
            }
            await File.WriteAllTextAsync(Path.Combine(assetsDir, "manifest.json"), manifestJson, ct);

            // Append /.agentics/assets/ to .gitignore if missing (idempotent)
            var gitignorePath = Path.Combine(jobWorkTree!, ".gitignore");
            const string ignoreLine = "/.agentics/assets/";
            var current = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath, ct) : "";
            var alreadyPresent = current
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .Any(l => l == ignoreLine || l == ".agentics/assets/");
            if (!alreadyPresent)
            {
                var prefix = current.Length > 0 && !current.EndsWith('\n') ? "\n" : "";
                await File.AppendAllTextAsync(gitignorePath, $"{prefix}{ignoreLine}\n", ct);
            }
        }
        else
        {
            var workspace = workspaceFolderInContainer!;
            var assetsDir = $"{workspace}/.agentics/assets";

            // Wipe + recreate inside the container
            await _spawnerService.ExecInContainerAsync(
                containerId!,
                $"bash -c 'rm -rf {assetsDir} && mkdir -p {assetsDir}'",
                timeoutSeconds: 30);

            // Stage on host then docker cp each file in
            var hostStaging = Path.Combine(Path.GetTempPath(), $"task-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(hostStaging);
            try
            {
                foreach (var (meta, bytes) in staged)
                {
                    var hostPath = Path.Combine(hostStaging, meta.FileName);
                    await File.WriteAllBytesAsync(hostPath, bytes, ct);
                    var cpResult = await RunCaptureAsync("docker", new[]
                    {
                        "cp", hostPath, $"{containerId!}:{assetsDir}/{meta.FileName}",
                    }, ct);
                    if (cpResult.ExitCode != 0)
                    {
                        _console.MarkupLine($"[yellow]  docker cp failed for {meta.FileName}: {cpResult.Stderr.Trim().EscapeMarkup()}[/]");
                    }
                }

                var manifestHostPath = Path.Combine(hostStaging, "manifest.json");
                await File.WriteAllTextAsync(manifestHostPath, manifestJson, ct);
                await RunCaptureAsync("docker", new[]
                {
                    "cp", manifestHostPath, $"{containerId!}:{assetsDir}/manifest.json",
                }, ct);
            }
            finally
            {
                try { Directory.Delete(hostStaging, recursive: true); } catch { /* best-effort */ }
            }

            // Append to .gitignore inside the container if missing
            await _spawnerService.ExecInContainerAsync(
                containerId!,
                $"bash -c 'touch {workspace}/.gitignore && grep -qxF \"/.agentics/assets/\" {workspace}/.gitignore || echo \"/.agentics/assets/\" >> {workspace}/.gitignore'",
                timeoutSeconds: 15);
        }

        _console.MarkupLine("[green]Task assets synced to .agentics/assets/[/]");
    }

    /// <summary>
    /// Map a free-form spawner progress message to a coarse provisioning stage so the
    /// server timeline can render a small, stable set of labels. Returns null if the
    /// message doesn't correspond to a tracked stage.
    /// </summary>
    private static string? MapProgressMessageToStage(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var m = message.ToLowerInvariant();
        if (m.Contains("checking docker") || m.Contains("checking devcontainer cli") || m.Contains("computing configuration hash")) return "provisioning_check";
        if (m.Contains("docker volume") || m.Contains("creating docker volume")) return "provisioning_volume";
        if (m.Contains("clon") || m.Contains("repository cloned")) return "provisioning_clone";
        if (m.Contains("bootstrap image")) return "provisioning_image";
        if (m.Contains("bootstrap container") || m.Contains("creating new container") || m.Contains("starting existing container")) return "provisioning_container";
        if (m.Contains("devcontainer up") || m.Contains("running devcontainer")) return "provisioning_devcontainer";
        if (m.Contains("resolving devcontainer template") || m.Contains("applying stage devcontainer") || m.Contains("stage devcontainer files written")) return "provisioning_template";
        return null;
    }

    private async Task PostJobProgressAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string stage,
        string? message,
        CancellationToken ct)
    {
        try
        {
            var msg = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/runs/{runId}/jobs/{jobId}/progress");
            msg.Content = JsonContent.Create(new { stage, message });
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _console.MarkupLine($"[yellow]POST job progress warning: {(int)resp.StatusCode} {body.EscapeMarkup()}[/]");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]POST job progress error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Refreshes this runner's lastSeen (via the same /progress endpoint devcontainer-spawn jobs use for
    /// provisioning updates) at a fixed cadence well inside the server's RUNNER_OFFLINE_THRESHOLD_MS (60s),
    /// so a Chat Channel session that blocks the main polling loop for a whole idle chat thread never gets
    /// mistaken for a dead runner. Runs until cancelled; errors are swallowed (best-effort, same as
    /// PostJobProgressAsync) since a missed beat or two is harmless as long as the next one lands in time.
    /// </summary>
    private async Task RunChatLlmHeartbeatLoopAsync(HttpClient client, string baseUrl, string runId, string jobId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(20);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;
            await PostJobProgressAsync(client, baseUrl, runId, jobId, "chat_llm_active", null, ct);
        }
    }

    private async Task PostJobLogChunkAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string chunk,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        try
        {
            var msg = new HttpRequestMessage(
                HttpMethod.Post,
                $"{baseUrl}/runs/{runId}/jobs/{jobId}/logs");
            msg.Content = new StringContent(chunk, Encoding.UTF8, "text/plain");
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _console.MarkupLine($"[yellow]POST job logs warning: {(int)resp.StatusCode} {body.EscapeMarkup()}[/]");
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]POST job logs error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Fire-and-forget loop that tails a build log file and POSTs new bytes to the server
    /// every <paramref name="pollIntervalMs"/>. Stops when <paramref name="ct"/> is cancelled,
    /// after a final flush.
    /// </summary>
    private async Task TailBuildLogAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string logPath,
        CancellationToken ct,
        int pollIntervalMs = 2000)
    {
        long offset = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                offset = await FlushBuildLogAsync(client, baseUrl, runId, jobId, logPath, offset, ct);
                try { await Task.Delay(pollIntervalMs, ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { await FlushBuildLogAsync(client, baseUrl, runId, jobId, logPath, offset, CancellationToken.None); } catch { /* best-effort final flush */ }
        }
    }

    private async Task<long> FlushBuildLogAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string logPath,
        long offset,
        CancellationToken ct)
    {
        try
        {
            if (!File.Exists(logPath)) return offset;
            var info = new FileInfo(logPath);
            if (info.Length <= offset) return offset;
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);
            var length = (int)Math.Min(info.Length - offset, 64 * 1024);
            var buffer = new byte[length];
            var read = await fs.ReadAsync(buffer.AsMemory(0, length), ct);
            if (read > 0)
            {
                var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                await PostJobLogChunkAsync(client, baseUrl, runId, jobId, chunk, ct);
                return offset + read;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Build-log tail error: {ex.Message.EscapeMarkup()}[/]");
        }
        return offset;
    }

    private async Task PatchJobStatusAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string status,
        string? conclusion,
        CancellationToken ct,
        string? sessionId = null,
        string? broadcastId = null)
    {
        try
        {
            var msg = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{baseUrl}/runs/{runId}/jobs/{jobId}");
            msg.Content = sessionId != null
                ? JsonContent.Create(new { status, conclusion, sessionId, broadcastId })
                : JsonContent.Create(new { status, conclusion });
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _console.MarkupLine($"[yellow]PATCH job status warning: {(int)resp.StatusCode} {body.EscapeMarkup()}[/]");
            }
            else
            {
                _console.MarkupLine($"[dim]Job {jobId} → {status}{(conclusion != null ? $"/{conclusion}" : "")} (✓)[/]");
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]PATCH job status error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private async Task ExecuteGitPushJobAsync(AgenticsRunnerRegistration registration, RunnerJob job, Settings settings, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";
        var payload = job.AgentDef!.GitPushPayload!;
        var verbose = settings.Verbose;

        // 1. Claim the job
        string runId;
        _console.MarkupLine($"[cyan]GitPush: claiming job {job.Id}...[/]");
        try
        {
            var claimResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/runners/generate-jitconfig",
                new { jobId = job.Id, name = "git-push-runner" },
                ct);
            claimResponse.EnsureSuccessStatusCode();
            var claimData = JsonSerializer.Deserialize<JsonElement>(
                await claimResponse.Content.ReadAsStringAsync(ct), JsonOptions);
            runId = claimData.GetProperty("runId").GetString()!;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]GitPush: failed to claim job: {ex.Message.EscapeMarkup()}[/]");
            return;
        }

        // 2. Mark in_progress
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

        // 3. Set up temp work directory
        var jobDir = Path.Combine(Path.GetTempPath(), $"agentics-git-push-{job.Id}");
        Directory.CreateDirectory(jobDir);

        try
        {
            // 4. Prepare git credentials
            var gitEnv = await PrepareGitCredentialsAsync(ct);

            // 5. Configure git identity
            await RunGitArgsAsync(["config", "--global", "user.name", "Agentics Runner"], null, verbose, ct);
            await RunGitArgsAsync(["config", "--global", "user.email", "runner@agentics.dk"], null, verbose, ct);

            // 6. Clone or initialize the target repo
            _console.MarkupLine($"[cyan]GitPush: cloning {payload.TargetRepo.EscapeMarkup()}...[/]");

            // Try cloning the repo (might be empty — that's OK)
            var cloneExit = await RunGitArgsAsync(["clone", payload.TargetRepo, jobDir], null, verbose, ct, gitEnv);
            if (cloneExit == 0)
            {
                // Check out or create the target branch
                var checkoutExit = await RunGitArgsAsync(["checkout", payload.TargetBranch], jobDir, verbose, ct);
                if (checkoutExit != 0)
                {
                    // Branch doesn't exist — create it
                    await RunGitArgsAsync(["checkout", "-b", payload.TargetBranch], jobDir, verbose, ct);
                }
            }
            else
            {
                // Clone failed (empty repo or network error) — init locally
                _console.MarkupLine("[yellow]GitPush: clone failed, initialising empty repo...[/]");
                Directory.CreateDirectory(jobDir);
                await RunGitArgsAsync(["init"], jobDir, verbose, ct);
                await RunGitArgsAsync(["remote", "add", "origin", payload.TargetRepo], jobDir, verbose, ct);
                await RunGitArgsAsync(["checkout", "-b", payload.TargetBranch], jobDir, verbose, ct);
            }

            // 7. Write all files into the work directory
            _console.MarkupLine($"[cyan]GitPush: writing {payload.Files.Count} file(s)...[/]");
            foreach (var (relativePath, content) in payload.Files)
            {
                var fullPath = Path.Combine(jobDir, relativePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content, ct);
                if (verbose)
                    _console.MarkupLine($"[dim]  wrote {relativePath.EscapeMarkup()}[/]");
            }

            // 8. Stage all changes
            var addExit = await RunGitArgsAsync(["add", "-A"], jobDir, verbose, ct);
            if (addExit != 0) throw new Exception("git add -A failed");

            // Check if there's anything to commit
            var statusResult = await RunGitOutputAsync(["status", "--porcelain"], jobDir, ct);
            if (string.IsNullOrWhiteSpace(statusResult))
            {
                _console.MarkupLine("[yellow]GitPush: nothing to commit — files are already up to date.[/]");
                await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "success", ct);
                return;
            }

            // 9. Commit
            var commitExit = await RunGitArgsAsync(["commit", "-m", payload.CommitMessage], jobDir, verbose, ct, gitEnv);
            if (commitExit != 0) throw new Exception("git commit failed");

            // 10. Push (--set-upstream handles both first push and subsequent pushes)
            _console.MarkupLine($"[cyan]GitPush: pushing to {payload.TargetBranch.EscapeMarkup()}...[/]");
            var pushExit = await RunGitArgsAsync(
                ["push", "--set-upstream", "origin", payload.TargetBranch],
                jobDir, verbose, ct, gitEnv);
            if (pushExit != 0) throw new Exception($"git push failed (exit {pushExit})");

            // 11. Get commit SHA for logs
            var commitSha = (await RunGitOutputAsync(["rev-parse", "HEAD"], jobDir, ct)).Trim();
            _console.MarkupLine($"[green]GitPush: pushed commit {commitSha.EscapeMarkup()}[/]");

            // 12. Report success
            await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id, "completed", "success", commitSha, ct);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]GitPush: failed: {ex.Message.EscapeMarkup()}[/]");
            await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id, "completed", "failure", ex.Message, ct);
        }
        finally
        {
            try { Directory.Delete(jobDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Execute a git_distribute job: clone source, apply the effective allowlist
    /// (AdminDistallowPatterns ∪ honored .agentics/.distallow), replace target tree,
    /// commit (with {sourceCommit} substituted), and push.
    /// </summary>
    private async Task ExecuteGitDistributeJobAsync(AgenticsRunnerRegistration registration, RunnerJob job, Settings settings, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";
        var payload = job.AgentDef!.DistributePayload!;
        var verbose = settings.Verbose;

        // 1. Claim the job
        string runId;
        _console.MarkupLine($"[cyan]Distribute: claiming job {job.Id}...[/]");
        try
        {
            var claimResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/runners/generate-jitconfig",
                new { jobId = job.Id, name = "git-distribute-runner" },
                ct);
            claimResponse.EnsureSuccessStatusCode();
            var claimData = JsonSerializer.Deserialize<JsonElement>(
                await claimResponse.Content.ReadAsStringAsync(ct), JsonOptions);
            runId = claimData.GetProperty("runId").GetString()!;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Distribute: failed to claim job: {ex.Message.EscapeMarkup()}[/]");
            return;
        }

        // 2. Mark in_progress
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

        // 3. Set up temp work directories. We use three:
        //   - sourceDir: shallow clone of the source repo
        //   - filteredDir: staging area for files that survived the allowlist
        //   - targetDir: clone (or init) of the target repo where we replace contents
        var workRoot = Path.Combine(Path.GetTempPath(), $"agentics-git-distribute-{job.Id}");
        var sourceDir = Path.Combine(workRoot, "source");
        var filteredDir = Path.Combine(workRoot, "filtered");
        var targetDir = Path.Combine(workRoot, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(filteredDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            // 4. Prepare git credentials (used for both source clone and target push).
            //    pks-cli's PrepareGitCredentialsAsync covers the github.com case via the
            //    GitHub App flow. Target-side auth for non-github hosts (e.g. ADO) is
            //    expected to be set up out-of-band by `pks-cli init`.
            var gitEnv = await PrepareGitCredentialsAsync(ct);

            // Never block on an interactive password prompt — if credentials are missing
            // for the target host (ADO, GitLab, etc.) we want git to fail fast so the
            // job reports back to the admin UI instead of hanging the runner.
            gitEnv["GIT_TERMINAL_PROMPT"] = "0";
            gitEnv["GCM_INTERACTIVE"] = "Never";

            await RunGitArgsAsync(["config", "--global", "user.name", "Agentics Runner"], null, verbose, ct);
            await RunGitArgsAsync(["config", "--global", "user.email", "runner@agentics.dk"], null, verbose, ct);

            // 5. Shallow-clone the source repo at the requested branch.
            _console.MarkupLine($"[cyan]Distribute: cloning source {payload.SourceRepo.EscapeMarkup()} ({payload.SourceBranch.EscapeMarkup()})...[/]");
            var sourceCloneExit = await RunGitArgsAsync(
                ["clone", "--depth", "1", "--branch", payload.SourceBranch, payload.SourceRepo, sourceDir],
                null, verbose, ct, gitEnv);
            if (sourceCloneExit != 0)
                throw new Exception($"git clone source failed (exit {sourceCloneExit})");

            // 6. Capture source HEAD SHA for the commit message token.
            var sourceCommit = (await RunGitOutputAsync(["rev-parse", "HEAD"], sourceDir, ct)).Trim();
            _console.MarkupLine($"[dim]  source HEAD: {sourceCommit.EscapeMarkup()}[/]");

            // 7. Read .agentics/.distallow from source (if honored & present).
            var distallowFromFile = new List<string>();
            if (payload.HonorDistallow)
            {
                var distallowPath = Path.Combine(sourceDir, ".agentics", ".distallow");
                if (File.Exists(distallowPath))
                {
                    foreach (var raw in await File.ReadAllLinesAsync(distallowPath, ct))
                    {
                        var line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;
                        distallowFromFile.Add(line);
                    }
                    _console.MarkupLine($"[dim]  loaded {distallowFromFile.Count} pattern(s) from .agentics/.distallow[/]");
                }
            }

            // 8. Build effective allowlist = admin patterns ∪ file patterns.
            var effectivePatterns = new List<string>();
            effectivePatterns.AddRange(payload.AdminDistallowPatterns);
            effectivePatterns.AddRange(distallowFromFile);
            if (effectivePatterns.Count == 0)
            {
                throw new Exception("Effective allowlist is empty — refusing to distribute. " +
                    "Add patterns in the admin UI or include a `.agentics/.distallow` in the source repo.");
            }

            var matcher = new Matcher();
            matcher.AddIncludePatterns(effectivePatterns);

            // 9. Walk the source tree (excluding .git/), copy allowed files into filteredDir.
            var matchResult = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(sourceDir)));
            if (!matchResult.HasMatches)
            {
                throw new Exception("No files matched the effective allowlist; nothing to distribute.");
            }
            var copiedCount = 0;
            foreach (var hit in matchResult.Files)
            {
                // hit.Path is POSIX-style relative to sourceDir.
                var rel = hit.Path.Replace('\\', '/');
                if (rel.StartsWith(".git/") || rel == ".git") continue;

                var srcFile = Path.Combine(sourceDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var dstFile = Path.Combine(filteredDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                File.Copy(srcFile, dstFile, overwrite: true);
                copiedCount++;
                if (verbose) _console.MarkupLine($"[dim]  + {rel.EscapeMarkup()}[/]");
            }
            _console.MarkupLine($"[cyan]Distribute: {copiedCount} file(s) survived the allowlist[/]");

            // 10. Clone the target repo. For empty repos (typical first-distribute case)
            //     ADO/GitHub send a warning but `git clone` still exits 0 with an empty
            //     working tree — that's fine for us. A non-zero exit here means auth or
            //     network failure; we don't fall back to `git init` because the customer
            //     must pre-create the target repo and a local init wouldn't fix auth.
            _console.MarkupLine($"[cyan]Distribute: preparing target {payload.TargetRepo.EscapeMarkup()}...[/]");
            Directory.Delete(targetDir, recursive: true);
            var (targetCloneExit, _, targetCloneStderr) = await RunGitCaptureAsync(
                ["clone", payload.TargetRepo, targetDir], null, ct, gitEnv);
            if (targetCloneExit != 0)
            {
                throw new Exception(
                    $"git clone target failed (exit {targetCloneExit}). " +
                    $"For non-github targets (Azure DevOps, GitLab, …) configure runner-local " +
                    $"credentials for {new Uri(payload.TargetRepo).Host}. " +
                    $"stderr: {Truncate(targetCloneStderr, 400)}");
            }
            var checkoutExit = await RunGitArgsAsync(["checkout", payload.TargetBranch], targetDir, verbose, ct);
            if (checkoutExit != 0)
                await RunGitArgsAsync(["checkout", "-b", payload.TargetBranch], targetDir, verbose, ct);

            // 11. Replace target tree: delete everything except .git, then copy filtered files in.
            foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir))
            {
                var name = Path.GetFileName(entry);
                if (name == ".git") continue;
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
            CopyDirectory(filteredDir, targetDir);

            // 12. Stage and check for diff.
            var addExit = await RunGitArgsAsync(["add", "-A"], targetDir, verbose, ct);
            if (addExit != 0) throw new Exception("git add -A failed in target");

            var statusResult = await RunGitOutputAsync(["status", "--porcelain"], targetDir, ct);
            if (string.IsNullOrWhiteSpace(statusResult))
            {
                _console.MarkupLine("[yellow]Distribute: target is already up to date — nothing to push.[/]");
                await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id, "completed", "success",
                    $"no-op: target already at source {sourceCommit}", ct);
                return;
            }

            // 13. Resolve commit message tokens.
            var resolvedMessage = payload.CommitMessage.Replace("{sourceCommit}", sourceCommit);

            var commitExit = await RunGitArgsAsync(["commit", "-m", resolvedMessage], targetDir, verbose, ct, gitEnv);
            if (commitExit != 0) throw new Exception("git commit failed in target");

            // 14. Push (no --force in v1).
            _console.MarkupLine($"[cyan]Distribute: pushing to {payload.TargetBranch.EscapeMarkup()}...[/]");
            var (pushExit, _, pushStderr) = await RunGitCaptureAsync(
                ["push", "--set-upstream", "origin", payload.TargetBranch],
                targetDir, ct, gitEnv);
            if (pushExit != 0)
            {
                throw new Exception(
                    $"git push failed (exit {pushExit}). stderr: {Truncate(pushStderr, 400)}");
            }

            var targetCommit = (await RunGitOutputAsync(["rev-parse", "HEAD"], targetDir, ct)).Trim();
            _console.MarkupLine($"[green]Distribute: pushed {targetCommit.EscapeMarkup()} to {payload.TargetRepo.EscapeMarkup()}[/]");

            await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id, "completed", "success",
                $"source={sourceCommit} target={targetCommit} files={copiedCount}", ct);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Distribute: failed: {ex.Message.EscapeMarkup()}[/]");
            await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id, "completed", "failure", ex.Message, ct);
        }
        finally
        {
            try { Directory.Delete(workRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Executes a chat-llm:v1 Job (Kind B, external/alp-spec/2026-03-30-draft/spec/13-chat.md). This is a
    /// bare Job — no devcontainer, no Operator, no repository. The Runner process itself claims the Job,
    /// then dials the Chat Channel directly (outbound-only, per the spec's Invariants) and forwards every
    /// chat.completion.request frame it receives to a backend, translating that backend's replies into
    /// chat.completion.chunk/done/error frames as each event arrives. When <paramref name="backendUrl"/>
    /// is set, requests forward verbatim to that OpenAI-compatible URL and the backend credential
    /// (<paramref name="backendKey"/>) never leaves this process, mirroring pks-agent-gateway's "forward
    /// unchanged, never rewrite the caller's own key" posture (projects/pks-agent-gateway/src/gateway/proxy.go).
    /// Otherwise <paramref name="chatLlmModelId"/> is resolved via AgentChatProviderFactory (the same
    /// Foundry/Anthropic/Azure OpenAI credential resolution `pks agent` uses) — the Server only ever sees
    /// the resulting chunks either way.
    /// </summary>
    private async Task ExecuteChatLlmJobAsync(
        AgenticsRunnerRegistration registration,
        RunnerJob job,
        string? backendUrl,
        string? backendKey,
        string chatLlmModelId,
        bool verbose,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";

        // 1. Claim the job
        string runId;
        _console.MarkupLine($"[cyan]ChatLlm: claiming job {job.Id}...[/]");
        try
        {
            var claimResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/runners/generate-jitconfig",
                new { jobId = job.Id, name = "chat-llm-runner" },
                ct);
            claimResponse.EnsureSuccessStatusCode();
            var claimData = JsonSerializer.Deserialize<JsonElement>(
                await claimResponse.Content.ReadAsStringAsync(ct), JsonOptions);
            runId = claimData.GetProperty("runId").GetString()!;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]ChatLlm: failed to claim job: {ex.Message.EscapeMarkup()}[/]");
            return;
        }

        // 2. Mark in_progress
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

        // 3. Dial the Chat Channel and serve turns until an explicit chat.end arrives (either direction)
        //    or the channel drops. Per 13-chat.md, Kind B reconnect is "just accept a new connection for
        //    the same Job" — so on an unexpected drop we redial once before giving up.
        //
        //    This loop can legitimately block for the entire lifetime of a chat thread (a Chat Job is
        //    opened once per thread, not once per turn), which starves the outer polling loop in
        //    ExecuteAsync — the only place that otherwise refreshes this runner's lastSeen. Left alone,
        //    an idle-but-healthy chat (no turns for 60s+) would have the server's RUNNER_OFFLINE_THRESHOLD_MS
        //    logic decide the runner died mid-job and force-cancel the Job out from under it. Run a
        //    heartbeat concurrently so lastSeen keeps refreshing while this loop is busy.
        var channelUrl = BuildChatChannelUrl(registration, job.Id);
        string? reason = null;
        var failed = false;
        var reconnected = false;

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunChatLlmHeartbeatLoopAsync(client, baseUrl, runId, job.Id, heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    _console.MarkupLine($"[cyan]ChatLlm: opening Chat Channel for job {job.Id}{(reconnected ? " (reconnect)" : "")}...[/]");
                    await ws.ConnectAsync(channelUrl, ct);
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]ChatLlm: failed to open Chat Channel: {ex.Message.EscapeMarkup()}[/]");
                    reason = ex.Message;
                    failed = true;
                    break;
                }

                var (ended, endReason, sessionFailed) =
                    await RunChatLlmChannelSessionAsync(
                        ws, job.Id, backendUrl, backendKey, chatLlmModelId, registration.Profile?.ChatModels, verbose, ct);

                if (ended || reconnected || ct.IsCancellationRequested)
                {
                    reason = endReason ?? "chat.end";
                    failed = sessionFailed;
                    break;
                }

                reconnected = true;
                _console.MarkupLine("[yellow]ChatLlm: Chat Channel dropped unexpectedly, reconnecting once...[/]");
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { /* expected on cancel */ }
        }

        // 4. Report the Job outcome — a Chat Job reports success/failure like any other Job once it
        //    concludes, regardless of how the chat itself ended (08-runner.md Job Outcome Schema).
        await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id,
            "completed", failed ? "failure" : "success", reason ?? "chat.end", ct);
    }

    /// <summary>
    /// Builds the Chat Channel WebSocket URL for a chat-llm:v1 Job, reusing the exact auth convention the
    /// existing /api/lives/broadcast/ws connection already uses to authenticate a Runner/Operator: the
    /// token as a `?token=` query parameter (see external/vibecast/internal/broadcast/broadcast.go,
    /// `broadcastPath += "&token=" + token`) rather than a new mechanism.
    /// </summary>
    private static Uri BuildChatChannelUrl(AgenticsRunnerRegistration registration, string jobId)
    {
        var serverUri = new Uri(registration.Server);
        var scheme = string.Equals(serverUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return new Uri($"{scheme}://{serverUri.Authority}/api/lives/chat/channel/ws" +
            $"?jobId={Uri.EscapeDataString(jobId)}&token={Uri.EscapeDataString(registration.Token)}");
    }

    /// <summary>
    /// Drives a single Chat Channel connection for a chat-llm:v1 Job: receives frames and, for every
    /// chat.completion.request, forwards it to either the explicit <paramref name="backendUrl"/> override
    /// (verbatim OpenAI-compatible forward) or, when unset, to whatever AgentChatProviderFactory resolves
    /// <paramref name="chatLlmModelId"/> to — and streams back chat.completion.chunk/done/error frames.
    /// Returns Ended=false when the socket dropped without an explicit chat.end (caller may redial),
    /// Ended=true once either side sent chat.end.
    /// </summary>
    /// <param name="chatModelAllowlist">
    /// <see cref="RunnerProfile.ChatModels"/> from the persisted profile (Phase 3), or null/empty for
    /// "no restriction". This is a real enforcement gate, not a display preference: a
    /// chat.completion.request naming a disallowed model is rejected with a chat.completion.error
    /// frame instead of being resolved, and chat.models.request's response list is filtered to it.
    /// Only applies to the provider-resolution path -- literal-forward mode (<paramref name="backendUrl"/>
    /// set) is unaffected either way, matching the existing "empty model list on backendUrl" behavior.
    /// </param>
    private async Task<(bool Ended, string? Reason, bool Failed)> RunChatLlmChannelSessionAsync(
        ClientWebSocket ws, string jobId, string? backendUrl, string? backendKey, string chatLlmModelId,
        IReadOnlyList<string>? chatModelAllowlist, bool verbose, CancellationToken ct)
    {
        using var backendClient = _httpClientFactory.CreateClient();
        // Chat turns can run for minutes (long completions) — never let HttpClient's 100s default abort
        // an in-flight backend stream. Mirrors pks-agent-gateway's WriteTimeout: 0 posture.
        backendClient.Timeout = Timeout.InfiniteTimeSpan;

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var (message, closed) = await ReceiveChatFrameTextAsync(ws, ct);
                if (closed)
                    return (false, "channel closed by peer", false);
                if (message is null)
                    continue;

                JsonElement frame;
                try
                {
                    frame = JsonSerializer.Deserialize<JsonElement>(message, JsonOptions);
                }
                catch (JsonException)
                {
                    continue; // ignore a malformed frame rather than tearing down the whole channel
                }

                if (verbose)
                    _console.MarkupLine($"[grey]«[/] {TruncateHead(message, 500).EscapeMarkup()}");

                var type = frame.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                switch (type)
                {
                    case "chat.completion.request":
                    {
                        var requestId = frame.TryGetProperty("requestId", out var ridProp) ? ridProp.GetString() ?? "" : "";
                        if (frame.TryGetProperty("body", out var bodyProp))
                        {
                            var requestedModel = bodyProp.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
                            // Enforcement gate (Phase 3): the request body's "model" can name
                            // anything, so it must be checked against the persisted allowlist before
                            // ever reaching AgentChatProviderFactory.ResolveAsync -- otherwise
                            // ChatModels is a display preference, not a real allowlist. The decision
                            // lives in DecideChatCompletionRoute so a test can pin the wiring.
                            var decision = DecideChatCompletionRoute(
                                backendUrl, requestedModel, chatLlmModelId, chatModelAllowlist);
                            switch (decision.Route)
                            {
                                case ChatCompletionRoute.Forward:
                                    await ForwardChatCompletionRequestAsync(
                                        ws, backendClient, backendUrl!, backendKey, jobId, requestId, bodyProp, verbose, ct);
                                    break;
                                case ChatCompletionRoute.Rejected:
                                    await SendChatFrameAsync(ws, new
                                    {
                                        type = "chat.completion.error",
                                        jobId,
                                        requestId,
                                        error = $"Model '{decision.ModelId}' is not in this runner's configured chat-model allowlist."
                                    }, verbose, ct);
                                    break;
                                default:
                                    await ForwardChatCompletionRequestViaProviderAsync(
                                        ws, decision.ModelId!, jobId, requestId, bodyProp, verbose, ct);
                                    break;
                            }
                        }
                        break;
                    }
                    case "chat.models.request":
                    {
                        var requestId = frame.TryGetProperty("requestId", out var ridProp) ? ridProp.GetString() ?? "" : "";
                        IReadOnlyList<string> models = Array.Empty<string>();
                        // Literal-forward mode bypasses the factory entirely, so its model list would be
                        // misleading — respond with an empty list rather than querying it.
                        if (string.IsNullOrEmpty(backendUrl))
                        {
                            try { models = await _chatProviderFactory.ListAvailableModelsAsync(ct); }
                            catch { /* empty list on failure */ }
                            // Allowlist (Phase 3): only applies to the provider path -- the
                            // literal-forward "empty on backendUrl" behavior above is preserved as-is.
                            models = FilterModelsByAllowlist(models, chatModelAllowlist);
                        }
                        await SendChatFrameAsync(ws, new { type = "chat.models.response", jobId, requestId, models }, verbose, ct);
                        break;
                    }
                    case "chat.end":
                    {
                        var endReason = frame.TryGetProperty("reason", out var reasonProp) ? reasonProp.GetString() : null;
                        return (true, endReason ?? "chat.end", false);
                    }
                    default:
                        // Unknown/forward-compatible frame type — ignore rather than fail the session.
                        break;
                }
            }

            return (false, "cancelled", false);
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled", false);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, true);
        }
        finally
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Receives one full WebSocket text message, reassembling fragments until EndOfMessage.</summary>
    private static async Task<(string? Message, bool Closed)> ReceiveChatFrameTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (null, true);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    /// <summary>
    /// Forwards one chat.completion.request body verbatim to the configured OpenAI-compatible backend
    /// (HttpCompletionOption.ResponseHeadersRead, no response buffering) and translates its SSE stream
    /// into chat.completion.chunk frames the instant each event is parsed — no batching across events,
    /// mirroring pks-agent-gateway's FlushInterval=-1 unbuffered-streaming guarantee
    /// (projects/pks-agent-gateway/src/gateway/proxy.go) — ending with chat.completion.done, or
    /// chat.completion.error on failure.
    /// </summary>
    private async Task ForwardChatCompletionRequestAsync(
        ClientWebSocket ws,
        HttpClient backendClient,
        string backendUrl,
        string? backendKey,
        string jobId,
        string requestId,
        JsonElement body,
        bool verbose,
        CancellationToken ct)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{backendUrl}/chat/completions")
            {
                Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(backendKey))
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", backendKey);
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await backendClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct);
                await SendChatFrameAsync(ws,
                    new { type = "chat.completion.error", jobId, requestId, error = $"backend returned {(int)response.StatusCode}: {Truncate(errBody, 2000)}" },
                    verbose, ct);
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            var stream = await response.Content.ReadAsStreamAsync(ct);
            await using (stream.ConfigureAwait(false))
            {
                if (contentType != null && contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    while (!reader.EndOfStream)
                    {
                        ct.ThrowIfCancellationRequested();
                        var line = await reader.ReadLineAsync();
                        if (line is null) break;
                        if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                            continue;

                        var data = line["data:".Length..].TrimStart();
                        if (data.Length == 0) continue;
                        if (data == "[DONE]") break;

                        JsonElement chunk;
                        try { chunk = JsonSerializer.Deserialize<JsonElement>(data, JsonOptions); }
                        catch (JsonException) { continue; }

                        await SendChatFrameAsync(ws, new { type = "chat.completion.chunk", jobId, requestId, chunk }, verbose, ct);
                    }
                }
                else
                {
                    // Non-streaming backend response — forward the single completion object as one chunk.
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var chunk = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
                        await SendChatFrameAsync(ws, new { type = "chat.completion.chunk", jobId, requestId, chunk }, verbose, ct);
                    }
                }
            }

            await SendChatFrameAsync(ws, new { type = "chat.completion.done", jobId, requestId }, verbose, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                await SendChatFrameAsync(ws, new { type = "chat.completion.error", jobId, requestId, error = ex.Message }, verbose, ct);
            }
            catch { /* channel may already be gone — the job-level outcome still gets reported by the caller */ }
        }
    }

    /// <summary>
    /// Enforcement check for <see cref="RunnerProfile.ChatModels"/> (Phase 3, docs/remote-runner-targets-plan.md
    /// "Chat model exposure is an enforcement gap"). <c>internal static</c> so it's unit-testable in
    /// isolation without a live WebSocket/provider harness -- see
    /// tests/Commands/Agentics/AgenticsRunnerChatModelAllowlistTests.cs. A null or empty allowlist
    /// means "no restriction configured" (auto/every resolvable model), matching
    /// <see cref="RunnerProfile.ChatModels"/>'s documented null semantics.
    /// </summary>
    /// <summary>
    /// Where a <c>chat.completion.request</c> frame is routed. Extracted as a testable seam so the
    /// allowlist ENFORCEMENT (the wiring), not just the truth table, is pinned by a test -- stubbing
    /// the guard out used to leave the whole unit suite green. See
    /// tests/Commands/Agentics/AgenticsRunnerChatModelAllowlistTests.cs.
    /// </summary>
    internal enum ChatCompletionRoute
    {
        /// <summary>Literal --chat-llm-backend-url forwarding; bypasses the provider factory (and the allowlist).</summary>
        Forward,
        /// <summary>Resolve via AgentChatProviderFactory for <see cref="ChatCompletionDecision.ModelId"/>.</summary>
        Provider,
        /// <summary>Requested model is not in the configured allowlist -- answer chat.completion.error.</summary>
        Rejected,
    }

    internal readonly record struct ChatCompletionDecision(ChatCompletionRoute Route, string? ModelId);

    /// <summary>
    /// Decides how one chat.completion.request is served. Pure so the enforcement path can be
    /// asserted without a WebSocket/provider harness.
    /// </summary>
    internal static ChatCompletionDecision DecideChatCompletionRoute(
        string? backendUrl, string? requestedModel, string? defaultModelId, IReadOnlyList<string>? allowlist)
    {
        if (!string.IsNullOrEmpty(backendUrl))
            return new ChatCompletionDecision(ChatCompletionRoute.Forward, null);

        var effectiveModelId = string.IsNullOrWhiteSpace(requestedModel) ? defaultModelId : requestedModel;
        return IsChatModelAllowed(effectiveModelId, allowlist)
            ? new ChatCompletionDecision(ChatCompletionRoute.Provider, effectiveModelId)
            : new ChatCompletionDecision(ChatCompletionRoute.Rejected, effectiveModelId);
    }

    internal static bool IsChatModelAllowed(string? modelId, IReadOnlyList<string>? allowlist)
    {
        if (allowlist is null || allowlist.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        return allowlist.Contains(modelId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters a resolved model list down to the persisted allowlist for the chat.models.request
    /// response (Phase 3) -- same null/empty "no restriction" semantics as <see cref="IsChatModelAllowed"/>.
    /// </summary>
    internal static IReadOnlyList<string> FilterModelsByAllowlist(IReadOnlyList<string> models, IReadOnlyList<string>? allowlist)
    {
        if (allowlist is null || allowlist.Count == 0)
            return models;
        return models.Where(m => allowlist.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Serves one chat.completion.request turn via AgentChatProviderFactory instead of a literal
    /// --chat-llm-backend-url: parses the OpenAI-shaped request body into a provider-neutral ChatRequest
    /// (OpenAiChatBridge.ParseRequest), resolves <paramref name="modelId"/> to a concrete IChatProvider +
    /// deployment name (a Foundry session from `pks foundry init`, or a stored Anthropic/Azure OpenAI
    /// credential — the same resolution `pks agent` already uses), and translates each ChatStreamEvent the
    /// provider yields back into a chat.completion.chunk frame (OpenAiChatBridge.ChunkBuilder) as it
    /// arrives — same streaming/error/done frame semantics as ForwardChatCompletionRequestAsync's literal
    /// HTTP forward.
    /// </summary>
    private async Task ForwardChatCompletionRequestViaProviderAsync(
        ClientWebSocket ws,
        string modelId,
        string jobId,
        string requestId,
        JsonElement body,
        bool verbose,
        CancellationToken ct)
    {
        try
        {
            var request = OpenAiChatBridge.ParseRequest(body);
            var (provider, deployment) = await _chatProviderFactory.ResolveAsync(modelId, ct);
            var chunkBuilder = new OpenAiChatBridge.ChunkBuilder($"chatcmpl-{jobId}-{requestId}", modelId);

            await foreach (var evt in provider.StreamAsync(request, deployment, ct))
            {
                var chunk = chunkBuilder.Build(evt);
                if (chunk is null) continue;
                await SendChatFrameAsync(ws, new { type = "chat.completion.chunk", jobId, requestId, chunk }, verbose, ct);
            }

            await SendChatFrameAsync(ws, new { type = "chat.completion.done", jobId, requestId }, verbose, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            try
            {
                await SendChatFrameAsync(ws, new { type = "chat.completion.error", jobId, requestId, error = ex.Message }, verbose, ct);
            }
            catch { /* channel may already be gone — the job-level outcome still gets reported by the caller */ }
        }
    }

    /// <summary>Serializes and sends one Chat Channel frame as a single WebSocket text message.</summary>
    private async Task SendChatFrameAsync(ClientWebSocket ws, object frame, bool verbose, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(frame);
        if (verbose)
            _console.MarkupLine($"[grey]»[/] {TruncateHead(Encoding.UTF8.GetString(json), 500).EscapeMarkup()}");
        await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(src, dst));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(src, dst), overwrite: true);
        }
    }

    /// <summary>Read the vibecast process log file, capped at last 4000 chars for OTEL span limits.</summary>
    private static async Task<string> CaptureVibecastLogAsync(string logFile, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(logFile)) return "(no vibecast log)";
            var content = await File.ReadAllTextAsync(logFile, ct);
            return content.Length > 4000 ? "...\n" + content[^4000..] : content;
        }
        catch (Exception ex)
        {
            return $"(log read error: {ex.Message})";
        }
    }

    /// <summary>Capture last 100 lines of a tmux pane — useful for seeing Claude's terminal output on failure.</summary>
    private async Task<string> CaptureTmuxPaneAsync(string session, string window, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("tmux")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("capture-pane");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add($"{session}:{window}.0");
            psi.ArgumentList.Add("-S");
            psi.ArgumentList.Add("-100");
            var proc = Process.Start(psi);
            if (proc == null) return "(tmux capture failed)";
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return string.IsNullOrWhiteSpace(output) ? "(empty pane)" : output;
        }
        catch (Exception ex)
        {
            return $"(tmux capture error: {ex.Message})";
        }
    }

    private async Task PatchJobStatusWithLogsAsync(
        HttpClient client, string baseUrl, string runId, string jobId,
        string status, string conclusion, string logs, CancellationToken ct)
    {
        try
        {
            var msg = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{jobId}");
            msg.Content = JsonContent.Create(new { status, conclusion, logs, completionReason = conclusion });
            var resp = await client.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _console.MarkupLine($"[yellow]PATCH job status warning: {(int)resp.StatusCode} {body.EscapeMarkup()}[/]");
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]PATCH job status error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>Runs git with an explicit argument array (avoids space-splitting issues with commit messages).</summary>
    private async Task<int> RunGitArgsAsync(string[] args, string? workingDir, bool verbose, CancellationToken ct,
        Dictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (workingDir != null)
            psi.WorkingDirectory = workingDir;
        if (extraEnv != null)
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;

        var proc = Process.Start(psi);
        if (proc == null) return -1;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (verbose || proc.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                _console.MarkupLine($"[dim]{stdout.Trim().EscapeMarkup()}[/]");
            if (!string.IsNullOrWhiteSpace(stderr))
                _console.MarkupLine($"[dim]{stderr.Trim().EscapeMarkup()}[/]");
        }
        return proc.ExitCode;
    }

    /// <summary>
    /// Run git and capture stdout AND stderr alongside the exit code. Used by the
    /// distribute flow so target-side failures (auth, network, divergent history) are
    /// reported back to the admin UI with the actual git error message attached.
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr)> RunGitCaptureAsync(
        string[] args, string? workingDir, CancellationToken ct, Dictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (workingDir != null)
            psi.WorkingDirectory = workingDir;
        if (extraEnv != null)
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;

        var proc = Process.Start(psi);
        if (proc == null) return (-1, "", "");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout, stderr);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= max ? s.Trim() : "…" + s[^max..].Trim());

    /// <summary>Like <see cref="Truncate"/> but keeps the start of the string -- used for --chat-llm-verbose
    /// frame logging, where the interesting bit (frame "type") is always near the front of the JSON.</summary>
    private static string TruncateHead(string s, int max) =>
        string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= max ? s.Trim() : s[..max].Trim() + "…");

    /// <summary>Runs git and returns stdout as a string.</summary>
    private static async Task<string> RunGitOutputAsync(string[] args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi);
        if (proc == null) return "";
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return output;
    }

    private async Task ExecuteInProcessAsync(AgenticsRunnerRegistration registration, RunnerJob job, Settings settings, CancellationToken ct)
    {
        // Capture the parent span (runner.execute_job) so we can enrich it at the end
        var jobSpan = System.Diagnostics.Activity.Current;

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";

        // 1. Claim via generate-jitconfig
        string runId;
        using (var claimSpan = _activitySource.StartActivity("runner.job.claim"))
        {
            _console.MarkupLine($"[cyan]InProcess: claiming job {job.Id}...[/]");
            var claimResponse = await client.PostAsJsonAsync(
                $"{baseUrl}/runners/generate-jitconfig",
                new { jobId = job.Id, name = "inprocess-runner" },
                ct);
            claimResponse.EnsureSuccessStatusCode();
            var claimJson = await claimResponse.Content.ReadAsStringAsync(ct);
            var claimData = JsonSerializer.Deserialize<JsonElement>(claimJson, JsonOptions);
            runId = claimData.GetProperty("runId").GetString()!;
            claimSpan?.SetTag("run_id", runId);
            claimSpan?.SetTag("job_id", job.Id);
        }

        // 2. Set up work directory
        var workDir = await ResolveWorkDirAsync(settings.WorkDir, ct);
        var gitEnv = await PrepareGitCredentialsAsync(ct);
        string? jobWorkTree = null;

        try
        {
            using (var wsSpan = _activitySource.StartActivity("runner.job.setup_workspace"))
            {
                var useWorktree = settings.Worktree;
                wsSpan?.SetTag("worktree_mode", useWorktree);
                wsSpan?.SetTag("repository", job.AgentDef?.Repository ?? "");
                wsSpan?.SetTag("branch", job.AgentDef?.Branch ?? "main");
                jobWorkTree = await SetupJobWorkTreeAsync(
                    workDir, registration, job, settings.Verbose, useWorktree, ct, gitEnv);
                wsSpan?.SetTag("workspace_path", jobWorkTree);
            }

            // 2b. Restore CLAUDE_CONFIG_DIR archive from server (no-op if none stored yet).
            // This enables cross-machine resume: the previous runner's CLAUDE_CONFIG_DIR
            // (sessions, memory, settings) is restored here before Claude starts.
            await RestoreClaudeConfigArchiveAsync(client, baseUrl, registration, ct);

            // 2c. Materialize user-uploaded task assets into {jobWorkTree}/.agentics/assets/.
            // Runs on every dispatch (replace-each-time) so resumed jobs and downstream
            // stations pick up any assets the user added since the last run.
            await SyncTaskAssetsAsync(
                client,
                job.AgentDef?.TaskAssets,
                jobWorkTree: jobWorkTree,
                containerId: null,
                workspaceFolderInContainer: null,
                ct);

            // 2d. Materialize the line's static .agentics/specs/ contracts into the worktree so
            //     station prompts can `Read .agentics/specs/<contract>.md` (see devcontainer path 6c).
            if (job.AgentDef?.AgenticsSpecFiles is { Count: > 0 } specFilesInProc)
            {
                foreach (var (relPath, content) in specFilesInProc)
                {
                    var safeRel = relPath.Replace("\\", "/").TrimStart('/');
                    if (safeRel.Contains("..")) continue;
                    var dest = Path.Combine(jobWorkTree!, safeRel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllTextAsync(dest, content, ct);
                }
                _console.MarkupLine($"  [green]✓[/] materialized {specFilesInProc.Count} spec file(s) → {jobWorkTree}/.agentics/specs/");
            }

            // 3. PATCH to in_progress
            _console.MarkupLine($"[cyan]InProcess: executing job {job.Id} in {jobWorkTree}...[/]");
            var patchInProgress = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{job.Id}");
            patchInProgress.Content = JsonContent.Create(new { status = "in_progress" });
            patchInProgress.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            var res1 = await client.SendAsync(patchInProgress, ct);
            res1.EnsureSuccessStatusCode();

            // 4. Resolve vibecast binary
            var vibecastBin = ResolveVibecastBinary(settings.VibecastBinary);
            _console.MarkupLine($"[cyan]Using vibecast: {vibecastBin}[/]");

            // 5. Resolve AGENTIC_SERVER from registration
            var serverUri = new Uri(registration.Server);
            var agenticServer = $"{serverUri.Host}:{serverUri.Port}";
            var agenticsBaseUrl = $"{serverUri.Scheme}://{serverUri.Host}{(serverUri.IsDefaultPort ? "" : $":{serverUri.Port}")}";

            // 6. Create isolated VIBECAST_HOME for this instance so control socket/sessions don't conflict
            // We use VIBECAST_HOME (not HOME) so Claude Code's settings/hooks still resolve from real HOME
            var vibecastHome = Path.Combine(Path.GetTempPath(), $"vibecast-job-{job.Id}");
            Directory.CreateDirectory(vibecastHome);
            var vibecastLogFile = Path.Combine(vibecastHome, "vibecast.log");

            // Start OTLP broadcast proxy. Vibecast and Claude send all telemetry here;
            // the proxy fans out to both Aspire (for real-time dashboard) and Next.js
            // (for per-project analysis via /api/otel). Resource attrs are injected via
            // OTEL_RESOURCE_ATTRIBUTES so vibecast spans are correlated to this job.
            await using var otlpProxy = OtlpProxy.Start(analysisBaseUrl: agenticsBaseUrl);

            var agenticsProxyOptions = await BuildAgenticsProxyOptionsAsync(job, ct);
            await using var agenticsProxy = await AgenticsProxy.StartAsync(agenticsProxyOptions, _foundryAuthService, ct: ct);
            var agenticsProxyEnv = $" AGENTICS_PROXY_URL=\"http://localhost:{agenticsProxy.Port}\""
                                 + $" AGENTICS_PROXY_TOKEN=\"{agenticsProxyOptions.BootstrapToken}\"";
            _console.MarkupLine($"[dim]AgenticsProxy listening on port {agenticsProxy.Port} ({agenticsProxyOptions.AllowedHosts.Count} allowed host(s))[/]");

            var resourceAttrs = $"job.id={job.Id},run.id={runId}";
            if (!string.IsNullOrEmpty(job.AgentDef?.TaskId))
                resourceAttrs += $",task.id={job.AgentDef.TaskId}";
            if (!string.IsNullOrEmpty(job.AgentDef?.AssemblyLineId))
                resourceAttrs += $",assembly_line.id={job.AgentDef.AssemblyLineId}";
            // Signal-specific endpoint vars (full URL including path) are required by Claude Code.
            // The generic OTEL_EXPORTER_OTLP_ENDPOINT is kept for vibecast itself.
            var proxyBase = $"http://localhost:{otlpProxy.Port}";
            var otelEnv = $" OTEL_EXPORTER_OTLP_ENDPOINT=\"{proxyBase}\""
                        + $" OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=\"{proxyBase}/v1/traces\""
                        + $" OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=\"{proxyBase}/v1/logs\""
                        + $" OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=\"{proxyBase}/v1/metrics\""
                        + $" OTEL_EXPORTER_OTLP_INSECURE=true"
                        + $" OTEL_EXPORTER_OTLP_PROTOCOL=\"http/json\""
                        + $" OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=\"http/json\""
                        + $" OTEL_EXPORTER_OTLP_LOGS_PROTOCOL=\"http/json\""
                        + $" OTEL_SERVICE_NAME=\"vibecast-job\""
                        + $" OTEL_RESOURCE_ATTRIBUTES=\"{resourceAttrs}\""
                        + $" CLAUDE_CODE_ENABLE_TELEMETRY=1"
                        + $" CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1"
                        + $" OTEL_TRACES_EXPORTER=otlp"
                        + $" OTEL_LOGS_EXPORTER=otlp"
                        + $" OTEL_TRACES_EXPORT_INTERVAL=5000"
                        + $" OTEL_LOGS_EXPORT_INTERVAL=5000"
                        + $" VIBECAST_DEBUG=1";

            // 7. Start vibecast as a background process in the worktree directory
            var agentSpan = _activitySource.StartActivity("runner.job.start_agent");
            agentSpan?.SetTag("vibecast.binary", vibecastBin);
            agentSpan?.SetTag("job.id", job.Id);
            var vibecastTmux = $"vibecast-job-{job.Id[..8]}";
            _console.MarkupLine($"[cyan]Starting vibecast in tmux session '{vibecastTmux}'...[/]");

            // Kill any stale session
            await RunProcessAsync("tmux", $"kill-session -t {vibecastTmux}", null, ct);

            // Start vibecast in a detached tmux session
            var startPsi = new ProcessStartInfo("tmux")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startPsi.ArgumentList.Add("new-session");
            startPsi.ArgumentList.Add("-d");
            startPsi.ArgumentList.Add("-s");
            startPsi.ArgumentList.Add(vibecastTmux);
            startPsi.ArgumentList.Add("-x");
            startPsi.ArgumentList.Add("120");
            startPsi.ArgumentList.Add("-y");
            startPsi.ArgumentList.Add("48");
            startPsi.ArgumentList.Add("bash");
            startPsi.ArgumentList.Add("-c");
            // Reset HOME to the real user home (runner overrides HOME for its own config isolation)
            // so that Claude Code finds its settings in ~/.claude/
            var realHome = await GetRealHomeDirectoryAsync(ct);
            var defaultAppendPrompt = "When you have completed the assigned task, use the stop_broadcast MCP tool with a message summarizing what you accomplished and conclusion 'success'. If you encounter an unrecoverable error, call stop_broadcast with conclusion 'failure' and describe the issue.";
            var appendPrompt = !string.IsNullOrWhiteSpace(job.AgentDef?.AppendSystemPrompt)
                ? job.AgentDef.AppendSystemPrompt + "\n\n" + defaultAppendPrompt
                : defaultAppendPrompt;
            if (job.AgentDef?.GitignoreLines?.Count > 0)
            {
                var lines = string.Join("\n", job.AgentDef.GitignoreLines.Select(l => $"  {l}"));
                appendPrompt = $"Ensure the project's .gitignore file contains the following lines (add them if missing):\n{lines}\n\n" + appendPrompt;
            }
            // Prepend workspace path so Claude knows exactly where to write .claude/ files,
            // agents, and other project-relative paths. This is belt-and-suspenders alongside
            // the settings.json anchor written to {jobWorkTree}/.claude/ below.
            appendPrompt = $"Your project workspace root is: {jobWorkTree}\n" +
                           $"All files (including .claude/agents/, .claude/settings.json, etc.) must be written " +
                           $"relative to this directory, not to any parent directory.\n\n" +
                           appendPrompt;

            // In-process mode: the runner runs on the same machine as the developer's host Aspire
            // session. Warn Claude so it verifies before killing any aspire/system process.
            // Also warn about the DOTNET_RESOURCE_SERVICE_ENDPOINT_URL port (22057) that the host
            // session reserves — new AppHost projects must override this in launchSettings.json.
            appendPrompt = "⚠️  IN-PROCESS ENVIRONMENT: This job runs on the same machine as the developer's " +
                           "host Aspire session (Next.js, ws-relay, Keycloak, etc. are already running). " +
                           "Before using pkill, kill $(pgrep ...), kill $(lsof ...), aspire stop, or ANY " +
                           "pattern-based process kill: verify EVERY matched PID belongs to your job " +
                           "(check /proc/<pid>/cwd or ps -p <pid>). Never use broad patterns like " +
                           "`pgrep -f \"aspire run\"` without scoping to your job's PID list — " +
                           "the host session's aspire process will match and be killed. " +
                           "Track your own background PIDs explicitly (e.g. `aspire run & MY_PID=$!`) " +
                           "and kill only those. " +
                           "When starting Aspire in this job, always use `aspire run --isolated` to avoid " +
                           "port conflicts with the host session. " +
                           "IMPORTANT: the host session reserves port 22057 via DOTNET_RESOURCE_SERVICE_ENDPOINT_URL. " +
                           "If you create a new AppHost project, override this in launchSettings.json — " +
                           "e.g. set DOTNET_RESOURCE_SERVICE_ENDPOINT_URL=https://localhost:28057.\n\n" +
                           appendPrompt;
            // Inherit VIBECAST_KEYBOARD_PIN from environment if set
            var keyboardPin = Environment.GetEnvironmentVariable("VIBECAST_KEYBOARD_PIN");
            var keyboardPinEnv = !string.IsNullOrEmpty(keyboardPin) ? $" VIBECAST_KEYBOARD_PIN={keyboardPin}" : "";

            // Write the job prompt to a file so vibecast can pass it to Claude as a positional arg.
            // This avoids tmux send-keys timing issues (prompt sent before Claude is ready)
            // and multi-line escaping issues.
            var promptFile = Path.Combine(vibecastHome, "initial-prompt.txt");
            var jobPrompt = job.AgentDef?.Prompt ?? "";
            await File.WriteAllTextAsync(promptFile, jobPrompt, ct);
            var initialPromptEnv = !string.IsNullOrEmpty(jobPrompt) ? $" VIBECAST_INITIAL_PROMPT_FILE={promptFile}" : "";

            // Write appendSystemPrompt to a file to avoid shell quoting issues with special chars/JSON
            var appendPromptFile = Path.Combine(vibecastHome, "append-system-prompt.txt");
            await File.WriteAllTextAsync(appendPromptFile, appendPrompt, ct);
            var appendPromptEnv = $" VIBECAST_APPEND_SYSTEM_PROMPT_FILE={appendPromptFile}";

            // Stage git credentials and isolated stage dir (inside vibecastHome so it's job-scoped)
            var stageGitUrl = job.AgentDef?.StageGitUrl ?? "";
            var stageGitToken = job.AgentDef?.StageGitToken ?? "";
            var stageDir = Path.Combine(vibecastHome, "stage");
            var stageGitEnv = !string.IsNullOrEmpty(stageGitUrl)
                ? $" STAGE_GIT_URL={stageGitUrl} STAGE_GIT_TOKEN={stageGitToken} STAGE_DIR={stageDir}"
                : "";

            // Write .claude/ config files in the job work tree to anchor Claude Code's project
            // root detection here. Claude walks up from CWD looking for a .claude/settings.json —
            // without this file it would walk past the job worktree all the way up to the runner's
            // workspace (e.g. /workspaces/agentic-live-www) and write agents/sessions there instead.
            //
            // settings.json   — anchors project root detection + deny rules that lock Claude to
            //                   the job work tree (Claude Code is NOT CWD-restricted by default)
            // settings.local.json — pre-approves writes to .claude/** so no TUI permission prompts
            // .gitignore      — keeps runner-injected files out of git history
            {
                var claudeSettingsDir = Path.Combine(jobWorkTree, ".claude");
                Directory.CreateDirectory(claudeSettingsDir);

                // Anchor file: Claude Code's project root detection walks up looking for settings.json.
                // An empty settings.json in the job worktree stops the walk here.
                // Path isolation is enforced by the vibecast PreToolUse hook (VIBECAST_ALLOWED_DIRECTORIES).
                var claudeSettingsFile = Path.Combine(claudeSettingsDir, "settings.json");
                if (!File.Exists(claudeSettingsFile))
                    await File.WriteAllTextAsync(claudeSettingsFile, "{}\n", ct);

                // Local overrides: pre-approve writes to .claude/** so no TUI permission prompts
                var claudeLocalFile = Path.Combine(claudeSettingsDir, "settings.local.json");
                await File.WriteAllTextAsync(claudeLocalFile, """
{
  "permissions": {
    "allow": [
      "Write(.claude/**)",
      "Edit(.claude/**)",
      "Bash(mkdir**)"
    ]
  },
  "enableAllProjectMcpServers": true
}
""", ct);
                // Write a CLAUDE.md that anchors project root, instructs Claude on isolation rules,
                // and warns about shared system resources to avoid interfering with the host env.
                var claudeMdFile = Path.Combine(jobWorkTree, "CLAUDE.md");
                if (!File.Exists(claudeMdFile))
                    await File.WriteAllTextAsync(claudeMdFile, $"""
# Job Environment

This is a runner job directory. Your workspace is `{jobWorkTree}`.

## Aspire

When starting Aspire in this job, use `--isolated` to get randomized ports:

```bash
aspire run --isolated --non-interactive
```

Before stopping or killing any Aspire or system process, check it belongs to this job
(verify PID cwd, session name, or port). Use `aspire ps` to see running instances.

## Working directory

All files must be created under `{jobWorkTree}`. Do not write to parent directories.
""", ct);

                // Write a targeted .claude/.gitignore that only ignores job/session-scoped files.
                // Do NOT use a blanket "*" — project-level files like settings.json and MCP
                // configs written by tools such as `aspire agent init` should be committable.
                var claudeGitignoreFile = Path.Combine(claudeSettingsDir, ".gitignore");
                if (!File.Exists(claudeGitignoreFile))
                    await File.WriteAllTextAsync(claudeGitignoreFile,
                        "# Runner-injected — never commit\n" +
                        "settings.local.json\n", ct);
                _console.MarkupLine($"[cyan]Pre-wrote .claude/settings.json + settings.local.json + .gitignore in {jobWorkTree}[/]");
            }

            // Propagate W3C trace context so logs/vibecast output can be correlated
            var traceparentEnv = !string.IsNullOrEmpty(job.AgentDef?.Traceparent)
                ? $" TRACEPARENT={job.AgentDef.Traceparent} AGENTICS_TRACE_ID={job.AgentDef.Traceparent.Split('-').ElementAtOrDefault(1) ?? ""}"
                : "";

            // If this is a retry of a timed-out session, pass the prior claudeSessionId so vibecast
            // can forward --resume to Claude Code and pick up the conversation thread
            var resumeEnv = !string.IsNullOrEmpty(job.AgentDef?.ResumeSessionId)
                ? $" VIBECAST_RESUME_SESSION_ID={job.AgentDef.ResumeSessionId}"
                : "";
            // Do NOT pass BROADCAST_ID for continue/resume jobs. Reusing an old broadcast ID causes
            // ws-relay to silently reject the broadcaster WebSocket reconnect, so the runner's
            // isActive check never becomes true and the job idle-times out immediately.
            // Vibecast will generate a fresh session ID; Claude still resumes via --resume <sessionId>.
            // if (!string.IsNullOrEmpty(job.AgentDef?.ResumeBroadcastId))
            //     resumeEnv += $" BROADCAST_ID={job.AgentDef.ResumeBroadcastId}";

            // Auto-git: tell vibecast's stop hook to block Claude from stopping with uncommitted changes
            var autoGitEnv = "";
            if (job.AgentDef?.AutoGit == true)
            {
                autoGitEnv = " AGENTICS_AUTO_GIT=1";
                if (!string.IsNullOrWhiteSpace(job.AgentDef.CommitMessageTemplate))
                {
                    var escapedHint = job.AgentDef.CommitMessageTemplate.Replace("'", "'\\''");
                    autoGitEnv += $" AGENTICS_COMMIT_MESSAGE_HINT='{escapedHint}'";
                }
                // Pass the project repo token separately — AGENTICS_TOKEN is the runner API token
                // and is NOT accepted by the git server's repo.git endpoint.
                if (!string.IsNullOrWhiteSpace(job.AgentDef.ProjectRepoToken))
                    autoGitEnv += $" AGENTICS_REPO_TOKEN='{job.AgentDef.ProjectRepoToken}'";
            }

            // Operator: auto-approve image uploads so headless stations don't require TUI interaction
            var autoApproveEnv = job.AgentDef?.OperatorConfig?.AutoApproveImageUploads == true
                ? " VIBECAST_AUTO_APPROVE_IMAGES=1"
                : "";

            // Operator: disable Claude Code background tasks to prevent subagent stalling
            var disableBackgroundTasksEnv = job.AgentDef?.OperatorConfig?.DisableBackgroundTasks == true
                ? " CLAUDE_CODE_DISABLE_BACKGROUND_TASKS=1"
                : "";

            // Operator: disable Claude Sonnet's 1M context window unless explicitly opted in
            // server-side. Server always emits Enable1mContext; a missing OperatorConfig means
            // an old server build, so default to disabling 1M (safer/cheaper).
            var disable1mContextEnv = job.AgentDef?.OperatorConfig?.Enable1mContext == true
                ? ""
                : " CLAUDE_CODE_DISABLE_1M_CONTEXT=1";

            // Operator: per-station model/effort. Passed through verbatim; vibecast maps to
            // `claude --model`/`--effort` and resolves the specific-model → tier fallback.
            var modelEnv = !string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Model)
                ? $" VIBECAST_CLAUDE_MODEL='{job.AgentDef!.OperatorConfig!.Model}'"
                : "";
            var modelTierEnv = !string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.ModelTier)
                ? $" VIBECAST_CLAUDE_MODEL_TIER='{job.AgentDef!.OperatorConfig!.ModelTier}'"
                : "";
            var effortEnv = !string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Effort)
                ? $" VIBECAST_CLAUDE_EFFORT='{job.AgentDef!.OperatorConfig!.Effort}'"
                : "";
            // Operator: which coding-agent CLI vibecast runs (claude|codex|pi); unset → claude.
            var agentEnv = !string.IsNullOrWhiteSpace(job.AgentDef?.OperatorConfig?.Agent)
                ? $" VIBECAST_AGENT='{job.AgentDef!.OperatorConfig!.Agent}'"
                : "";

            // SubagentStart hook: inject additionalContext into every spawned subagent
            var subagentSuffixEnv = "";
            if (!string.IsNullOrWhiteSpace(job.AgentDef?.SubagentPromptAppendix))
            {
                var suffixFile = Path.Combine(vibecastHome, "subagent-prompt-suffix.txt");
                await File.WriteAllTextAsync(suffixFile, job.AgentDef.SubagentPromptAppendix, ct);
                subagentSuffixEnv = $" SUBAGENT_PROMPT_SUFFIX_FILE={suffixFile}";
            }

            var broadcastIdEnv = !string.IsNullOrEmpty(job.AgentDef?.BroadcastId)
                ? $" BROADCAST_ID={job.AgentDef.BroadcastId}"
                : "";

            // Kind A chat-session:v1 (external/alp-spec/2026-03-30-draft/spec/13-chat.md): tells the
            // spawned vibecast (Operator) to open its Chat Channel for this job's main pane. vibecast
            // gates this behind AGENTICS_CHAT_SESSION=1 alongside the AGENTICS_JOB_ID already set below
            // — see maybeStartChatChannel in external/vibecast/internal/stream/stream.go.
            var chatSessionEnv = job.IsChatSession ? " AGENTICS_CHAT_SESSION=1" : "";

            // initBranch: create a task-scoped branch in the worktree before launching Claude
            if (job.AgentDef?.InitBranch == true && !string.IsNullOrEmpty(job.AgentDef.TaskId))
            {
                var branchName = $"task/{job.AgentDef.TaskId}";
                try
                {
                    await RunProcessAsync("git", $"-C {jobWorkTree} checkout -b {branchName}", null, ct);
                }
                catch
                {
                    // Branch may already exist — try checking it out instead
                    await RunProcessAsync("git", $"-C {jobWorkTree} checkout {branchName}", null, ct);
                }
            }

            // Clone plugins and write agent plugin dirs locally — pks-cli and vibecast share the same FS
            var allLocalPluginPaths = new List<string>();

            if (job.AgentDef?.Plugins?.Count > 0)
            {
                using var pluginSpan = _activitySource.StartActivity("runner.job.clone_plugins");
                pluginSpan?.SetTag("plugin.count", job.AgentDef.Plugins.Count);
                pluginSpan?.SetTag("plugin.ids", string.Join(",", job.AgentDef.Plugins.Select(p => p.Id)));
                var localPluginPaths = await ClonePluginsLocallyAsync(
                    job.AgentDef.Plugins, job.Id, workDir, ct);
                allLocalPluginPaths.AddRange(localPluginPaths);
                pluginSpan?.SetTag("plugin.paths", string.Join(",", localPluginPaths));
            }

            if (job.AgentDef?.Agents?.Count > 0)
            {
                var agentPluginPath = CreateAgentPluginDirLocally(
                    job.AgentDef.Agents, job.Id, workDir);
                if (agentPluginPath != null)
                    allLocalPluginPaths.Add(agentPluginPath);
            }

            var extraPluginsEnv = allLocalPluginPaths.Count > 0
                ? $" VIBECAST_EXTRA_PLUGINS=\"{string.Join(":", allLocalPluginPaths)}\""
                : "";

            // Set VIBECAST_ALLOWED_DIRECTORIES so vibecast uses the job dir as Claude's working
            // directory (instead of git-root detection which could find the runner's workspace).
            // Note: Claude Code is NOT CWD-restricted by default — path isolation is enforced via
            // deny rules in .claude/settings.json written above.
            var allowedDirsEnv = $" VIBECAST_ALLOWED_DIRECTORIES=\"{jobWorkTree}\"";

            // CLAUDE_CONFIG_DIR: project-scoped config dir in /tmp so all jobs for the same
            // project share one Claude config. Combined with the stable task CWD this means
            // session transcripts accumulate in one place and --resume always works.
            var claudeConfigDirEnv = $" CLAUDE_CONFIG_DIR=/tmp/agentic-{registration.Owner}-{registration.Project}";

            // Redirect vibecast stdout+stderr to a log file so failures are diagnosable.
            // The log is captured on timeout and included in the job PATCH logs field.
            var vibecastLogRedirect = $" > \"{vibecastLogFile}\" 2>&1";

            // Forward Anthropic provider env from the runner host into the job (real
            // key or the pks-agent-gateway LLM sim). Explicit rather than relying on
            // tmux env inheritance — the tmux server may predate this process's env.
            // localhost URLs work as-is on-host (no container rebase needed here).
            var anthropicEnv = "";
            var anthropicKeyInProc = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            var anthropicBaseInProc = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
            if (!string.IsNullOrEmpty(anthropicKeyInProc))
                anthropicEnv += $" ANTHROPIC_API_KEY='{anthropicKeyInProc}'";
            if (!string.IsNullOrEmpty(anthropicBaseInProc))
                anthropicEnv += $" ANTHROPIC_BASE_URL='{anthropicBaseInProc}'";
            // OpenAI env-key auth for a codex Operator (see the spawn-path note above).
            var openaiKeyInProc = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrEmpty(openaiKeyInProc))
                anthropicEnv += $" OPENAI_API_KEY='{openaiKeyInProc}'";

            startPsi.ArgumentList.Add($"cd {jobWorkTree} && HOME={realHome} VIBECAST_HOME={vibecastHome} VIBECAST_BIN={vibecastBin} AGENTICS_SERVER={agenticServer} AGENTIC_SERVER={agenticServer} AGENTICS_PROJECT={registration.Owner}/{registration.Project} AGENTICS_JOB_ID={job.Id} AGENTICS_TOKEN='{registration.Token}' AGENTICS_OWNER='{registration.Owner}' AGENTICS_PROJECT_NAME='{registration.Project}' AGENTICS_BASE_URL='{agenticsBaseUrl}' AGENTICS_JOB_MODE=1{keyboardPinEnv}{initialPromptEnv}{appendPromptEnv}{stageGitEnv}{extraPluginsEnv}{traceparentEnv}{resumeEnv}{autoGitEnv}{autoApproveEnv}{disableBackgroundTasksEnv}{disable1mContextEnv}{modelEnv}{modelTierEnv}{effortEnv}{agentEnv}{subagentSuffixEnv}{broadcastIdEnv}{chatSessionEnv}{allowedDirsEnv}{claudeConfigDirEnv}{otelEnv}{agenticsProxyEnv}{anthropicEnv} {vibecastBin}{vibecastLogRedirect}");

            var startProc = Process.Start(startPsi);
            if (startProc != null)
            {
                await startProc.WaitForExitAsync(ct);
                if (startProc.ExitCode != 0)
                {
                    var stderr = await startProc.StandardError.ReadToEndAsync(ct);
                    _console.MarkupLine($"[red]Failed to start vibecast tmux session: {stderr.EscapeMarkup()}[/]");
                    throw new InvalidOperationException($"tmux new-session failed (exit {startProc.ExitCode})");
                }
            }

            // Track this job for cleanup on shutdown
            var activeJob = new ActiveJobContext(
                job.Id, vibecastTmux, null, null, jobWorkTree, vibecastHome);
            lock (_activeJobsLock) { _activeJobs.Add(activeJob); }

            // 8. Wait for vibecast control socket to be ready
            var controlSocket = Path.Combine(vibecastHome, ".vibecast", "control.sock");
            _console.MarkupLine($"[cyan]Waiting for vibecast control socket at {controlSocket}...[/]");
            for (var i = 0; i < 30; i++)
            {
                if (File.Exists(controlSocket))
                {
                    _console.MarkupLine($"[green]Control socket ready after {i}s[/]");
                    break;
                }
                await Task.Delay(1000, ct);
            }

            if (!File.Exists(controlSocket))
            {
                _console.MarkupLine("[red]Control socket not found after 30s[/]");
                var startupLog = await CaptureVibecastLogAsync(vibecastLogFile, ct);
                agentSpan?.AddEvent(new System.Diagnostics.ActivityEvent("vibecast.control_socket_timeout",
                    tags: new System.Diagnostics.ActivityTagsCollection { ["vibecast.log"] = startupLog }));
                await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id,
                    "completed", "failure",
                    $"Vibecast failed to start (control socket not found after 30s):\n{startupLog}", ct);
                throw new InvalidOperationException("vibecast control socket not found");
            }

            // Small delay for control server to fully initialize
            await Task.Delay(1000, ct);

            // 9. Trigger start-stream via control socket
            _console.MarkupLine("[cyan]Triggering start-stream...[/]");
            await SendControlSocketRequestAsync(controlSocket, "POST", "/start-stream",
                """{"promptSharing":true,"shareProjectInfo":true}""", ct);

            // 10. Wait for streaming session to be created (vibecast-<sessionId>)
            _console.MarkupLine("[cyan]Waiting for streaming session...[/]");
            string? streamingSession = null;
            string? sessionIdValue = null;
            string? broadcastIdValue = null;
            for (var i = 0; i < 30; i++)
            {
                // Check /status on control socket to get sessionId and broadcastId
                var statusJson = await SendControlSocketRequestAsync(controlSocket, "GET", "/status", null, ct);
                if (statusJson != null)
                {
                    var statusData = JsonSerializer.Deserialize<JsonElement>(statusJson, JsonOptions);
                    if (statusData.TryGetProperty("sessionId", out var sid) && sid.GetString() is { Length: > 0 } sId)
                    {
                        if (statusData.TryGetProperty("phase", out var phaseEl) && phaseEl.GetString() == "live")
                        {
                            sessionIdValue = sId;
                            if (statusData.TryGetProperty("broadcastId", out var bid) && bid.GetString() is { Length: > 0 } bId)
                                broadcastIdValue = bId;
                            streamingSession = $"vibecast-{sId}";
                            _console.MarkupLine($"[green]Streaming live! Session: {streamingSession}[/]");
                            break;
                        }
                    }
                }
                await Task.Delay(1000, ct);
            }

            // Update tracked job with streaming session and control socket
            lock (_activeJobsLock)
            {
                var idx = _activeJobs.FindIndex(j => j.JobId == job.Id);
                if (idx >= 0)
                    _activeJobs[idx] = activeJob with { StreamingSession = streamingSession, ControlSocket = controlSocket };
            }

            if (streamingSession == null)
            {
                _console.MarkupLine("[yellow]Streaming session not detected — capturing logs for diagnostics...[/]");
                var vibecastLog = await CaptureVibecastLogAsync(vibecastLogFile, ct);
                var tmuxLog = await CaptureTmuxPaneAsync(vibecastTmux, "main", ct);
                var combinedLog = $"--- vibecast ---\n{vibecastLog}\n--- claude pane ---\n{tmuxLog}";
                agentSpan?.AddEvent(new System.Diagnostics.ActivityEvent("vibecast.stream_timeout",
                    tags: new System.Diagnostics.ActivityTagsCollection
                    {
                        ["vibecast.log"] = vibecastLog,
                        ["tmux.pane"] = tmuxLog
                    }));
                await PatchJobStatusWithLogsAsync(client, baseUrl, runId, job.Id,
                    "completed", "failure",
                    $"Streaming session never started (timeout):\n{combinedLog}", ct);
                throw new InvalidOperationException("vibecast streaming session not found after 30s");
            }
            agentSpan?.SetTag("session.id", sessionIdValue ?? "");
            agentSpan?.SetTag("broadcast.id", broadcastIdValue ?? "");
            agentSpan?.Dispose();

            // 11. Update job with sessionId/broadcastId and link stream to project
            if (sessionIdValue != null)
            {
                _console.MarkupLine($"[cyan]Updating job with sessionId: {sessionIdValue}[/]");
                var patchStream = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{job.Id}");
                patchStream.Content = JsonContent.Create(new { status = "in_progress", sessionId = sessionIdValue, broadcastId = broadcastIdValue });
                patchStream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
                await client.SendAsync(patchStream, ct);

                // Link stream to the task so the task card glows when live
                if (job.AgentDef?.TaskId != null && job.AgentDef?.AssemblyLineId != null)
                {
                    try
                    {
                        var linkReq = new HttpRequestMessage(HttpMethod.Post,
                            $"{baseUrl}/assembly-lines/{job.AgentDef.AssemblyLineId}/tasks/{job.AgentDef.TaskId}/streams");
                        linkReq.Content = JsonContent.Create(new { sessionId = sessionIdValue, broadcastId = broadcastIdValue });
                        linkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
                        await client.SendAsync(linkReq, ct);
                        _console.MarkupLine($"[green]Stream linked to task {job.AgentDef.TaskId}[/]");
                    }
                    catch (Exception ex)
                    {
                        _console.MarkupLine($"[yellow]Failed to link stream to task: {ex.Message.EscapeMarkup()}[/]");
                    }
                }
            }

            // 12. Switch to the "main" window so viewers see Claude Code (not the info screen)
            if (streamingSession != null)
            {
                _console.MarkupLine("[cyan]Switching to main window...[/]");
                await RunProcessAsync("tmux", $"select-window -t {streamingSession}:main", null, ct);
            }

            // Note: the job prompt is injected via VIBECAST_INITIAL_PROMPT_FILE which vibecast
            // passes directly to Claude as a positional argument at startup. No send-keys needed.
            // A positional arg to claude stays interactive (NOT the same as -p which exits after response).
            _console.MarkupLine($"[green]Prompt will be delivered via VIBECAST_INITIAL_PROMPT_FILE ({promptFile})[/]");

            // Answer injection is handled by vibecast (broadcast.go startAnswerInjectionLoop).
            // vibecast polls /api/lives/sessions/{sessionId}/pending-answer and injects via tmux send-keys.
            // The runner must not do tmux operations — in production runner is on the host and vibecast
            // is inside Docker; they don't share a tmux server.

            // 13. Wait for job to complete with activity-based timeout
            var idleTimeoutMs = (job.AgentDef?.IdleTimeoutMinutes ?? 2) * 60 * 1000;
            var maxTimeout = TimeSpan.FromMinutes(job.AgentDef?.MaxTimeoutMinutes ?? 60);
            _console.MarkupLine($"[cyan]Waiting up to {maxTimeout.TotalMinutes} minutes (idle threshold: {idleTimeoutMs / 60000} min)...[/]");

            using var waitSpan = _activitySource.StartActivity("runner.job.wait_completion");
            waitSpan?.SetTag("idle_timeout_minutes", job.AgentDef?.IdleTimeoutMinutes ?? 2);
            waitSpan?.SetTag("max_timeout_minutes", job.AgentDef?.MaxTimeoutMinutes ?? 60);
            waitSpan?.SetTag("session.id", sessionIdValue ?? "");
            waitSpan?.SetTag("broadcast.id", broadcastIdValue ?? "");

            var startTime = DateTime.UtcNow;
            // Track why the loop exited so we report the right conclusion
            var completionReason = "timeout"; // default: loop ran to max timeout
            while (DateTime.UtcNow - startTime < maxTimeout)
            {
                // Check for task completion signal file
                var completionSignal = Path.Combine(jobWorkTree, ".task-complete");
                if (File.Exists(completionSignal))
                {
                    _console.MarkupLine("[green]Task completion signal detected![/]");
                    completionReason = "success";
                    break;
                }

                // Check if the streaming session still exists
                if (streamingSession != null)
                {
                    var checkPsi = new ProcessStartInfo("tmux", $"has-session -t {streamingSession}")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    var checkProc = Process.Start(checkPsi);
                    if (checkProc != null)
                    {
                        await checkProc.WaitForExitAsync(ct);
                        if (checkProc.ExitCode != 0)
                        {
                            // Vibecast called stop_broadcast itself — honour whatever conclusion
                            // was already sent via session-event; use "success" as default here
                            // because the actual conclusion comes from the broadcast end event.
                            _console.MarkupLine("[yellow]Streaming session ended, completing job.[/]");
                            completionReason = "success";
                            break;
                        }
                    }
                }

                // Poll activity endpoint to detect idle agent.
                // We treat either recent websocket activity OR recent OTLP telemetry as "active".
                // Claude emits spans continuously while thinking/using tools, so OTLP traffic is a
                // reliable proxy for "Claude is still working" even when there's no terminal output.
                // When subagents are active the server returns a 3x extended whenTimeout so we don't
                // false-positive during the orchestrator's silent inference pass after subagents finish.
                if (sessionIdValue != null)
                {
                    try
                    {
                        var scheme = serverUri.Scheme;
                        var activityUrl = $"{scheme}://{serverUri.Host}:{serverUri.Port}/api/lives/activity?sessionId={sessionIdValue}&idleThresholdMs={idleTimeoutMs}";
                        var actResp = await client.GetAsync(activityUrl, ct);
                        if (actResp.IsSuccessStatusCode)
                        {
                            var actData = JsonSerializer.Deserialize<JsonElement>(
                                await actResp.Content.ReadAsStringAsync(ct), JsonOptions);

                            var activeSubagentCount = actData.TryGetProperty("activeSubagentCount", out var asc) ? asc.GetInt32() : 0;
                            var subagentMultiplier = activeSubagentCount > 0 ? 3 : 1;
                            var effectiveIdleMs = idleTimeoutMs * subagentMultiplier;

                            // Apply same multiplier to the local OTEL proxy signal
                            var otlpIdleSecs = (DateTime.UtcNow - otlpProxy.LastActivityAt).TotalSeconds;
                            var otlpActive = otlpIdleSecs < effectiveIdleMs / 1000.0;

                            if (actData.TryGetProperty("isActive", out var isActive) && !isActive.GetBoolean())
                            {
                                // Server-side activity is idle — but check OTLP too before timing out.
                                if (otlpActive)
                                {
                                    _console.MarkupLine($"[dim]Server idle but OTLP active ({otlpIdleSecs:F0}s ago), continuing...[/]");
                                }
                                else
                                {
                                    var idleSince = actData.TryGetProperty("idleSinceMs", out var idleMs) ? idleMs.GetInt64() / 1000 : 0;
                                    if (activeSubagentCount > 0)
                                    {
                                        // Should not normally reach here since server applies 3x multiplier,
                                        // but guard just in case both signals are stale.
                                        _console.MarkupLine($"[dim]Idle but {activeSubagentCount} subagent(s) active — extending timeout 3x, continuing...[/]");
                                    }
                                    else
                                    {
                                        _console.MarkupLine($"[yellow]Agent idle for {idleSince}s (no websocket or OTLP activity), completing job.[/]");
                                        completionReason = "idle_timeout";
                                        break;
                                    }
                                }
                            }
                            else if (activeSubagentCount > 0)
                            {
                                var whenTimeout = actData.TryGetProperty("whenTimeout", out var wt) ? DateTimeOffset.FromUnixTimeMilliseconds(wt.GetInt64()).LocalDateTime.ToString("HH:mm:ss") : "?";
                                _console.MarkupLine($"[dim]{activeSubagentCount} subagent(s) active — timeout extended to {whenTimeout}[/]");
                            }

                            waitSpan?.SetTag("active_subagent_count", activeSubagentCount);
                        }
                    }
                    catch
                    {
                        // Activity check is best-effort; continue waiting
                    }
                }

                if (ct.IsCancellationRequested) break;
                await Task.Delay(30_000, ct);

                var elapsed = DateTime.UtcNow - startTime;
                if ((int)elapsed.TotalSeconds % 60 < 30)
                    _console.MarkupLine($"[dim]{elapsed.Minutes}m elapsed...[/]");
            }

            // Record completion reason on wait span and parent job span
            var elapsedMinutes = (DateTime.UtcNow - startTime).TotalMinutes;
            waitSpan?.SetTag("completion.reason", completionReason);
            waitSpan?.SetTag("elapsed.minutes", Math.Round(elapsedMinutes, 2));

            // 15. Stop vibecast streaming — vibecast sends session-event/end which stores
            // completionMessage + claudeSessionId on the session. The PATCH below reads that
            // session data to create the task comment and stamp the task fields. The Runner
            // does NOT call session-event/end directly; the streaming layer owns that.
            var jobConclusion = completionReason == "success" ? "success" : "failure";
            var stopPath = completionReason == "success"
                ? "/stop-broadcast"
                : $"/stop-broadcast?conclusion={Uri.EscapeDataString(completionReason)}";
            _console.MarkupLine("[cyan]Stopping vibecast session...[/]");
            await SendControlSocketRequestAsync(controlSocket, "POST", stopPath, null, ct);
            await Task.Delay(2000, ct);

            // Kill the vibecast tmux session
            await RunProcessAsync("tmux", $"kill-session -t {vibecastTmux}", null, ct);

            // Backup CLAUDE_CONFIG_DIR so a runner on a different machine can restore it
            // before the next job for this project (cross-machine resume support).
            await BackupClaudeConfigArchiveAsync(client, baseUrl, registration, ct);

            _console.MarkupLine($"[cyan]Marking job as completed (conclusion: {jobConclusion}, reason: {completionReason})...[/]");
            var vibecastLogSummary = await CaptureVibecastLogAsync(vibecastLogFile, ct);
            var patchCompleted = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{job.Id}");
            patchCompleted.Content = JsonContent.Create(new
            {
                status = "completed",
                conclusion = jobConclusion,
                // completionReason carries the detailed reason (idle_timeout, timeout, success, etc.)
                // so the server can apply the correct task lifecycle logic without parsing conclusion.
                completionReason,
                sessionId = sessionIdValue,
                broadcastId = broadcastIdValue,
                logs = $"Job completed after {(DateTime.UtcNow - startTime).TotalMinutes:F1} minutes in {jobWorkTree} (reason: {completionReason})\n{vibecastLogSummary}"
            });
            patchCompleted.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            await client.SendAsync(patchCompleted, ct);

            _console.MarkupLine($"[green]Job {job.Id} completed.[/]");

            // Enrich parent job span with final outcome
            jobSpan?.SetTag("agentics.conclusion", jobConclusion);
            jobSpan?.SetTag("agentics.completion_reason", completionReason);
            jobSpan?.SetTag("agentics.session_id", sessionIdValue ?? "");
            jobSpan?.SetTag("agentics.broadcast_id", broadcastIdValue ?? "");
            jobSpan?.SetTag("agentics.elapsed_minutes", Math.Round(elapsedMinutes, 2));
            if (jobConclusion != "success")
                jobSpan?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, completionReason);

            // Remove from active tracking (normal completion)
            lock (_activeJobsLock) { _activeJobs.RemoveAll(j => j.JobId == job.Id); }
        }
        finally
        {
            if (jobWorkTree != null)
            {
                // Skip cleanup for stable task dirs (/tmp/pks-runner-tasks/…) — they must
                // persist so a future continue/resume job can find the same CWD and session.
                // Per-job dirs (/tmp/pks-runner-jobs/…) are still cleaned up as before.
                var isTaskDir = jobWorkTree.Contains(Path.Combine(Path.GetTempPath(), "pks-runner-tasks"));
                if (!isTaskDir)
                    await CleanupWorkTreeAsync(jobWorkTree, settings.Verbose, ct);
                else
                    _console.MarkupLine($"[dim]Keeping task dir for future resume: {jobWorkTree}[/]");
            }
        }
    }

    /// <summary>
    /// Download the project's CLAUDE_CONFIG_DIR archive from the server and extract it to
    /// /tmp/agentic-{owner}-{project}/ so Claude finds prior sessions + memory on this machine.
    /// </summary>
    private async Task RestoreClaudeConfigArchiveAsync(
        HttpClient client, string baseUrl, AgenticsRunnerRegistration registration,
        CancellationToken ct)
    {
        var claudeConfigDir = $"/tmp/agentic-{registration.Owner}-{registration.Project}";
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/claude-config-archive");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            var resp = await client.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _console.MarkupLine("[dim]No CLAUDE_CONFIG_DIR archive on server — starting fresh[/]");
                return;
            }
            resp.EnsureSuccessStatusCode();

            var zipBytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var tmpZip = Path.Combine(Path.GetTempPath(), $"claude-config-{registration.Owner}-{registration.Project}.zip");
            await File.WriteAllBytesAsync(tmpZip, zipBytes, ct);

            Directory.CreateDirectory(claudeConfigDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tmpZip, claudeConfigDir, overwriteFiles: true);
            File.Delete(tmpZip);
            _console.MarkupLine($"[green]Restored CLAUDE_CONFIG_DIR ({zipBytes.Length / 1024}KB) → {claudeConfigDir}[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]CLAUDE_CONFIG_DIR restore skipped: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Zip /tmp/agentic-{owner}-{project}/ and upload it to the server so a future runner
    /// (possibly on a different machine) can restore it before the next job.
    /// </summary>
    private async Task BackupClaudeConfigArchiveAsync(
        HttpClient client, string baseUrl, AgenticsRunnerRegistration registration,
        CancellationToken ct)
    {
        var claudeConfigDir = $"/tmp/agentic-{registration.Owner}-{registration.Project}";
        if (!Directory.Exists(claudeConfigDir))
        {
            _console.MarkupLine("[dim]No CLAUDE_CONFIG_DIR to backup[/]");
            return;
        }
        try
        {
            var tmpZip = Path.Combine(Path.GetTempPath(), $"claude-config-backup-{registration.Owner}-{registration.Project}.zip");
            if (File.Exists(tmpZip)) File.Delete(tmpZip);
            System.IO.Compression.ZipFile.CreateFromDirectory(claudeConfigDir, tmpZip);

            var zipBytes = await File.ReadAllBytesAsync(tmpZip, ct);
            File.Delete(tmpZip);

            var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/claude-config-archive");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            req.Content = new ByteArrayContent(zipBytes);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            _console.MarkupLine($"[green]Backed up CLAUDE_CONFIG_DIR ({zipBytes.Length / 1024}KB) → server[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]CLAUDE_CONFIG_DIR backup skipped: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private async Task<AgenticsProxyOptions> BuildAgenticsProxyOptionsAsync(RunnerJob job, CancellationToken ct)
    {
        var options = new AgenticsProxyOptions { JobId = job.Id };

        if (!await _foundryAuthService.IsAuthenticatedAsync())
            return options;

        var creds = await _foundryAuthService.GetStoredCredentialsAsync();
        if (creds?.SelectedResourceName is not { } resourceName)
            return options;

        var scope = _foundryConfig.CognitiveScope;
        foreach (var host in new[]
        {
            $"{resourceName}.cognitiveservices.azure.com",
            $"{resourceName}.services.ai.azure.com",
            $"{resourceName}.openai.azure.com",
        })
        {
            options.AllowedHosts[host] = new HostPolicy { TokenScope = scope };
        }

        return options;
    }

    private static string ResolveVibecastBinary(string? explicitPath)
    {
        // 1. Explicit path from --vibecast-binary flag
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        // 2. VIBECAST_BINARY environment variable
        var envPath = Environment.GetEnvironmentVariable("VIBECAST_BINARY");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        // 3. Fallback to npx
        return "npx vibecast";
    }

    /// <summary>
    /// Resolves the chat-llm:v1 backend base URL (an OpenAI-compatible chat-completions endpoint,
    /// e.g. http://localhost:11434/v1 for Ollama): explicit --chat-llm-backend-url flag, else the
    /// CHAT_LLM_BACKEND_URL env var, else null (capability not advertised — mirrors ResolveVibecastBinary's
    /// flag-then-env fallback shape, minus the "always has a default" step since there's no sane default backend).
    /// </summary>
    private static string? ResolveChatLlmBackendUrl(string? explicitValue)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
            return explicitValue.TrimEnd('/');

        var envValue = Environment.GetEnvironmentVariable("CHAT_LLM_BACKEND_URL");
        return !string.IsNullOrWhiteSpace(envValue) ? envValue.TrimEnd('/') : null;
    }

    /// <summary>
    /// Resolves the chat-llm:v1 backend API key: explicit --chat-llm-backend-key flag, else the
    /// CHAT_LLM_BACKEND_KEY env var, else null (some local backends, e.g. Ollama, need no key at all).
    /// This value is only ever sent to the configured backend, never to the Server — see 13-chat.md's
    /// Kind B credential invariant.
    /// </summary>
    private static string? ResolveChatLlmBackendKey(string? explicitValue) =>
        !string.IsNullOrWhiteSpace(explicitValue)
            ? explicitValue
            : Environment.GetEnvironmentVariable("CHAT_LLM_BACKEND_KEY");

    /// <summary>
    /// Resolves the chat-llm:v1 model id used when no --chat-llm-backend-url override is set: explicit
    /// --chat-llm-model flag, else CHAT_LLM_MODEL env, else the persisted <see cref="RunnerProfile.DefaultChatModel"/>
    /// (Phase 3 -- set by the interactive configure flow), else "gpt-5.5" — the built-in
    /// AgentChatProviderFactory default that resolves to the azure-openai provider with no fixed
    /// endpoint, so it auto-fills from whatever resource `pks foundry init` already selected
    /// (AgentChatProviderFactory.GetModelEntryAsync's Foundry fallback) with zero extra configuration.
    /// Explicit CLI flags/env always win over the persisted profile.
    /// </summary>
    private static string ResolveChatLlmModelId(string? explicitValue, string? profileDefault = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue))
            return explicitValue;

        var envValue = Environment.GetEnvironmentVariable("CHAT_LLM_MODEL");
        if (!string.IsNullOrWhiteSpace(envValue))
            return envValue;

        return !string.IsNullOrWhiteSpace(profileDefault) ? profileDefault : "gpt-5.5";
    }

    private static async Task<string?> SendControlSocketRequestAsync(
        string socketPath, string method, string path, string? body, CancellationToken ct)
    {
        try
        {
            var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), ct);

            using var networkStream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            using var writer = new StreamWriter(networkStream, leaveOpen: true);
            using var reader = new StreamReader(networkStream, leaveOpen: true);

            // Write HTTP request
            await writer.WriteAsync($"{method} {path} HTTP/1.1\r\n");
            await writer.WriteAsync("Host: localhost\r\n");
            if (body != null)
            {
                await writer.WriteAsync("Content-Type: application/json\r\n");
                await writer.WriteAsync($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n");
            }
            await writer.WriteAsync("Connection: close\r\n");
            await writer.WriteAsync("\r\n");
            if (body != null)
                await writer.WriteAsync(body);
            await writer.FlushAsync(ct);

            // Read response
            var response = await reader.ReadToEndAsync(ct);

            // Extract body (after blank line)
            var bodyStart = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart >= 0)
                return response[(bodyStart + 4)..];

            return response;
        }
        catch
        {
            return null;
        }
    }

    private static async Task TmuxSendKeysAsync(string target, string text, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tmux")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("send-keys");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(text);
        psi.ArgumentList.Add("Enter");

        var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct);
    }

    /// <summary>Sends a Tab key to the target tmux pane (no text, no Enter).</summary>
    private static async Task TmuxSendKeysToTabAsync(string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tmux")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("send-keys");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("Tab");

        var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct);
    }

    /// <summary>Captures the visible text content of a tmux pane (no ANSI codes).</summary>
    private static async Task<string> TmuxCapturePaneAsync(string target, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tmux")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("capture-pane");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("-p");

        var proc = Process.Start(psi);
        if (proc == null) return string.Empty;
        var output = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return output;
    }

    /// <summary>
    /// Captures the pane, writes a debug snapshot file to <paramref name="debugDir"/>,
    /// and emits an OTEL event on the current Activity. Returns the captured content.
    ///
    /// Files are named: <c>NNN_label.txt</c> (NNN = zero-padded global sequence) so they
    /// sort chronologically in a file browser.  The debug dir is created on first use.
    /// </summary>
    private static async Task<string> TmuxCaptureAndDebugAsync(
        string target, string label, string debugDir, CancellationToken ct)
    {
        var content = await TmuxCapturePaneAsync(target, ct);

        try
        {
            Directory.CreateDirectory(debugDir);
            var seq = Interlocked.Increment(ref _captureSeq);
            var safeLabel = string.Concat(label.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
            var filename = Path.Combine(debugDir, $"{seq:D4}_{safeLabel}.txt");
            await File.WriteAllTextAsync(filename, content, ct);
        }
        catch { /* debug writes are best-effort */ }

        // Emit OTEL event — truncate content to avoid oversized spans
        var truncated = content.Length > 2000 ? content[..2000] + "…" : content;
        System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent(
            "tmux.capture",
            tags: new System.Diagnostics.ActivityTagsCollection
            {
                ["tmux.target"] = target,
                ["tmux.label"] = label,
                ["tmux.content"] = truncated,
            }));

        return content;
    }

    /// <summary>
    /// Captures the tmux pane, retrying until at least one numbered option (e.g. "1. Foo")
    /// is visible or the timeout elapses. This handles the delay between the wizard advancing
    /// to the next question and Claude Code actually rendering the new options on screen.
    /// When <paramref name="previousContent"/> is provided the function first waits for the
    /// pane to show DIFFERENT content (wizard has advanced) before checking for options.
    /// This prevents reading stale content from the previous step immediately after Enter.
    /// </summary>
    private static async Task<string> WaitForOptionsAsync(
        string target, string label, string debugDir, CancellationToken ct,
        int maxWaitMs = 3000, int intervalMs = 300, string? previousContent = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var content = "";
        while (DateTime.UtcNow < deadline)
        {
            content = await TmuxCaptureAndDebugAsync(target, label, debugDir, ct);

            // If we have previous content, skip until the wizard has visually advanced
            // (content changed) to avoid matching the just-answered question's options.
            if (previousContent != null && content.Trim() == previousContent.Trim())
            {
                await Task.Delay(intervalMs, ct);
                continue;
            }

            // Consider the pane "ready" when at least one line looks like "N. Option text"
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    content, @"^\s*\d+\.\s+\S",
                    System.Text.RegularExpressions.RegexOptions.Multiline))
                return content;
            await Task.Delay(intervalMs, ct);
        }
        // Return whatever we have after timeout (fall back to free-text if still no options)
        return content;
    }

    /// <summary>
    /// Parses the visible pane content for numbered option lines like "  1. SaaS (Recommended)"
    /// or "❯ 1. SaaS (Recommended)" and returns the number whose text matches answerText.
    /// Strips common annotation suffixes like "(Recommended)" from both sides before comparing.
    /// Returns 0 if no match is found.
    /// </summary>
    private static int MatchOptionNumber(string paneContent, string answerText)
    {
        static string Normalize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(.*?\)\s*$", "").Trim();

        var normalizedAnswer = Normalize(answerText);

        foreach (var line in paneContent.Split('\n'))
        {
            // Strip leading selection cursor, checkbox markers (◯ ◉ ● ○), and whitespace
            var trimmed = line.TrimStart('❯', ' ', '\t').TrimStart('◯', '◉', '●', '○', ' ');
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\d+)\.\s+(.+)");
            if (!match.Success) continue;

            var num = int.Parse(match.Groups[1].Value);
            var optText = Normalize(match.Groups[2].Value);

            if (optText.Equals(normalizedAnswer, StringComparison.OrdinalIgnoreCase) ||
                normalizedAnswer.StartsWith(optText, StringComparison.OrdinalIgnoreCase) ||
                optText.StartsWith(normalizedAnswer, StringComparison.OrdinalIgnoreCase))
            {
                return num;
            }
        }
        return 0;
    }

    /// <summary>
    /// Returns the option number currently highlighted by the ❯ cursor in the pane content.
    /// Returns 1 if no cursor is found (safe default — the wizard always starts at option 1).
    /// </summary>
    private static int SelectedOptionNumber(string paneContent)
    {
        foreach (var line in paneContent.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("❯")) continue;
            // Strip cursor, optional checkbox markers, and whitespace
            var trimmed = line.TrimStart('❯', ' ', '\t').TrimStart('◯', '◉', '●', '○', ' ');
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\d+)\.");
            if (match.Success) return int.Parse(match.Groups[1].Value);
        }
        return 1;
    }

    /// <summary>
    /// Returns the option number for "Type something." in the pane, or 0 if not found.
    /// </summary>
    private static int TypeSomethingOptionNumber(string paneContent)
    {
        foreach (var line in paneContent.Split('\n'))
        {
            var trimmed = line.TrimStart('❯', ' ', '\t');
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\d+)\.\s+Type something");
            if (match.Success) return int.Parse(match.Groups[1].Value);
        }
        return 0;
    }

    /// <summary>Sends a single named key (e.g. "Down", "Enter", "Tab") without appending Enter.</summary>
    private static async Task TmuxSendKeyRawAsync(string target, string key, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tmux")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("send-keys");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(key);

        var proc = Process.Start(psi);
        if (proc != null)
            await proc.WaitForExitAsync(ct);
    }

    private static async Task RunProcessAsync(string cmd, string args, string? workDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (workDir != null)
                psi.WorkingDirectory = workDir;
            var proc = Process.Start(psi);
            if (proc != null)
                await proc.WaitForExitAsync(ct);
        }
        catch { /* ignore */ }
    }

    private async Task<string> ResolveWorkDirAsync(string? workDir, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(workDir))
            return Path.GetFullPath(workDir);

        // Use git repo root so .agentics/_work/ is at the workspace level
        var repoRoot = await GetGitToplevelAsync(ct);
        if (repoRoot != null)
            return Path.Combine(repoRoot, ".agentics", "_work");

        return Path.GetFullPath(Path.Combine(".agentics", "_work"));
    }

    private async Task<string> SetupJobWorkTreeAsync(
        string workDir, AgenticsRunnerRegistration registration,
        RunnerJob job, bool verbose, bool useWorktree, CancellationToken ct,
        Dictionary<string, string>? gitEnv = null)
    {
        var owner = registration.Owner;
        var project = registration.Project;
        var repository = job.AgentDef?.Repository;
        var branch = job.AgentDef?.Branch ?? "main";

        // Clean-clone mode: no worktree. Fresh git clone (or empty dir) per job.
        // The actual job directory lives in /tmp outside the project tree so Claude
        // cannot walk up and find the runner workspace's .claude/ settings, skills,
        // or CLAUDE.md files. A symlink is placed at .agentics/_work/jobs/{jobId}
        // for VS Code / file-explorer visibility.
        if (!useWorktree)
        {
            // Use a stable task-scoped directory when a taskId is available.
            // This keeps the working directory path constant across all jobs for the same
            // task, so Claude's project hash (derived from CWD) is always the same.
            // --resume can then find prior session transcripts without any copying.
            // Fall back to a per-job dir when no taskId is present.
            var taskId = job.AgentDef?.TaskId;
            var tmpJobDir = !string.IsNullOrEmpty(taskId)
                ? Path.Combine(Path.GetTempPath(), "pks-runner-tasks", taskId)
                : Path.Combine(Path.GetTempPath(), "pks-runner-jobs", job.Id);
            var isTaskDir = !string.IsNullOrEmpty(taskId);

            Directory.CreateDirectory(tmpJobDir);

            var symlinkParent = Path.Combine(workDir, "jobs");
            Directory.CreateDirectory(symlinkParent);
            var symlinkPath = Path.Combine(symlinkParent, job.Id);
            if (!Path.Exists(symlinkPath))
                Directory.CreateSymbolicLink(symlinkPath, tmpJobDir);

            _console.MarkupLine(isTaskDir
                ? $"[dim]Task dir (stable): {tmpJobDir} (symlink: {symlinkPath})[/]"
                : $"[dim]Job dir: {tmpJobDir} (symlink: {symlinkPath})[/]");

            // Pre-approve the job directory so Claude Code skips the "Do you trust this folder?"
            // prompt. Claude checks for an existing project entry in {CLAUDE_CONFIG_DIR}/projects/{encoded}/
            // to decide whether to show the prompt. Creating the directory signals prior approval.
            //
            // CLAUDE_CONFIG_DIR is set to /tmp/agentic-{owner}-{project} so all jobs for the
            // same project share one config dir. Combined with the stable task CWD above, Claude
            // sessions accumulate in the same project slot across all jobs for a task.
            try
            {
                var claudeConfigDir = $"/tmp/agentic-{registration.Owner}-{registration.Project}";
                var encodedPath = tmpJobDir.TrimStart('/').Replace("/", "-");
                var claudeProjectDir = Path.Combine(claudeConfigDir, "projects", encodedPath);
                Directory.CreateDirectory(claudeProjectDir);
                _console.MarkupLine($"[dim]CLAUDE_CONFIG_DIR: {claudeConfigDir}[/]");
            }
            catch (Exception ex) { _console.MarkupLine($"[yellow]Claude config dir setup: {ex.Message.EscapeMarkup()}[/]"); }

            // For continue jobs on a task dir: if the directory already has content, skip
            // git clone/init — Claude resumes into the exact state from the prior job.
            var isResume = !string.IsNullOrEmpty(job.AgentDef?.ResumeSessionId);
            var dirHasContent = isTaskDir && Directory.Exists(tmpJobDir) &&
                Directory.GetFileSystemEntries(tmpJobDir).Length > 0;
            var skipGitSetup = isResume && dirHasContent;

            if (skipGitSetup)
            {
                _console.MarkupLine($"[dim]Resume: task dir has existing content — skipping git setup[/]");
                return tmpJobDir;
            }

            if (!string.IsNullOrEmpty(repository))
            {
                _console.MarkupLine($"[cyan]Cloning {repository} → {tmpJobDir}...[/]");
                var cloneResult = await RunGitAsync(
                    $"clone --depth=1 --branch {branch} {repository} {tmpJobDir}",
                    null, verbose, ct, gitEnv);
                if (cloneResult != 0)
                    _console.MarkupLine($"[yellow]git clone failed (exit {cloneResult}) — using empty job directory[/]");
            }
            else
            {
                _console.MarkupLine($"[cyan]No repository URL — initializing isolated git repo in {tmpJobDir}[/]");
                // Init a git repo so Claude's git-root detection stops here.
                await RunGitAsync($"init {tmpJobDir}", null, verbose, ct, gitEnv);
                // .claude/ is written by the runner after this init — gitignore it so Claude
                // never sees it as untracked and doesn't try to commit runner-injected config.
                await File.WriteAllTextAsync(Path.Combine(tmpJobDir, ".gitignore"), ".claude/\n", ct);
                var initGitEnv = new Dictionary<string, string>(gitEnv ?? [])
                {
                    ["GIT_AUTHOR_NAME"] = "pks-runner",
                    ["GIT_AUTHOR_EMAIL"] = "runner@agentics.dk",
                    ["GIT_COMMITTER_NAME"] = "pks-runner",
                    ["GIT_COMMITTER_EMAIL"] = "runner@agentics.dk",
                };
                await RunGitAsync($"-C {tmpJobDir} add .gitignore", null, verbose, ct, initGitEnv);
                await RunGitAsync($"-C {tmpJobDir} commit -m \"init job {job.Id}\"", null, verbose, ct, initGitEnv);
            }

            return tmpJobDir;
        }

        // Find git repo root and its remote URL for same-repo detection
        var repoRoot = await GetGitToplevelAsync(ct);
        var currentRepoUrl = await GetCurrentRepoUrlAsync(ct);

        // Same-repo: if no repository specified, or if it matches the current workspace repo
        var isSameRepo = string.IsNullOrEmpty(repository)
            || (!string.IsNullOrEmpty(currentRepoUrl)
                && NormalizeGitUrl(repository) == NormalizeGitUrl(currentRepoUrl));

        string sourceDir;

        if (!isSameRepo)
        {
            // External repo: clone to _work/<owner>/<project>/<branch>/
            var cloneDir = Path.Combine(workDir, owner, project, branch);
            if (!Directory.Exists(Path.Combine(cloneDir, ".git")))
            {
                _console.MarkupLine($"[cyan]Cloning {repository} → {cloneDir}...[/]");
                Directory.CreateDirectory(Path.GetDirectoryName(cloneDir)!);
                var cloneResult = await RunGitAsync(
                    $"clone --branch {branch} {repository} {cloneDir}",
                    null, verbose, ct, gitEnv);
                if (cloneResult != 0)
                    throw new InvalidOperationException($"git clone failed (exit {cloneResult})");
            }
            else
            {
                // Pull latest
                if (verbose) _console.MarkupLine($"[dim]Pulling latest in {cloneDir}...[/]");
                await RunGitAsync("pull --ff-only", cloneDir, verbose, ct, gitEnv);
            }
            sourceDir = cloneDir;
        }
        else
        {
            // Same repo: use the git repo root (not CWD which may be a subdirectory)
            sourceDir = repoRoot ?? Directory.GetCurrentDirectory();
            if (verbose) _console.MarkupLine($"[dim]Using local repo at {sourceDir}[/]");
        }

        // Create worktree at _work/<owner>/<project>/jobs/<jobId>/
        // (or _work/jobs/<jobId>/ for same-repo)
        string worktreePath;
        if (!isSameRepo)
            worktreePath = Path.Combine(workDir, owner, project, "jobs", job.Id);
        else
            worktreePath = Path.Combine(workDir, "jobs", job.Id);

        _console.MarkupLine($"[cyan]Creating worktree at {worktreePath}...[/]");
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        var worktreeResult = await RunGitAsync(
            $"worktree add {worktreePath} -b job/{job.Id} {branch}",
            sourceDir, verbose, ct);

        if (worktreeResult != 0)
        {
            // Branch might already exist or detached HEAD - try without -b
            worktreeResult = await RunGitAsync(
                $"worktree add --detach {worktreePath} {branch}",
                sourceDir, verbose, ct);
            if (worktreeResult != 0)
                throw new InvalidOperationException($"git worktree add failed (exit {worktreeResult})");
        }

        return worktreePath;
    }

    private static async Task<string?> GetGitToplevelAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task CleanupWorkTreeAsync(string worktreePath, bool verbose, CancellationToken ct)
    {
        if (!Directory.Exists(worktreePath)) return;

        try
        {
            // A linked git worktree has a .git FILE (not directory) at its root.
            // A plain directory that happens to sit inside a git repo does NOT.
            // We must check for this before calling `git worktree remove` to avoid
            // the "is not a working tree" fatal from git.
            var gitFilePath = Path.Combine(worktreePath, ".git");
            var isLinkedWorktree = File.Exists(gitFilePath) && !Directory.Exists(gitFilePath);

            if (isLinkedWorktree)
            {
                var gitDir = await GetGitDirForWorktreeAsync(worktreePath, ct);
                if (gitDir != null)
                {
                    if (verbose) _console.MarkupLine($"[dim]Removing worktree {worktreePath}...[/]");
                    await RunGitAsync($"worktree remove --force {worktreePath}", gitDir, verbose, ct);
                    return;
                }
            }

            // Fallback: just delete the directory
            Directory.Delete(worktreePath, true);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Warning: worktree cleanup failed: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private async Task CleanupAllActiveJobsAsync()
    {
        List<ActiveJobContext> jobs;
        lock (_activeJobsLock)
        {
            jobs = new List<ActiveJobContext>(_activeJobs);
            _activeJobs.Clear();
        }

        if (jobs.Count == 0) return;

        _console.MarkupLine($"[yellow]Cleaning up {jobs.Count} active job(s)...[/]");

        foreach (var job in jobs)
        {
            try
            {
                // 1. Stop vibecast via control socket (graceful)
                if (job.ControlSocket != null && File.Exists(job.ControlSocket))
                {
                    _console.MarkupLine($"[dim]Stopping vibecast for job {job.JobId[..8]}...[/]");
                    await SendControlSocketRequestAsync(job.ControlSocket, "POST", "/stop-broadcast", null, CancellationToken.None);
                    await Task.Delay(500);
                }

                // 2. Kill the streaming session (vibecast-<streamId>) and its ttyd group sessions
                if (job.StreamingSession != null)
                {
                    // Kill ttyd group sessions (vibecast-<streamId>-ttyd-*)
                    var listOut = await GetProcessOutputAsync("tmux", "list-sessions -F #{session_name}");
                    if (listOut != null)
                    {
                        var prefix = job.StreamingSession + "-ttyd-";
                        foreach (var line in listOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (line.Trim().StartsWith(prefix))
                                await RunProcessAsync("tmux", $"kill-session -t {line.Trim()}", null, CancellationToken.None);
                        }
                    }
                    await RunProcessAsync("tmux", $"kill-session -t {job.StreamingSession}", null, CancellationToken.None);
                }

                // 3. Kill the runner's vibecast tmux session
                await RunProcessAsync("tmux", $"kill-session -t {job.VibecastTmuxSession}", null, CancellationToken.None);

                // 4. Clean up worktree (skip stable task dirs — they persist for future resumes)
                if (job.WorkTreePath != null &&
                    !job.WorkTreePath.Contains(Path.Combine(Path.GetTempPath(), "pks-runner-tasks")))
                    await CleanupWorkTreeAsync(job.WorkTreePath, false, CancellationToken.None);

                // 5. Clean up isolated HOME
                if (job.VibecastHome != null && Directory.Exists(job.VibecastHome))
                {
                    try { Directory.Delete(job.VibecastHome, true); }
                    catch { /* ignore */ }
                }

                _console.MarkupLine($"[green]Cleaned up job {job.JobId[..8]}[/]");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Warning: cleanup failed for job {job.JobId[..8]}: {ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    private static async Task<string?> GetProcessOutputAsync(string cmd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static async Task<string> GetRealHomeDirectoryAsync(CancellationToken ct)
    {
        // On Linux, HOME may be overridden by the runner. Get the real home from getent passwd.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var psi = new ProcessStartInfo("sh", $"-c \"getent passwd $(whoami) | cut -d: -f6\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = await proc.StandardOutput.ReadToEndAsync(ct);
                    await proc.WaitForExitAsync(ct);
                    var home = output.Trim();
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(home))
                        return home;
                }
            }
            catch { /* fallback below */ }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static async Task<string?> GetGitDirForWorktreeAsync(string worktreePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --git-common-dir")
            {
                WorkingDirectory = worktreePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0) return null;
            var commonDir = output.Trim();
            // The common dir is the .git directory of the main repo
            return Path.GetDirectoryName(Path.GetFullPath(Path.Combine(worktreePath, commonDir)));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> GetCurrentRepoUrlAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "config --get remote.origin.url")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeGitUrl(string url)
    {
        // Normalize git URLs for comparison:
        // https://github.com/owner/repo.git → github.com/owner/repo
        // git@github.com:owner/repo.git → github.com/owner/repo
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        // SSH format
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
            url = url["git@".Length..].Replace(':', '/');
        // HTTPS format
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[prefix.Length..];
                break;
            }
        }
        return url.ToLowerInvariant();
    }

    private async Task<Dictionary<string, string>> PrepareGitCredentialsAsync(CancellationToken ct)
    {
        // If the environment already has a working GIT_ASKPASS (e.g. VS Code devcontainer
        // injects one with a gho_ token), verify it actually works before trusting it.
        // VS Code's GIT_ASKPASS connects to a Unix socket (/tmp/vscode-git-*.sock) that may
        // be stale/dead if the VS Code session that created it is gone.
        var existingAskPass = Environment.GetEnvironmentVariable("GIT_ASKPASS");
        if (!string.IsNullOrEmpty(existingAskPass) && File.Exists(existingAskPass))
        {
            try
            {
                var testPsi = new ProcessStartInfo(existingAskPass)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                testPsi.ArgumentList.Add("Password:");
                testPsi.Environment["GIT_TERMINAL_PROMPT"] = "0";
                using var testCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                testCts.CancelAfter(TimeSpan.FromSeconds(3));
                var testProc = Process.Start(testPsi);
                if (testProc != null)
                {
                    var output = await testProc.StandardOutput.ReadToEndAsync(testCts.Token);
                    await testProc.WaitForExitAsync(testCts.Token);
                    if (testProc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        if (_console != null)
                            _console.MarkupLine("[dim]Using existing GIT_ASKPASS from environment.[/]");
                        return new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
                    }
                }
            }
            catch
            {
                // GIT_ASKPASS script failed (e.g. stale VS Code socket) — fall through to stored token
            }

            if (_console != null)
                _console.MarkupLine("[yellow]Existing GIT_ASKPASS is not functional (stale socket?), falling back to stored token.[/]");
        }

        try
        {
            var stored = await _githubAuth.GetStoredTokenAsync();
            var ghToken = stored?.AccessToken ?? string.Empty;
            if (string.IsNullOrEmpty(ghToken) && _console != null)
            {
                _console.MarkupLine(
                    "[yellow]No stored GitHub token — clones from github.com will fail. " +
                    "Run [bold]pks github init[/] (or [bold]--token ghp_...[/]) to authenticate.[/]");
            }

            // Path to the running pks-cli executable so the askpass script can shell
            // back into `pks git askpass …` for hosts (like dev.azure.com) where the
            // credential is short-lived and must be refreshed per request.
            var pksBinary = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? "pks";

            var scriptPath = Path.Combine(Path.GetTempPath(), $"git-askpass-{Guid.NewGuid():N}.sh");

            // Dispatch by host:
            //   *dev.azure.com*  → delegate to `pks git askpass` (refreshes ADO OAuth token)
            //   *github.com*     → return the stored GitHub installation token (static)
            //   everything else  → empty → git fails fast under GIT_TERMINAL_PROMPT=0
            //
            // We dispatch on the prompt itself: git asks "Password for 'https://host/...'",
            // so the URL appears verbatim in $1.
            var sb = new StringBuilder();
            sb.AppendLine("#!/bin/sh");
            sb.AppendLine("# Auto-generated by pks-cli agentics runner. Multi-host credential dispatch.");
            sb.AppendLine("prompt=\"$1\"");
            sb.AppendLine("case \"$prompt\" in");
            sb.AppendLine("  *dev.azure.com*|*visualstudio.com*)");
            sb.AppendLine($"    exec \"{pksBinary}\" git askpass \"$prompt\"");
            sb.AppendLine("    ;;");
            sb.AppendLine("esac");
            sb.AppendLine("case \"$prompt\" in");
            sb.AppendLine("  *Username*) echo \"x-access-token\" ;;");
            if (!string.IsNullOrEmpty(ghToken))
                sb.AppendLine($"  *Password*) echo \"{ghToken}\" ;;");
            else
                sb.AppendLine("  *Password*) exit 1 ;;");
            sb.AppendLine("  *) exit 1 ;;");
            sb.AppendLine("esac");

            await File.WriteAllTextAsync(scriptPath, sb.ToString(), ct);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new Dictionary<string, string>
            {
                ["GIT_ASKPASS"] = scriptPath,
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                // Neutralise any inherited credential.helper (e.g. a stale VS Code
                // devcontainer node helper) so git uses our GIT_ASKPASS directly
                // without first spending stderr on a broken helper.
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "credential.helper",
                ["GIT_CONFIG_VALUE_0"] = "",
            };
        }
        catch
        {
            return new Dictionary<string, string>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "credential.helper",
                ["GIT_CONFIG_VALUE_0"] = "",
            };
        }
    }

    private async Task<int> RunGitAsync(string args, string? workingDir, bool verbose, CancellationToken ct,
        Dictionary<string, string>? extraEnv = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Use ArgumentList for proper quoting
        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            psi.ArgumentList.Add(arg);

        if (workingDir != null)
            psi.WorkingDirectory = workingDir;

        if (extraEnv != null)
        {
            foreach (var (key, value) in extraEnv)
                psi.Environment[key] = value;
        }

        var proc = Process.Start(psi);
        if (proc == null) return -1;

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (verbose || proc.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
                _console.MarkupLine($"[dim]{stdout.Trim().EscapeMarkup()}[/]");
            if (!string.IsNullOrWhiteSpace(stderr))
                _console.MarkupLine($"[dim]{stderr.Trim().EscapeMarkup()}[/]");
        }

        return proc.ExitCode;
    }

    /// <summary>
    /// Replaces the ephemeral claude-code-config-${devcontainerId} volume with a stable named
    /// volume so credentials persist across container respawns. The naming strategy is controlled
    /// by <paramref name="scope"/> per ADR 0004:
    /// <list type="bullet">
    ///   <item>"task" → pks-claude-{owner}-{project}-task-{taskId} (full isolation, OAuth per task)</item>
    ///   <item>"project" (default) → pks-claude-{owner}-{project} (shared across tasks of one project)</item>
    ///   <item>"runner" → pks-claude-{owner} (shared across all the operator's projects)</item>
    /// </list>
    /// Returns the (possibly patched) file map AND the chosen volume name so the caller can log it.
    /// </summary>
    private static (Dictionary<string, string>? Files, string VolumeName) PatchDevcontainerVolumes(
        Dictionary<string, string>? files, string owner, string project, string? taskId, string? scope)
    {
        // Naming rules now live in ClaudeCredentialVolumes (docs/remote-runner-targets-plan.md
        // Phase 5, work item 1) so the SSH-handoff pre-flight / runner status / runner claude-login
        // commands resolve the exact same volume name this method does.
        var stableVolume = PKS.Infrastructure.Services.Runner.ClaudeCredentialVolumes.ResolveVolumeName(
            owner, project, taskId, scope);

        if (files == null) return (null, stableVolume);
        var patched = new Dictionary<string, string>(files);
        const string key = ".devcontainer/devcontainer.json";
        if (patched.TryGetValue(key, out var content))
            patched[key] = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"source=claude-code-config-\$\{devcontainerId\}",
                $"source={stableVolume}");
        return (patched, stableVolume);
    }

    /// <summary>
    /// Computes a short fingerprint for the devcontainer config.
    /// Same owner/project + same devcontainer files → same fingerprint → container can be reused.
    /// </summary>
    /// <summary>
    /// Bumped whenever the runner's hardcoded fallback devcontainer config changes (e.g. when
    /// we switched the fallback image to one that installs tmux). Including it in the
    /// fingerprint forces a rebuild of warm containers that were created before the change.
    /// </summary>
    private const string FallbackConfigVersion = "v2-tmux";

    private static string ComputeDevcontainerFingerprint(
        string owner,
        string project,
        string? taskId,
        Dictionary<string, string>? devcontainerFiles,
        DevcontainerTemplateRef? template)
    {
        var content = devcontainerFiles == null ? string.Empty :
            string.Concat(devcontainerFiles
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
        var templatePart = template == null
            ? string.Empty
            : $"tpl:{template.Id}|{template.Source ?? "nuget"}|{template.Version ?? "latest"}";
        // taskId is part of the fingerprint per ADR 0003 — each task gets its own dedicated container.
        // null taskId (jobless / migration path) keeps the old project-scoped behaviour.
        var taskPart = string.IsNullOrEmpty(taskId) ? string.Empty : $"task:{taskId}/";
        var input = System.Text.Encoding.UTF8.GetBytes(
            $"{owner}/{project}/{taskPart}fb={FallbackConfigVersion}/{templatePart}/{content}");
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>
    /// Returns true if the container with the given ID is currently running.
    /// </summary>
    private static async Task<bool> IsContainerRunningAsync(string containerId)
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"inspect --format={{{{.State.Running}}}} {containerId}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var proc = Process.Start(psi);
            if (proc == null) return false;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the first running container that has the given Docker label filter (e.g. "pks.agentics.fingerprint=abc123").
    /// Returns the container ID or null if none found.
    /// </summary>
    /// <summary>
    /// If a vibecast linux-amd64 binary was embedded at build time (via -p:EmbedVibecast=true),
    /// extracts it to a temp file, copies it into the container, and returns the in-container path.
    /// Returns null when no embedded binary is present (normal/release builds).
    /// </summary>
    private async Task<string?> TryInjectEmbeddedVibecastAsync(string containerId)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("vibecast-linux-amd64");
        if (stream == null)
        {
            _console.MarkupLine("[grey]No embedded vibecast in pks-cli build — container will use [italic]npx --yes vibecast[/] from registry. " +
                "(To embed your local build: rebuild pks-cli with [italic]-p:EmbedVibecastPath=/abs/path/to/vibecast[/].)[/]");

            return null;
        }

        using var ms = new System.IO.MemoryStream();
        await stream.CopyToAsync(ms);
        _console.MarkupLine($"[cyan]Injecting embedded vibecast ({ms.Length / 1024} KB) into container...[/]");

        // Pipe the raw binary via `docker exec -i ... cat > dest` — avoids docker cp
        // Windows path issues and base64 command-length limits.
        var dest = "/tmp/vibecast-embedded";

        // Remove any previous binary so we always inject the latest local build.
        await _spawnerService.ExecInContainerAsync(containerId, $"rm -f {dest}", timeoutSeconds: 5);

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(containerId);
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"cat > {dest} && chmod +x {dest}");

        Process? proc;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) return null;

            ms.Position = 0;
            await ms.CopyToAsync(proc.StandardInput.BaseStream);
            await proc.StandardInput.BaseStream.FlushAsync();
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]vibecast inject pipe error: {ex.Message.EscapeMarkup()} — falling back to npx[/]");
            return null;
        }

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            _console.MarkupLine($"[yellow]vibecast inject failed (exit {proc.ExitCode}): {stderr.Trim().EscapeMarkup()} — falling back to npx[/]");

            return null;
        }

        _console.MarkupLine($"[green]✓ Embedded vibecast active inside container at {dest}[/]");

        // Also extract the claude-plugin directory next to the binary so vibecast's
        // PluginDir() lookup (filepath.Dir(exe)/claude-plugin) finds it at /tmp/claude-plugin.
        await InjectEmbeddedPluginDirAsync(containerId);

        return dest;
    }

    private static readonly string[] PluginResources =
    [
        "claude-plugin/.mcp.json",
        "claude-plugin/.claude-plugin/plugin.json",
        "claude-plugin/hooks/hooks.json",
    ];

    private async Task InjectEmbeddedPluginDirAsync(string containerId)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var resourceName in PluginResources)
        {
            using var resStream = asm.GetManifestResourceStream(resourceName);
            if (resStream == null) continue;

            var destPath = $"/tmp/{resourceName}";
            var destDir = System.IO.Path.GetDirectoryName(destPath)!.Replace('\\', '/');

            // Ensure destination directory exists
            await _spawnerService.ExecInContainerAsync(containerId, $"mkdir -p {destDir}", timeoutSeconds: 5);

            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(containerId);
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"cat > {destPath}");

            Process? proc;
            try
            {
                proc = Process.Start(psi);
                if (proc == null) continue;
                await resStream.CopyToAsync(proc.StandardInput.BaseStream);
                await proc.StandardInput.BaseStream.FlushAsync();
                proc.StandardInput.Close();
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Plugin inject pipe error ({resourceName}): {ex.Message.EscapeMarkup()}[/]");
                continue;
            }

            await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
        }
        _console.MarkupLine("[dim]Claude plugin dir injected at /tmp/claude-plugin.[/]");
    }

    private static async Task<string?> FindContainerByLabelAsync(string labelFilter)
        => await FindContainerByLabelsAsync(labelFilter);

    /// <summary>
    /// Finds a running container that matches ALL of the given label filters
    /// (e.g. "pks.agentics.fingerprint=abc123" + "pks.agentics.runner-instance=def456").
    /// Used by the warm-container reuse check — see ADR 0002.
    /// </summary>
    private static async Task<string?> FindContainerByLabelsAsync(params string[] labelFilters)
    {
        try
        {
            var args = new List<string> { "ps", "--filter", "status=running", "--format", "{{.ID}}" };
            foreach (var lf in labelFilters)
            {
                args.Add("--filter");
                args.Add($"label={lf}");
            }
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var id = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .FirstOrDefault();
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch { return null; }
    }

    private static DevcontainerSpawnOptions BuildSpawnOptions(RunnerJob job, string credentialSocketPath, AgenticsRunnerRegistration registration, string? gitToken)
    {
        // Determine the git URL and branch from the job's agent definition
        string? gitUrl = null;
        var gitBranch = job.AgentDef?.Branch ?? "main";

        // Always clone from AgentDef.Repository (the GitHub source repo).
        // StageGitUrl is the commit-back target for the in-process/vibecast path — not for cloning.
        if (!string.IsNullOrEmpty(job.AgentDef?.Repository))
        {
            var repo = job.AgentDef.Repository;
            if (repo.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                repo.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                // Already a full URL — embed token only if there is no existing userinfo.
                // Without this guard, a URL like http://x-access-token:T1@host/... gets a
                // second credential prepended, producing two `@` separators which libcurl
                // rejects with "Port number was not a decimal number".
                var alreadyHasCredentials = System.Text.RegularExpressions.Regex.IsMatch(repo, @"^https?://[^/]*@");
                if (!alreadyHasCredentials && !string.IsNullOrEmpty(gitToken))
                    gitUrl = System.Text.RegularExpressions.Regex.Replace(repo, @"^(https?://)", $"$1x-access-token:{gitToken}@");
                else
                    gitUrl = repo;
            }
            else
            {
                // owner/repo shorthand — construct GitHub URL
                var credPart = !string.IsNullOrEmpty(gitToken) ? $"x-access-token:{gitToken}@" : string.Empty;
                var suffix = repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? string.Empty : ".git";
                gitUrl = $"https://{credPart}github.com/{repo}{suffix}";
            }
        }

        // Use the project slug as ProjectName so the clone lands at /workspace/{slug}.
        // Falling back to the URL's last segment yields ugly names like "repo.git" for
        // self-hosted URLs (.../{owner}/{project}/repo.git), so prefer job.ProjectName.
        var repoName = job.ProjectName
            ?? job.AgentDef?.Repository?.Split('/').LastOrDefault()?.Replace(".git", "")
            ?? $"{registration.Owner}-{registration.Project}-{job.Id[..8]}";

        // Fingerprint is computed later (after PatchDevcontainerVolumes), so we set a placeholder
        // label here; the caller overwrites it via spawnOptions.IdLabels after computing the real value.
        return new DevcontainerSpawnOptions
        {
            ProjectName = repoName,
            ProjectPath = string.Empty,
            DevcontainerPath = string.Empty,
            LaunchVsCode = false,
            ReuseExisting = false,
            CopySourceFiles = false,
            UseBootstrapContainer = true,
            CredentialSocketPath = credentialSocketPath,
            GitUrl = gitUrl,
            GitBranch = gitBranch,
            RemoveExistingContainer = true,
            InlineDevcontainerFiles = job.AgentDef?.DevcontainerFiles,
            DevcontainerTemplate = job.AgentDef?.DevcontainerTemplate,
            // Hard memory cap for the spawned devcontainer. Defaults to 8 GiB to protect
            // the host from a single agent's next-server (or similar) leaking memory and
            // OOM-ing the host machine. Assembly-line settings can override via
            // settings.runtimeResources.memoryMB on the server side.
            MemoryBytes = ResolveMemoryBytes(job.AgentDef?.RuntimeResources?.MemoryMB),
        };
    }

    private const long DefaultMemoryBytes = 8L * 1024L * 1024L * 1024L; // 8 GiB

    private static long ResolveMemoryBytes(int? memoryMB)
    {
        if (memoryMB is int mb && mb > 0)
            return (long)mb * 1024L * 1024L;
        return DefaultMemoryBytes;
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Agentics Runner Start[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();
    }

    private void DisplaySuccess(string message) =>
        _console.MarkupLine($"[green]{message}[/]");

    private void DisplayError(string message) =>
        _console.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

    private void DisplayInfo(string message) =>
        _console.MarkupLine($"[cyan]{message}[/]");

    /// <summary>Response wrapper for the /runners/jobs poll endpoint.</summary>
    private class PollResponse
    {
        public List<RunnerJob> Jobs { get; set; } = new();
    }

    /// <summary>Minimal job model returned by the server's /runners/jobs endpoint.</summary>
    private class RunnerJob
    {
        public string Id { get; set; } = "";
        public string? RunId { get; set; }
        public string? ProjectName { get; set; }
        public string? ProjectPath { get; set; }
        public string? DevcontainerPath { get; set; }
        /// <summary>Capability strings the claiming Runner had to declare to be matched to this Job
        /// (mirrors the Server's AgentJob.needs, populated from AgentDefinition.needs at dispatch
        /// time — see task-dispatch.ts). Used here to detect a Kind A chat-session:v1 Job
        /// (external/alp-spec/2026-03-30-draft/spec/13-chat.md), which carries
        /// needs: ["chat-session:v1"] but otherwise dispatches through the ordinary alp_operator
        /// spawn path with no JobType of its own.</summary>
        public List<string>? Needs { get; set; }
        public RunnerAgentDefinition? AgentDefinition { get; set; }

        /// <summary>Convenience accessor for agentDefinition.</summary>
        public RunnerAgentDefinition? AgentDef => AgentDefinition;

        /// <summary>True when this Job's needs declare the chat-session:v1 capability (Kind A chat
        /// Job). Checked in addition to Needs/AgentDefinition.Needs since the Server populates both
        /// with the same array — belt-and-suspenders against either being trimmed in a future change.</summary>
        public bool IsChatSession =>
            (Needs?.Contains(ChatSessionCapability) ?? false) ||
            (AgentDefinition?.Needs?.Contains(ChatSessionCapability) ?? false);

        /// <summary>True when this Job's needs declare the devcontainer-session:v1 capability (a
        /// devcontainer-hosted sub-agent session, plan `snappy-wandering-mochi` Phase 2/3). Same
        /// belt-and-suspenders check as IsChatSession. Not yet read anywhere -- reserved for Phase 3,
        /// which needs to tell this apart from a Kind A chat session (e.g. to launch the agent-share
        /// channel bridge instead of dialing the ALP Chat Channel).</summary>
        public bool IsDevAgentSession =>
            (Needs?.Contains(DevAgentSessionCapability) ?? false) ||
            (AgentDefinition?.Needs?.Contains(DevAgentSessionCapability) ?? false);
    }

    private class RunnerAgentDefinition
    {
        public string? Repository { get; set; }
        public string? Branch { get; set; }
        public string? Prompt { get; set; }
        public string? AppendSystemPrompt { get; set; }
        public string? SubagentPromptAppendix { get; set; }
        public List<string> Labels { get; set; } = new();
        public string? TaskId { get; set; }
        public string? AssemblyLineId { get; set; }
        public int? IdleTimeoutMinutes { get; set; }
        public int? MaxTimeoutMinutes { get; set; }
        public string? StageGitUrl { get; set; }
        public string? StageGitToken { get; set; }
        /// <summary>Token for the project's main repo.git endpoint — separate from the runner API token (AGENTICS_TOKEN).</summary>
        public string? ProjectRepoToken { get; set; }
        public Dictionary<string, string>? DevcontainerFiles { get; set; }
        /// <summary>
        /// Curated template reference. Used when DevcontainerFiles is null/empty —
        /// the runner resolves it (default source: nuget.org) and writes the rendered
        /// files into the workspace before `devcontainer up`.
        /// </summary>
        public DevcontainerTemplateRef? DevcontainerTemplate { get; set; }
        /// <summary>
        /// Operator-controlled scope for the Claude credentials Docker volume — see ADR 0004.
        /// "task": one volume per task (full isolation, OAuth per task).
        /// "project" (default): one volume per (owner, project), shared across tasks of that project.
        /// "runner": one volume per owner, shared across all the operator's projects.
        /// Anything else (or null) falls back to "project".
        /// </summary>
        public string? ClaudeCredentialsScope { get; set; }
        public List<PluginRef>? Plugins { get; set; }
        public List<AgentRef>? Agents { get; set; }
        /// <summary>
        /// Bearer tokens for marketplace authorities the plugin clones will
        /// touch. Runner spins up a local git proxy and registers each entry,
        /// then `git config --global url.&lt;proxy&gt;.insteadOf https://AUTH/`
        /// inside the container — so the existing `git clone` lines work
        /// without auth-aware plugins. Token never enters the container.
        /// </summary>
        public List<MarketplaceTokenRef>? MarketplaceTokens { get; set; }
        /// <summary>W3C traceparent header from the server-side span that dispatched this job.</summary>
        public string? Traceparent { get; set; }
        /// <summary>claudeSessionId from a prior timed-out run — when set, runner injects VIBECAST_RESUME_SESSION_ID so vibecast can pass --resume to Claude.</summary>
        public string? ResumeSessionId { get; set; }
        /// <summary>broadcastId from a prior timed-out run — when set, runner can pass it to vibecast to reuse the same broadcast channel.</summary>
        public string? ResumeBroadcastId { get; set; }
        /// <summary>broadcastId for the assembly line this job belongs to — runner passes BROADCAST_ID so vibecast streams to the right channel.</summary>
        public string? BroadcastId { get; set; }
        /// <summary>When true, vibecast blocks Claude from stopping until the working tree is clean (no uncommitted changes).</summary>
        public bool AutoGit { get; set; }
        /// <summary>When true, runner creates a task-scoped branch (task-{taskId}) before launching Claude.</summary>
        public bool InitBranch { get; set; }
        /// <summary>Commit message hint shown to Claude when AutoGit blocks session end due to uncommitted changes.</summary>
        public string? CommitMessageTemplate { get; set; }
        /// <summary>Lines to ensure exist in the project .gitignore — injected into the agent system prompt.</summary>
        public List<string>? GitignoreLines { get; set; }
        public RunnerOperatorConfig? OperatorConfig { get; set; }
        /// <summary>Per-job devcontainer resource limits resolved server-side from
        /// assembly-line settings. When null/empty, the runner applies an 8 GiB hard cap
        /// (HostConfig.Memory == HostConfig.MemorySwap) so the container can't spill to
        /// host swap and crash the host.</summary>
        public RunnerRuntimeResources? RuntimeResources { get; set; }
        /// <summary>User-uploaded task assets — runner downloads each into {workspace}/.agentics/assets/{fileName}.</summary>
        public List<TaskAssetDef>? TaskAssets { get; set; }
        /// <summary>Static control-plane files from the line's `.agentics/specs/` directory, keyed by
        /// workspace-relative path (e.g. ".agentics/specs/findings-schema.md"). Runner writes each into
        /// {workspace}/{path} before the station starts, so station prompts can Read the static contracts
        /// the workers never author themselves.</summary>
        public Dictionary<string, string>? AgenticsSpecFiles { get; set; }
        /// <summary>Job type — defaults to alp_operator. Use git_push for runner-proxied git push operations,
        /// git_distribute for source-code mirroring with an allowlist filter, or chat_llm for a bare
        /// chat-llm:v1 Job (external/alp-spec/2026-03-30-draft/spec/13-chat.md Kind B — no devcontainer;
        /// the Runner dials the Chat Channel directly and forwards chat-completions turns to its
        /// locally configured backend). Kind A (chat-session:v1) Jobs need no JobType of their own —
        /// they dispatch through the default alp_operator spawn path unchanged.</summary>
        public string? JobType { get; set; }
        /// <summary>Capability strings a runner had to declare to be matched to this Job (server-side
        /// AgentDefinition.needs). See RunnerJob.Needs/IsChatSession — this is the same array, just
        /// also reachable via AgentDef for call sites that already have that reference in hand.</summary>
        public List<string>? Needs { get; set; }
        /// <summary>Payload for git_push jobs. Runner clones targetRepo, writes files, commits, and pushes.</summary>
        public GitPushPayloadModel? GitPushPayload { get; set; }
        /// <summary>Payload for git_distribute jobs. Runner clones SourceRepo, applies the allowlist
        /// (AdminDistallowPatterns ∪ honored .agentics/.distallow from the source HEAD), replaces the
        /// TargetRepo working tree with the filtered subset, commits, and pushes. No credentials carried
        /// here — runner resolves source/target auth on its own.</summary>
        public DistributePayloadModel? DistributePayload { get; set; }
        /// <summary>Set only for the reserved devagent Task (plan `snappy-wandering-mochi` Phase 3) —
        /// the project's dedicated pks-agent-share inbox the outer chat's `delegate_to_devagent` tool
        /// pushes tasks into. Whenever this is present — regardless of IsDevAgentSession/needs, since
        /// www-site sends it on ordinary alp_operator jobs too — the runner writes a `.mcp.json`
        /// wiring Claude's `agent-share channel` stdio bridge to this inbox so the in-container session
        /// long-polls it and can reply via `complete()`.</summary>
        public RunnerDevAgentChannel? DevAgentChannel { get; set; }
    }

    private class RunnerDevAgentChannel
    {
        public string InboxId { get; set; } = "";
        public string Token { get; set; } = "";
        public string McpUrl { get; set; } = "";
    }

    private class TaskAssetDef
    {
        public string FileName { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public long Size { get; set; }
        public string Url { get; set; } = "";
    }

    private class GitPushPayloadModel
    {
        public string TargetRepo { get; set; } = "";
        public string TargetBranch { get; set; } = "main";
        public string CommitMessage { get; set; } = "chore: export assembly line";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    private class DistributePayloadModel
    {
        public string ProductId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string SourceRepo { get; set; } = "";
        public string SourceBranch { get; set; } = "main";
        public string TargetRepo { get; set; } = "";
        public string TargetBranch { get; set; } = "main";
        /// <summary>`{sourceCommit}` is replaced with the source HEAD SHA at runner time.</summary>
        public string CommitMessage { get; set; } = "chore: sync from agentics ({sourceCommit})";
        public List<string> AdminDistallowPatterns { get; set; } = new();
        public bool HonorDistallow { get; set; } = true;
    }

    private class RunnerRuntimeResources
    {
        /// <summary>Hard memory cap in megabytes. Applied as both HostConfig.Memory and
        /// HostConfig.MemorySwap so the container can't spill to host swap.</summary>
        public int? MemoryMB { get; set; }
    }

    private class RunnerOperatorConfig
    {
        public bool AutoApproveImageUploads { get; set; }
        public bool DisableBackgroundTasks { get; set; }
        /// <summary>
        /// Opt-in to Claude Sonnet's 1M-token context window. Resolved server-side
        /// from the station / assembly-line settings. When false (default), the runner
        /// exports CLAUDE_CODE_DISABLE_1M_CONTEXT=1 to remove 1M model variants
        /// from Claude's model picker.
        /// </summary>
        public bool Enable1mContext { get; set; }
        /// <summary>Model family tier (haiku|sonnet|opus), resolved server-side
        /// (station ?? line default). Exported as VIBECAST_CLAUDE_MODEL_TIER; vibecast maps
        /// it to `claude --model &lt;alias&gt;`.</summary>
        public string? ModelTier { get; set; }
        /// <summary>Exact model id/alias (station only), overriding ModelTier. Exported as
        /// VIBECAST_CLAUDE_MODEL; vibecast honors it when the provider supports it.</summary>
        public string? Model { get; set; }
        /// <summary>Effort level (low|medium|high|xhigh|max), resolved server-side
        /// (station ?? line default). Exported as VIBECAST_CLAUDE_EFFORT; vibecast maps it to
        /// `claude --effort &lt;level&gt;`.</summary>
        public string? Effort { get; set; }
        /// <summary>Coding-agent CLI the vibecast Operator runs (claude|codex|pi), resolved
        /// server-side (station ?? line default). Exported as VIBECAST_AGENT; unset → vibecast
        /// defaults to claude. vibecast owns the per-agent launch + config-seed.</summary>
        public string? Agent { get; set; }
    }

    private class PluginRef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SourceUrl { get; set; } = "";
    }

    private class AgentRef
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Content { get; set; } = "";
    }

    /// <summary>
    /// Pair of (marketplace authority, bearer JWT). agentic-live emits one of
    /// these for every distinct marketplace host the plugin list references.
    /// </summary>
    private class MarketplaceTokenRef
    {
        /// <summary>Host[:port] of the marketplace (e.g. "x.devtunnels.ms" or "marketplace.agentics.dk").</summary>
        public string Authority { get; set; } = "";
        /// <summary>The Bearer JWT (or opaque bearer for bearer-mode profiles).</summary>
        public string Bearer { get; set; } = "";
        /// <summary>Optional explicit upstream prefix override. Defaults to <c>https://{Authority}/</c>.</summary>
        public string? UpstreamPrefix { get; set; }
    }

    private class RegisterRunnerResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
