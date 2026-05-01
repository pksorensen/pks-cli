using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PKS.Infrastructure.Services;
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
    private readonly IAnsiConsole _console;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
    }

    public AgenticsRunnerStartCommand(
        IAgenticsRunnerConfigurationService configService,
        IDevcontainerSpawnerService spawnerService,
        IHttpClientFactory httpClientFactory,
        IGitHubAuthenticationService githubAuth,
        IAzureFoundryAuthService foundryAuthService,
        AzureFoundryAuthConfig foundryConfig,
        IAnsiConsole console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _githubAuth = githubAuth ?? throw new ArgumentNullException(nameof(githubAuth));
        _foundryAuthService = foundryAuthService ?? throw new ArgumentNullException(nameof(foundryAuthService));
        _foundryConfig = foundryConfig ?? throw new ArgumentNullException(nameof(foundryConfig));
        _console = console ?? throw new ArgumentNullException(nameof(console));
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

            // GitHub authentication pre-flight: only run when the project actually
            // points at github.com. Self-hosted projects (repo on the agentics server)
            // never need GitHub credentials, so prompting would be both unnecessary and
            // surprising. The runner queries the server to find out.
            var requiresGitHub = false;
            if (!settings.InProcess)
            {
                requiresGitHub = await ProjectRequiresGitHubAsync(registration);
                if (!requiresGitHub && settings.Verbose)
                {
                    DisplayInfo("Project does not use GitHub — skipping GitHub auth preflight.");
                }
            }
            if (!settings.InProcess && requiresGitHub)
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
            if (!settings.InProcess)
            {
                // Start credential server (serves locally stored device-code OAuth token)
                credentialServer = new GitCredentialServer(_githubAuth, registration.Id);
                await credentialServer.StartAsync();

                if (settings.Verbose)
                {
                    DisplayInfo($"Credential server started at: {credentialServer.SocketPath}");
                }
            }
            else
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

                // Compute runner capabilities once at startup
                var capabilities = await ComputeCapabilitiesAsync(settings.InProcess, cts.Token);
                _console.MarkupLine($"[dim]Runner capabilities: {string.Join(", ", capabilities)}[/]");

                // Polling loop
                var jobsProcessed = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var job = await PollForJobAsync(registration, capabilities, cts.Token);

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
                                await ExecuteGitPushJobAsync(registration, job, settings, cts.Token);
                                jobsProcessed++;
                                _console.MarkupLine($"[green]Git push job completed.[/]");
                            }
                            else if (settings.InProcess)
                            {
                                await ExecuteInProcessAsync(registration, job, settings, cts.Token);
                                jobsProcessed++;
                                _console.MarkupLine($"[green]InProcess job completed.[/]");
                            }
                            else
                            {
                                await ExecuteSpawnModeAsync(registration, job, settings, credentialServer!, cts.Token);
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

                // Clean up all active jobs on shutdown
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
        var requestBody = new { name = runnerName, labels = Array.Empty<string>() };
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
    /// Computes the capability strings this runner instance supports.
    /// "alp_operator" — always present (can run vibecast/claude jobs).
    /// "git:push"       — present when a GitHub token is stored (so git push won't prompt).
    /// </summary>
    private async Task<List<string>> ComputeCapabilitiesAsync(bool inProcess, CancellationToken ct)
    {
        var caps = new List<string> { "alp_operator" };

        // In --inprocess mode: check whether the stored GitHub token is valid.
        // In spawn mode:       the GitCredentialServer is always started with a stored token,
        //                      so we only get here after authentication succeeded (see startup preflight).
        var hasGitCredentials = inProcess
            ? await _githubAuth.IsAuthenticatedAsync()
            : true; // spawn mode always authenticated (preflight enforced above)

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

            var spawnResult = await _spawnerService.SpawnLocalAsync(spawnOptions, msg =>
            {
                _console.MarkupLine($"[dim]{msg.EscapeMarkup()}[/]");
            });

            if (!spawnResult.Success || spawnResult.ContainerId == null)
            {
                var errorMsg = string.Join("; ", new[] { spawnResult.Message }
                    .Concat(spawnResult.Errors)
                    .Where(s => !string.IsNullOrEmpty(s)));
                _console.MarkupLine($"[red]Devcontainer spawn failed:[/] {spawnResult.Message.EscapeMarkup()}");
                await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
                await ReportJobResultAsync(registration, "failed", errorMsg, ct);
                return;
            }

            containerId = spawnResult.ContainerId;
            _console.MarkupLine($"[green]Container ready:[/] {containerId[..12]} (labelled for reuse)");
        }

        // 4. PATCH to in_progress now that the container is running
        await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

        // 4b. Install plugins INSIDE the container at $HOME/.alp/plugins (per ADR 0003).
        // Clean slate every job so station transitions get the right tool belt without
        // recreating the container. No Docker volume → no "volume in use" cleanup races.
        if (hasPlugins || hasAgents)
        {
            await _spawnerService.ExecInContainerAsync(containerId,
                "bash -c 'rm -rf $HOME/.alp/plugins/* $HOME/.alp/plugins/.* 2>/dev/null; mkdir -p $HOME/.alp/plugins'",
                timeoutSeconds: 30);

            if (hasPlugins)
            {
                foreach (var plugin in job.AgentDef!.Plugins!)
                {
                    var pluginUrl = plugin.SourceUrl;
                    var pluginName = plugin.Name;
                    if (string.IsNullOrEmpty(pluginUrl) || string.IsNullOrEmpty(pluginName))
                    {
                        _console.MarkupLine($"[yellow]Skipping plugin with missing url or name[/]");
                        continue;
                    }
                    var dest = $"$HOME/.alp/plugins/{pluginName}";
                    var cloneRes = await _spawnerService.ExecInContainerAsync(containerId,
                        $"bash -c 'git clone --depth 1 \"{pluginUrl}\" \"{dest}\" 2>&1'",
                        timeoutSeconds: 60);
                    if (cloneRes.ExitCode != 0)
                    {
                        _console.MarkupLine($"[red]Failed to clone plugin {pluginName}: {cloneRes.Output.Trim().EscapeMarkup()}[/]");
                        continue;
                    }
                    pluginContainerPaths.Add(dest);
                    _console.MarkupLine($"  [green]✓[/] cloned {pluginName} → {dest}");
                }
            }

            if (hasAgents)
            {
                // Mirror the existing layout: a synthetic plugin dir with .claude-plugin/plugin.json
                // and agents/{agentId}.md. Vibecast picks it up as just another plugin.
                var agentDir = $"$HOME/.alp/plugins/agents-{job.Id}";
                var pluginJson = $$"""{"name":"task-agents","version":"1.0.0","description":"Agents for job {{job.Id}}"}""";
                var pluginJsonB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pluginJson));
                await _spawnerService.ExecInContainerAsync(containerId,
                    $"bash -c 'mkdir -p {agentDir}/.claude-plugin {agentDir}/agents && " +
                    $"printf \"%s\" \"{pluginJsonB64}\" | base64 -d > {agentDir}/.claude-plugin/plugin.json'",
                    timeoutSeconds: 10);
                foreach (var agent in job.AgentDef!.Agents!)
                {
                    if (string.IsNullOrEmpty(agent.Id) || string.IsNullOrEmpty(agent.Content)) continue;
                    var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(agent.Content));
                    await _spawnerService.ExecInContainerAsync(containerId,
                        $"bash -c 'printf \"%s\" \"{b64}\" | base64 -d > {agentDir}/agents/{agent.Id}.md'",
                        timeoutSeconds: 10);
                }
                pluginContainerPaths.Add(agentDir);
                _console.MarkupLine($"  [green]✓[/] wrote {job.AgentDef.Agents!.Count} agent file(s) → {agentDir}");
            }
        }

        // 5. Determine workspace folder and AGENTIC_SERVER for inside the container
        var repoName = spawnOptions.ProjectName;
        var workspaceFolder = $"/workspaces/{repoName}";

        // localhost on the host is not localhost inside the container — use host.docker.internal
        var serverUri = new Uri(registration.Server);
        var hostForContainer = serverUri.Host is "localhost" or "127.0.0.1"
            ? "host.docker.internal"
            : serverUri.Host;
        var agenticServerForContainer = $"{hostForContainer}:{serverUri.Port}";

        // 6. Write prompt file into the container (base64 to avoid quoting issues)
        var vibecastHome = $"/tmp/vibecast-job-{job.Id}";
        var promptFile = $"{vibecastHome}/initial-prompt.txt";
        var jobPrompt = job.AgentDef?.Prompt ?? "";
        await _spawnerService.ExecInContainerAsync(containerId,
            $"bash -c 'mkdir -p {vibecastHome}'",
            timeoutSeconds: 30);

        // Local helper to write arbitrary file content into the container without shell quoting issues.
        // Uses base64 transport — caller's content can contain any bytes, including newlines, quotes, $, ()
        // — none of it is interpreted by bash.
        async Task WriteContainerFileAsync(string path, string content, int timeoutSeconds = 30)
        {
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
            await _spawnerService.ExecInContainerAsync(containerId,
                $"bash -c 'mkdir -p \"$(dirname {path})\" && printf \"%s\" \"{b64}\" | base64 -d > {path}'",
                timeoutSeconds: timeoutSeconds);
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
              }
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
        const string otlpBridgePath = "/tmp/otlp-bridge.js";
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
        // Write a targeted .claude/.gitignore so only job/session-scoped files are excluded from git.
        // A blanket "*" would also suppress MCP configs written by tools like `aspire agent init`.
        scriptLines.AppendLine("mkdir -p .claude");
        scriptLines.AppendLine("[ -f .claude/.gitignore ] || printf '%s\\n' '# Runner-injected — never commit' 'settings.local.json' > .claude/.gitignore");
        scriptLines.AppendLine("exec ${VIBECAST_BIN:-npx --yes vibecast}");

        var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(scriptLines.ToString().Replace("\r\n", "\n")));
        await _spawnerService.ExecInContainerAsync(containerId,
            $"bash -c 'printf \"%s\" \"{scriptB64}\" | base64 -d > {launchScript} && chmod +x {launchScript}'",
            timeoutSeconds: 30);

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
                    var actUrl = $"{registration.Server.TrimEnd('/')}/api/lives/activity?streamId={sessionIdValue}&idleThresholdMs={idleTimeoutMs}";
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
  }
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

            startPsi.ArgumentList.Add($"cd {jobWorkTree} && HOME={realHome} VIBECAST_HOME={vibecastHome} VIBECAST_BIN={vibecastBin} AGENTICS_SERVER={agenticServer} AGENTIC_SERVER={agenticServer} AGENTICS_PROJECT={registration.Owner}/{registration.Project} AGENTICS_JOB_ID={job.Id} AGENTICS_TOKEN='{registration.Token}' AGENTICS_OWNER='{registration.Owner}' AGENTICS_PROJECT_NAME='{registration.Project}' AGENTICS_BASE_URL='{agenticsBaseUrl}' AGENTICS_JOB_MODE=1{keyboardPinEnv}{initialPromptEnv}{appendPromptEnv}{stageGitEnv}{extraPluginsEnv}{traceparentEnv}{resumeEnv}{autoGitEnv}{autoApproveEnv}{disableBackgroundTasksEnv}{subagentSuffixEnv}{broadcastIdEnv}{allowedDirsEnv}{claudeConfigDirEnv}{otelEnv}{agenticsProxyEnv} {vibecastBin}{vibecastLogRedirect}");

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
                        var activityUrl = $"{scheme}://{serverUri.Host}:{serverUri.Port}/api/lives/activity?streamId={sessionIdValue}&idleThresholdMs={idleTimeoutMs}";
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
            if (stored == null || string.IsNullOrEmpty(stored.AccessToken))
                return new Dictionary<string, string>();

            // Write a GIT_ASKPASS script that provides the token non-interactively
            var scriptPath = Path.Combine(Path.GetTempPath(), $"git-askpass-{Guid.NewGuid():N}.sh");
            var token = stored.AccessToken;
            await File.WriteAllTextAsync(scriptPath,
                $"#!/bin/sh\ncase \"$1\" in\n  *Username*) echo \"x-access-token\" ;;\n  *Password*) echo \"{token}\" ;;\n  *) echo \"\" ;;\nesac\n", ct);

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return new Dictionary<string, string>
            {
                ["GIT_ASKPASS"] = scriptPath,
                ["GIT_TERMINAL_PROMPT"] = "0",
            };
        }
        catch
        {
            return new Dictionary<string, string>();
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
        static string Sanitize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "-");

        var effectiveScope = scope?.ToLowerInvariant() switch
        {
            "task" or "project" or "runner" => scope!.ToLowerInvariant(),
            _ => "project",
        };
        var stableVolume = effectiveScope switch
        {
            "task" when !string.IsNullOrEmpty(taskId) =>
                $"pks-claude-{Sanitize(owner)}-{Sanitize(project)}-task-{Sanitize(taskId)}",
            "runner" => $"pks-claude-{Sanitize(owner)}",
            _ => $"pks-claude-{Sanitize(owner)}-{Sanitize(project)}",
        };

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
        };
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
        public RunnerAgentDefinition? AgentDefinition { get; set; }

        /// <summary>Convenience accessor for agentDefinition.</summary>
        public RunnerAgentDefinition? AgentDef => AgentDefinition;
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
        /// <summary>User-uploaded task assets — runner downloads each into {workspace}/.agentics/assets/{fileName}.</summary>
        public List<TaskAssetDef>? TaskAssets { get; set; }
        /// <summary>Job type — defaults to alp_operator. Use git_push for runner-proxied git push operations.</summary>
        public string? JobType { get; set; }
        /// <summary>Payload for git_push jobs. Runner clones targetRepo, writes files, commits, and pushes.</summary>
        public GitPushPayloadModel? GitPushPayload { get; set; }
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

    private class RunnerOperatorConfig
    {
        public bool AutoApproveImageUploads { get; set; }
        public bool DisableBackgroundTasks { get; set; }
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

    private class RegisterRunnerResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
