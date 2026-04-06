using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PKS.Infrastructure.Services;
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
    private readonly IAnsiConsole _console;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>ActivitySource name used by the runner. Referenced by Program.cs when building the TracerProvider.</summary>
    public const string ActivitySourceName = "pks-cli.agentics.runner";
    private static readonly System.Diagnostics.ActivitySource _activitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>Global monotonic counter so debug captures sort correctly across concurrent jobs.</summary>
    private static int _captureSeq = 0;

    // Container reuse is tracked via Docker labels (pks.agentics.fingerprint) rather than
    // in-memory state, so containers survive pks-cli restarts.

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
        IAnsiConsole console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _githubAuth = githubAuth ?? throw new ArgumentNullException(nameof(githubAuth));
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

            // GitHub authentication pre-flight: ensure a valid token is stored so
            // the credential server can serve it and git clones succeed.
            // Flow: check stored token → try refresh → device code login.
            if (!settings.InProcess)
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

                // Polling loop
                var jobsProcessed = 0;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var job = await PollForJobAsync(registration, cts.Token);
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
                            jobActivity?.SetTag("agentics.stage_id", job.AgentDef?.StageId);
                            jobActivity?.SetTag("agentics.mode", settings.InProcess ? "inprocess" : "spawn");

                            if (settings.InProcess)
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
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _console.MarkupLine($"[red]Polling error:[/] {ex.Message.EscapeMarkup()}");
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

    private async Task<RunnerJob?> PollForJobAsync(AgenticsRunnerRegistration registration, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var response = await client.PostAsJsonAsync(
            $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs",
            new { },
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

        // 2. Check for a warm container via Docker labels (survives pks-cli restarts)
        var storedToken = await _githubAuth.GetStoredTokenAsync();
        var spawnOptions = BuildSpawnOptions(job, credentialServer.SocketPath, registration, storedToken?.AccessToken);

        // Patch claude config volume to a stable name so credentials persist across container respawns
        spawnOptions.InlineDevcontainerFiles = PatchDevcontainerVolumes(
            spawnOptions.InlineDevcontainerFiles, registration.Owner, registration.Project);

        // Fingerprint computed AFTER patching so cache key matches what gets deployed
        var fingerprint = ComputeDevcontainerFingerprint(
            registration.Owner, registration.Project, spawnOptions.InlineDevcontainerFiles);

        // Stamp the container with labels so we can rediscover it after pks-cli restarts
        spawnOptions.IdLabels = new Dictionary<string, string>
        {
            ["pks.agentics.owner"]       = registration.Owner,
            ["pks.agentics.project"]     = registration.Project,
            ["pks.agentics.fingerprint"] = fingerprint,
        };

        // Acquire plugins and agent plugin dirs on the Runner side, populating a Docker volume
        // mounted at /run/alp/plugins inside the devcontainer.
        var hasPlugins = job.AgentDef?.Plugins?.Count > 0;
        var hasAgents  = job.AgentDef?.Agents?.Count  > 0;
        string? pluginVolumeName = null;
        var pluginContainerPaths = new List<string>();

        if (hasPlugins || hasAgents)
        {
            var volumeName = $"alp-plugins-{job.Id}";
            var createResult = await RunCaptureAsync("docker", new[] { "volume", "create", volumeName }, ct);
            if (createResult.ExitCode == 0)
            {
                pluginVolumeName = volumeName;
                _console.MarkupLine($"[cyan]Created ALP plugin volume: {volumeName}[/]");

                if (hasPlugins)
                {
                    var clonedPaths = await ClonePluginsIntoVolumeAsync(
                        job.AgentDef!.Plugins!, volumeName, ct);
                    pluginContainerPaths.AddRange(clonedPaths);
                }

                if (hasAgents)
                {
                    var agentPluginPath = await WriteAgentPluginDirInVolumeAsync(
                        job.AgentDef!.Agents!, volumeName, job.Id, ct);
                    if (agentPluginPath != null)
                        pluginContainerPaths.Add(agentPluginPath);
                }
            }
            else
            {
                _console.MarkupLine($"[red]Failed to create plugin volume {volumeName}: {createResult.Stderr.EscapeMarkup()}[/]");
            }
        }

        if (pluginVolumeName != null)
            spawnOptions.PluginVolumeName = pluginVolumeName;

        string containerId;
        var warmId = await FindContainerByLabelAsync($"pks.agentics.fingerprint={fingerprint}");
        if (warmId != null)
        {
            _console.MarkupLine($"[green]Reusing warm container:[/] {warmId[..12]} (devcontainer unchanged)");
            containerId = warmId;
        }
        else
        {
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
        var promptFile   = $"{vibecastHome}/initial-prompt.txt";
        var jobPrompt    = job.AgentDef?.Prompt ?? "";
        await _spawnerService.ExecInContainerAsync(containerId,
            $"bash -c 'mkdir -p {vibecastHome}'",
            timeoutSeconds: 30);
        if (!string.IsNullOrEmpty(jobPrompt))
        {
            var promptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(jobPrompt));
            _console.MarkupLine($"[cyan]Writing prompt to {promptFile}...[/]");
            await _spawnerService.ExecInContainerAsync(containerId,
                $"bash -c 'printf \"%s\" \"{promptB64}\" | base64 -d > {promptFile}'",
                timeoutSeconds: 30);
        }

        // 7. Build and write a launch script into the container to avoid shell quoting issues
        var vibecastTmux = $"vibecast-{job.Id[..8]}";
        var defaultAppendPrompt = "When you have completed the assigned task, use the stop_broadcast MCP tool " +
            "with a message summarizing what you accomplished and conclusion success. " +
            "If you encounter an unrecoverable error, call stop_broadcast with conclusion failure and describe the issue.";
        var appendPrompt = !string.IsNullOrWhiteSpace(job.AgentDef?.AppendSystemPrompt)
            ? job.AgentDef.AppendSystemPrompt + "\n\n" + defaultAppendPrompt
            : defaultAppendPrompt;
        var stageGitUrl   = job.AgentDef?.StageGitUrl ?? "";
        var stageGitToken = job.AgentDef?.StageGitToken ?? "";
        var stageDir      = $"{vibecastHome}/stage";

        // Rebase stage git URL onto the container-accessible server (same host/port as AGENTIC_SERVER).
        // The stage git server IS the agentic server, so we reuse hostForContainer + serverUri.Port.
        if (!string.IsNullOrEmpty(stageGitUrl))
        {
            var stageUri = new Uri(stageGitUrl);
            var rebased = new UriBuilder(stageUri)
            {
                Host   = hostForContainer,
                Port   = serverUri.Port,
                Scheme = serverUri.Scheme,
            };
            stageGitUrl = rebased.Uri.ToString();
        }

        var launchScript = $"{vibecastHome}/start.sh";
        var scriptLines = new System.Text.StringBuilder();
        scriptLines.AppendLine("#!/bin/bash");
        // Unset TMUX so vibecast doesn't inherit the outer container tmux session context.
        // If TMUX is set, vibecast's ttyd inherits it and "tmux attach" refuses to nest sessions,
        // causing the broadcast relay to see nothing instead of the Claude Code window.
        scriptLines.AppendLine("unset TMUX");
        // Ensure user-local bin is on PATH so claude is found (e.g. /home/node/.local/bin)
        scriptLines.AppendLine("export PATH=\"$HOME/.local/bin:/home/node/.local/bin:/usr/local/bin:/usr/bin:/bin:$PATH\"");
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
        scriptLines.AppendLine($"export VIBECAST_APPEND_SYSTEM_PROMPT=\"{appendPrompt}\"");

        // Expose pre-cloned plugins via VIBECAST_EXTRA_PLUGINS so vibecast passes --plugin-dir
        // to Claude for each one. Plugins are cloned by the Runner (pks-cli) before container
        // spawn and mounted at /run/alp/plugins via a dedicated Docker volume — see PreparePluginVolumeAsync.
        if (pluginContainerPaths.Count > 0)
        {
            scriptLines.AppendLine($"export VIBECAST_EXTRA_PLUGINS=\"{string.Join(":", pluginContainerPaths)}\"");
        }

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
        }

        var gitUserName  = settings.GitUserName  ?? "si-14x";
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

        // Remove any stale .mcp.json left by an older vibecast session (plugin dir handles MCP now)
        scriptLines.AppendLine("rm -f .mcp.json");
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
                _console.MarkupLine($"[grey]vibecast log (t+{i+1}s):[/] {log.Output.Trim().EscapeMarkup()}");

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

        // 10. Get streamId and link to task
        string? streamIdValue = null;
        for (var i = 0; i < 30; i++)
        {
            var status = await _spawnerService.ExecInContainerAsync(containerId,
                $"curl -sf --unix-socket {controlSocket} http://localhost/status",
                timeoutSeconds: 5);
            if (status.Output.Contains("\"phase\":\"live\""))
            {
                var m = System.Text.RegularExpressions.Regex.Match(status.Output, "\"streamId\":\"([^\"]+)\"");
                if (m.Success) { streamIdValue = m.Groups[1].Value; break; }
            }
            await Task.Delay(1000, ct);
        }

        // Print vibecast log so far to help diagnose connection issues
        var vibecastLogSnapshot = await _spawnerService.ExecInContainerAsync(containerId,
            $"cat {vibecastHome}/vibecast.log 2>/dev/null || echo '(empty)'", timeoutSeconds: 5);
        if (!string.IsNullOrWhiteSpace(vibecastLogSnapshot.Output))
            _console.MarkupLine($"[grey]vibecast log:[/]\n{vibecastLogSnapshot.Output.Trim().EscapeMarkup()}");

        if (streamIdValue != null)
        {
            _console.MarkupLine($"[green]Streaming live! streamId: {streamIdValue}[/]");
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct, streamIdValue);

            if (job.AgentDef?.TaskId != null && job.AgentDef?.StageId != null)
            {
                try
                {
                    var linkReq = new HttpRequestMessage(HttpMethod.Post,
                        $"{baseUrl}/stages/{job.AgentDef.StageId}/tasks/{job.AgentDef.TaskId}/streams");
                    linkReq.Content = JsonContent.Create(new { streamId = streamIdValue });
                    await client.SendAsync(linkReq, ct);
                    _console.MarkupLine($"[green]Stream linked to task {job.AgentDef.TaskId}[/]");
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[yellow]Failed to link stream to task: {ex.Message.EscapeMarkup()}[/]");
                }
            }

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
                        streamId = streamIdValue,
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
        }
        else
        {
            _console.MarkupLine("[yellow]Streaming session not detected, continuing anyway...[/]");
        }

        // 11. Wait for job to complete (tmux session ends, idle timeout, or max timeout)
        var idleTimeoutMs  = (job.AgentDef?.IdleTimeoutMinutes ?? 5) * 60_000;
        var maxTimeout     = TimeSpan.FromMinutes(job.AgentDef?.MaxTimeoutMinutes ?? 60);
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
            if (streamIdValue != null)
            {
                try
                {
                    var actUrl = $"{registration.Server.TrimEnd('/')}/api/lives/activity?streamId={streamIdValue}&idleThresholdMs={idleTimeoutMs}";
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

        // 14. Remove plugin volume now that the job is done
        if (pluginVolumeName != null)
            await RemovePluginVolumeAsync(pluginVolumeName);
    }

    /// <summary>
    /// Creates a Docker volume named <c>alp-plugins-{jobId}</c>, clones each declared plugin
    /// into it via a short-lived alpine/git container, and returns the container-side paths
    /// (<c>/run/alp/plugins/{pluginId}</c>) to expose via <c>VIBECAST_EXTRA_PLUGINS</c>.
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
                containerPaths.Add($"/run/alp/plugins/{plugin.Id}");
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

        return $"/run/alp/plugins/{dirName}";
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

    private async Task PatchJobStatusAsync(
        HttpClient client,
        string baseUrl,
        string runId,
        string jobId,
        string status,
        string? conclusion,
        CancellationToken ct,
        string? streamId = null)
    {
        try
        {
            var msg = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{baseUrl}/runs/{runId}/jobs/{jobId}");
            msg.Content = streamId != null
                ? JsonContent.Create(new { status, conclusion, streamId })
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
        var answerLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? answerLoopTask = null;

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

            // Write settings.local.json to pre-approve writes to .claude/ in the job work tree.
            // Claude Code (newer versions) prompts for permission when agents write to .claude/
            // even with dangerouslyAllowAllTools set — this silences that prompt.
            // Also write a .claude/.gitignore so git never tracks any job-scoped .claude/ files.
            {
                var claudeSettingsDir = Path.Combine(jobWorkTree, ".claude");
                Directory.CreateDirectory(claudeSettingsDir);
                var claudeSettingsFile = Path.Combine(claudeSettingsDir, "settings.local.json");
                await File.WriteAllTextAsync(claudeSettingsFile, """
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
                // Prevent any .claude/ contents from being committed by Claude during auto-git.
                var claudeGitignoreFile = Path.Combine(claudeSettingsDir, ".gitignore");
                if (!File.Exists(claudeGitignoreFile))
                    await File.WriteAllTextAsync(claudeGitignoreFile, "*\n", ct);
                _console.MarkupLine($"[cyan]Pre-wrote .claude/settings.local.json and .claude/.gitignore in {jobWorkTree}[/]");
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
            }

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

            startPsi.ArgumentList.Add($"cd {jobWorkTree} && HOME={realHome} VIBECAST_HOME={vibecastHome} VIBECAST_BIN={vibecastBin} AGENTICS_SERVER={agenticServer} AGENTIC_SERVER={agenticServer} AGENTICS_PROJECT={registration.Owner}/{registration.Project} AGENTICS_JOB_ID={job.Id} AGENTICS_TOKEN='{registration.Token}' AGENTICS_OWNER='{registration.Owner}' AGENTICS_PROJECT_NAME='{registration.Project}' AGENTICS_BASE_URL='{agenticsBaseUrl}' AGENTICS_JOB_MODE=1{keyboardPinEnv}{initialPromptEnv}{appendPromptEnv}{stageGitEnv}{extraPluginsEnv}{traceparentEnv}{resumeEnv}{autoGitEnv} {vibecastBin}");

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
                throw new InvalidOperationException("vibecast control socket not found");
            }

            // Small delay for control server to fully initialize
            await Task.Delay(1000, ct);

            // 9. Trigger start-stream via control socket
            _console.MarkupLine("[cyan]Triggering start-stream...[/]");
            await SendControlSocketRequestAsync(controlSocket, "POST", "/start-stream",
                """{"promptSharing":true,"shareProjectInfo":true}""", ct);

            // 10. Wait for streaming session to be created (vibecast-<streamId>)
            _console.MarkupLine("[cyan]Waiting for streaming session...[/]");
            string? streamingSession = null;
            string? streamIdValue = null;
            for (var i = 0; i < 30; i++)
            {
                // Check /status on control socket to get streamId
                var statusJson = await SendControlSocketRequestAsync(controlSocket, "GET", "/status", null, ct);
                if (statusJson != null)
                {
                    var statusData = JsonSerializer.Deserialize<JsonElement>(statusJson, JsonOptions);
                    if (statusData.TryGetProperty("streamId", out var sid) && sid.GetString() is { Length: > 0 } sId)
                    {
                        if (statusData.TryGetProperty("phase", out var phaseEl) && phaseEl.GetString() == "live")
                        {
                            streamIdValue = sId;
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
                _console.MarkupLine("[yellow]Streaming session not detected, will try to paste prompt anyway...[/]");
            }
            agentSpan?.SetTag("stream.id", streamIdValue ?? "");
            agentSpan?.Dispose();

            // 11. Update job with streamId and link stream to project
            if (streamIdValue != null)
            {
                _console.MarkupLine($"[cyan]Updating job with streamId: {streamIdValue}[/]");
                var patchStream = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{job.Id}");
                patchStream.Content = JsonContent.Create(new { status = "in_progress", streamId = streamIdValue });
                patchStream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
                await client.SendAsync(patchStream, ct);

                // Link stream to the task so the task card glows when live
                if (job.AgentDef?.TaskId != null && job.AgentDef?.StageId != null)
                {
                    try
                    {
                        var linkReq = new HttpRequestMessage(HttpMethod.Post,
                            $"{baseUrl}/stages/{job.AgentDef.StageId}/tasks/{job.AgentDef.TaskId}/streams");
                        linkReq.Content = JsonContent.Create(new { streamId = streamIdValue });
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
            _console.MarkupLine($"[green]Prompt will be delivered via VIBECAST_INITIAL_PROMPT_FILE ({promptFile})[/]");

            // 12b. Start background answer-injection loop if this job is linked to a task.
            // Polls the server for pending questions and injects answers via tmux send-keys.
            if (job.AgentDef?.TaskId != null && job.AgentDef?.StageId != null && streamingSession != null)
            {
                var vibecastSession = streamingSession; // vibecast names its tmux session after the stream ID
                answerLoopTask = Task.Run(async () =>
                {
                    var lastInjectedToolUseId = "";
                    var lastSeenToolUseId = ""; // survives pendingQuestion being cleared after user answers
                    _console.MarkupLine("[cyan]Answer injection loop started.[/]");
                    while (!answerLoopCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(3000, answerLoopCts.Token);

                            // Fetch current pending question from the task
                            var tasksResp = await client.GetAsync(
                                $"{baseUrl}/stages/{job.AgentDef.StageId}/tasks",
                                answerLoopCts.Token);
                            if (!tasksResp.IsSuccessStatusCode) continue;

                            var tasksJson = await tasksResp.Content.ReadAsStringAsync(answerLoopCts.Token);
                            using var tasksDoc = JsonDocument.Parse(tasksJson);
                            // Clone the matched element before the doc is disposed
                            JsonElement? matchedRaw = null;
                            foreach (var t in tasksDoc.RootElement.EnumerateArray())
                            {
                                if (t.TryGetProperty("id", out var idEl) && idEl.GetString() == job.AgentDef.TaskId)
                                {
                                    matchedRaw = t.Clone();
                                    break;
                                }
                            }
                            if (matchedRaw == null) continue;
                            var matchedTask = matchedRaw.Value;

                            // Track the toolUseId from pendingQuestion while it exists.
                            // IMPORTANT: when the user submits an answer, clearTaskPendingQuestion removes
                            // pendingQuestion from the task — so we must remember the last seen toolUseId
                            // and still poll for its answer, otherwise we miss the window.
                            if (matchedTask.TryGetProperty("pendingQuestion", out var pq) &&
                                pq.TryGetProperty("toolUseId", out var tuid) &&
                                tuid.GetString() is { Length: > 0 } activeTuid)
                            {
                                lastSeenToolUseId = activeTuid;
                            }

                            // ── Permission request branch ──────────────────────────────────────────
                            // When Claude Code shows a permission dialog in the tmux pane,
                            // the PermissionRequest hook records a pendingPermission + vote on the task.
                            // We poll the vote, and when resolved send Down+Enter to pick Allow/Deny.
                            if (matchedTask.TryGetProperty("pendingPermission", out var pp) &&
                                pp.TryGetProperty("toolUseId", out var ppTuid) &&
                                pp.TryGetProperty("streamId", out var ppStream) &&
                                ppTuid.GetString() is { Length: > 0 } permToolUseId &&
                                permToolUseId != lastInjectedToolUseId)
                            {
                                var permStreamId = ppStream.GetString() ?? "";
                                var voteResp = await client.GetAsync(
                                    $"{agenticsBaseUrl}/api/lives/question-vote?streamId={Uri.EscapeDataString(permStreamId)}&toolUseId={Uri.EscapeDataString(permToolUseId)}",
                                    answerLoopCts.Token);
                                if (voteResp.IsSuccessStatusCode)
                                {
                                    var voteJson = await voteResp.Content.ReadAsStringAsync(answerLoopCts.Token);
                                    using var voteDoc = JsonDocument.Parse(voteJson);
                                    var voteRoot = voteDoc.RootElement;
                                    if (voteRoot.TryGetProperty("resolvedAnswer", out var resolvedEl) &&
                                        resolvedEl.GetString() is { Length: > 0 } resolvedAnswer)
                                    {
                                        // Vote resolved — inject answer via tmux exactly like single-question mode
                                        _console.MarkupLine($"[green]Permission vote resolved: {resolvedAnswer.EscapeMarkup()} for {permToolUseId}[/]");
                                        lastInjectedToolUseId = permToolUseId;

                                        var permDebugDir = Path.Combine(vibecastHome, "debug");
                                        var permPaneTarget = $"{vibecastSession}:main.0";
                                        var paneContent = await TmuxCaptureAndDebugAsync(permPaneTarget, "permission_before", permDebugDir, answerLoopCts.Token);

                                        // Map vote result to pane option: "Allow" → first Yes option, "Deny" → first No option
                                        var searchTerm = resolvedAnswer == "Allow" ? "Yes" : "No";
                                        var optionNumber = MatchOptionNumber(paneContent, searchTerm);
                                        if (optionNumber == 0) optionNumber = resolvedAnswer == "Allow" ? 1 : 3;

                                        var currentOption = SelectedOptionNumber(paneContent);
                                        var downs = optionNumber - currentOption;
                                        _console.MarkupLine($"[dim]  → permission: '{resolvedAnswer.EscapeMarkup()}' → option {optionNumber} ({downs} Down(s))[/]");
                                        for (var d = 0; d < downs; d++)
                                        {
                                            await TmuxSendKeyRawAsync(permPaneTarget, "Down", answerLoopCts.Token);
                                            await Task.Delay(100, answerLoopCts.Token);
                                        }
                                        await TmuxSendKeyRawAsync(permPaneTarget, "Enter", answerLoopCts.Token);
                                        // Skip the rest of this loop iteration so the wizard injection
                                        // doesn't run while the permission dialog is still dismissing.
                                        continue;
                                    }
                                }
                            }

                            // Poll for whichever toolUseId we know about (active or recently cleared)
                            var toolUseId = lastSeenToolUseId;
                            if (string.IsNullOrEmpty(toolUseId) || toolUseId == lastInjectedToolUseId) continue;

                            // Check if an answer is available
                            var ansResp = await client.GetAsync(
                                $"{baseUrl}/stages/{job.AgentDef.StageId}/tasks/{job.AgentDef.TaskId}/answer-question?toolUseId={Uri.EscapeDataString(toolUseId)}",
                                answerLoopCts.Token);
                            if (!ansResp.IsSuccessStatusCode) continue;

                            var ansJson = await ansResp.Content.ReadAsStringAsync(answerLoopCts.Token);
                            var ansData = JsonSerializer.Deserialize<JsonElement>(ansJson, JsonOptions);
                            if (!ansData.TryGetProperty("answer", out var ansEl) || ansEl.ValueKind == JsonValueKind.Null) continue;
                            var answer = ansEl.GetString() ?? "";
                            if (string.IsNullOrEmpty(answer)) continue;

                            // Inject answer into the vibecast tmux session.
                            // Target pane 0 of the "main" window explicitly — pane 1 is the fkeybar
                            // and may be active, so omitting the pane index drops keys into fkeybar.
                            //
                            // Claude Code's ask_followup_question renders two distinct UIs:
                            //   A) Simple text prompt — user types freely, sends with Enter
                            //   B) Multi-step selection wizard — each question has option 1 pre-selected,
                            //      navigate with Enter (select), Tab (next field / Submit), then Enter
                            //
                            // The server stores answers as a Q&A block (blank-line-separated pairs) when
                            // the web UI fills in the wizard. For wizard answers we must NOT type the raw
                            // text — instead we send Enter for each Q&A pair (accepting the pre-selected
                            // recommended option) then Tab + Enter to confirm the Submit button.
                            //
                            // A "wizard answer" is detected by the presence of blank-line-separated
                            // Q&A blocks (question line + answer line + blank line).
                            // A "plain answer" is a single non-empty block without that structure.
                            _console.MarkupLine($"[green]Injecting answer for toolUseId {toolUseId}: {answer.EscapeMarkup()}[/]");
                            // NOTE: lastInjectedToolUseId is set only after the first successful step so that
                            // a bail-out (wizard not visible yet) causes a retry on the next poll cycle.

                            var debugDir = Path.Combine(vibecastHome, "debug");
                            var paneTarget = $"{vibecastSession}:main.0";
                            var paragraphs = answer.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                            var isWizardAnswer = paragraphs.Length > 1;

                            if (isWizardAnswer)
                            {
                                // Wizard mode: for each Q&A block, capture the pane to read the visible
                                // options, match the answer text to an option number, and send that digit.
                                // Claude auto-advances to the next question when a valid option number
                                // is pressed. After all questions, Tab moves to Submit and Enter confirms.
                                _console.MarkupLine($"[dim]Wizard answer detected ({paragraphs.Length} steps)[/]");
                                var stepIdx = 0;
                                var wizardAborted = false;
                                string? prevStepContent = null;
                                foreach (var para in paragraphs)
                                {
                                    var lines = para.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                                    if (lines.Length < 2) continue;
                                    var answerText = lines[1].Trim(); // second line is the answer

                                    // Capture pane, retrying until numbered options are visible.
                                    // Pass prevStepContent so we wait for the wizard to visually
                                    // advance (content change) before reading the next question's options.
                                    // After Enter advances the wizard the next question may take
                                    // up to ~1.5s to render — retry avoids a spurious free-text fallback.
                                    var paneContent = await WaitForOptionsAsync(paneTarget, $"wizard_step{stepIdx}_before", debugDir, answerLoopCts.Token, previousContent: prevStepContent);
                                    var hasAnyOptions = System.Text.RegularExpressions.Regex.IsMatch(
                                        paneContent, @"^\s*\d+\.\s+\S",
                                        System.Text.RegularExpressions.RegexOptions.Multiline);

                                    if (!hasAnyOptions)
                                    {
                                        // The wizard UI isn't visible at all — we might be between dialogs or
                                        // the dialog was dismissed already. Bail out without marking as injected
                                        // so the next poll cycle retries when the wizard actually appears.
                                        _console.MarkupLine($"[yellow]Wizard not visible at step {stepIdx} (no numbered options), deferring injection[/]");
                                        wizardAborted = true;
                                        break;
                                    }

                                    var optionNumber = MatchOptionNumber(paneContent, answerText);

                                    if (optionNumber > 0)
                                    {
                                        // Mark as injected only once we've confirmed the wizard is on screen.
                                        if (stepIdx == 0) lastInjectedToolUseId = toolUseId;

                                        // The wizard cursor starts at option 1 (❯). Navigate down to the target
                                        // option with arrow keys, then press Enter to select and advance.
                                        // The wizard responds to Enter, not digit shortcuts.
                                        var currentOption = SelectedOptionNumber(paneContent);
                                        var downs = optionNumber - currentOption;
                                        _console.MarkupLine($"[dim]  → matched '{answerText.EscapeMarkup()}' to option {optionNumber} (cursor at {currentOption}, {downs} Down(s))[/]");
                                        for (var d = 0; d < downs; d++)
                                        {
                                            await TmuxSendKeyRawAsync(paneTarget, "Down", answerLoopCts.Token);
                                            await Task.Delay(100, answerLoopCts.Token);
                                        }
                                        // Enter selects the highlighted option and advances to the next question
                                        await TmuxSendKeyRawAsync(paneTarget, "Enter", answerLoopCts.Token);
                                        prevStepContent = paneContent; // remember so next step waits for change
                                    }
                                    else
                                    {
                                        // No matching numbered option — navigate to "Type something" if available.
                                        // Only type free text if "Type something" is visible in the wizard.
                                        // If it's not present the wizard isn't in the right state; bail out.
                                        var typeSomethingNum = TypeSomethingOptionNumber(paneContent);
                                        if (typeSomethingNum > 0)
                                        {
                                            if (stepIdx == 0) lastInjectedToolUseId = toolUseId;
                                            _console.MarkupLine($"[dim]  → no option match for '{answerText.EscapeMarkup()}', typing as free text[/]");
                                            var currentOption = SelectedOptionNumber(paneContent);
                                            var downs = typeSomethingNum - currentOption;
                                            for (var d = 0; d < downs; d++)
                                            {
                                                await TmuxSendKeyRawAsync(paneTarget, "Down", answerLoopCts.Token);
                                                await Task.Delay(100, answerLoopCts.Token);
                                            }
                                            await TmuxSendKeyRawAsync(paneTarget, "Enter", answerLoopCts.Token);
                                            await Task.Delay(300, answerLoopCts.Token);
                                            await TmuxSendKeysAsync(paneTarget, answerText, answerLoopCts.Token);
                                            prevStepContent = paneContent; // remember so next step waits for change
                                        }
                                        else
                                        {
                                            // Numbered options exist but none match and there's no free-text slot.
                                            // This likely means we're in a different dialog (e.g., permission).
                                            // Bail out without marking as injected — retry next cycle.
                                            _console.MarkupLine($"[yellow]No option match for '{answerText.EscapeMarkup()}' and no 'Type something' slot — deferring injection[/]");
                                            wizardAborted = true;
                                            break;
                                        }
                                    }
                                    stepIdx++;
                                }

                                if (wizardAborted) continue;
                                // Capture before final Tab+Enter
                                await TmuxCaptureAndDebugAsync(paneTarget, "wizard_submit_before", debugDir, answerLoopCts.Token);
                                await TmuxSendKeysToTabAsync(paneTarget, answerLoopCts.Token);
                                await Task.Delay(300, answerLoopCts.Token);
                                await TmuxSendKeysAsync(paneTarget, "", answerLoopCts.Token);
                            }
                            else
                            {
                                // Single-answer mode: capture pane and check if it shows a numbered option list.
                                // AskUserQuestion with questions.length===1 sends just the label text as the answer
                                // (no \n\n), but the UI is the same arrow-key selection as the multi-step wizard —
                                // NOT free-text typing. Try option matching first; fall back to typing only if no match.
                                var plainPane = await WaitForOptionsAsync(paneTarget, "plain_before", debugDir, answerLoopCts.Token);
                                var plainHasOptions = System.Text.RegularExpressions.Regex.IsMatch(
                                    plainPane, @"^\s*\d+\.\s+\S",
                                    System.Text.RegularExpressions.RegexOptions.Multiline);

                                if (!plainHasOptions)
                                {
                                    // No question UI visible — not the right moment to inject.
                                    // Don't mark as injected so the next poll cycle retries.
                                    _console.MarkupLine($"[yellow]Single-question: no question UI visible, deferring injection[/]");
                                    continue;
                                }

                                lastInjectedToolUseId = toolUseId;
                                var plainOptionNumber = MatchOptionNumber(plainPane, answer);
                                if (plainOptionNumber > 0)
                                {
                                    var currentOption = SelectedOptionNumber(plainPane);
                                    var downs = plainOptionNumber - currentOption;
                                    _console.MarkupLine($"[dim]  → single-question: matched '{answer.EscapeMarkup()}' to option {plainOptionNumber} ({downs} Down(s))[/]");
                                    for (var d = 0; d < downs; d++)
                                    {
                                        await TmuxSendKeyRawAsync(paneTarget, "Down", answerLoopCts.Token);
                                        await Task.Delay(100, answerLoopCts.Token);
                                    }
                                    // Enter selects and auto-submits (no Tab needed for single-question)
                                    await TmuxSendKeyRawAsync(paneTarget, "Enter", answerLoopCts.Token);
                                }
                                else
                                {
                                    // Truly free-text: question UI is showing but has no numbered options.
                                    _console.MarkupLine($"[dim]  → single-question: no option match, typing as free text[/]");
                                    await TmuxSendKeysAsync(paneTarget, answer, answerLoopCts.Token);
                                }
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            _console.MarkupLine($"[yellow]Answer loop error: {ex.Message.EscapeMarkup()}[/]");
                        }
                    }
                    _console.MarkupLine("[cyan]Answer injection loop stopped.[/]");
                }, answerLoopCts.Token);
            }

            // 13. Wait for job to complete with activity-based timeout
            var idleTimeoutMs = (job.AgentDef?.IdleTimeoutMinutes ?? 2) * 60 * 1000;
            var maxTimeout = TimeSpan.FromMinutes(job.AgentDef?.MaxTimeoutMinutes ?? 60);
            _console.MarkupLine($"[cyan]Waiting up to {maxTimeout.TotalMinutes} minutes (idle threshold: {idleTimeoutMs / 60000} min)...[/]");

            using var waitSpan = _activitySource.StartActivity("runner.job.wait_completion");
            waitSpan?.SetTag("idle_timeout_minutes", job.AgentDef?.IdleTimeoutMinutes ?? 2);
            waitSpan?.SetTag("max_timeout_minutes", job.AgentDef?.MaxTimeoutMinutes ?? 60);
            waitSpan?.SetTag("stream.id", streamIdValue ?? "");

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

                // Poll activity endpoint to detect idle agent
                if (streamIdValue != null)
                {
                    try
                    {
                        var scheme = serverUri.Scheme;
                        var activityUrl = $"{scheme}://{serverUri.Host}:{serverUri.Port}/api/lives/activity?streamId={streamIdValue}&idleThresholdMs={idleTimeoutMs}";
                        var actResp = await client.GetAsync(activityUrl, ct);
                        if (actResp.IsSuccessStatusCode)
                        {
                            var actData = JsonSerializer.Deserialize<JsonElement>(
                                await actResp.Content.ReadAsStringAsync(ct), JsonOptions);
                            if (actData.TryGetProperty("isActive", out var isActive) && !isActive.GetBoolean())
                            {
                                var idleSince = actData.TryGetProperty("idleSinceMs", out var idleMs) ? idleMs.GetInt64() / 1000 : 0;
                                _console.MarkupLine($"[yellow]Agent idle for {idleSince}s, completing job.[/]");
                                completionReason = "idle_timeout";
                                break;
                            }
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
            _console.MarkupLine($"[cyan]Marking job as completed (conclusion: {jobConclusion}, reason: {completionReason})...[/]");
            var patchCompleted = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{job.Id}");
            patchCompleted.Content = JsonContent.Create(new
            {
                status = "completed",
                conclusion = jobConclusion,
                // completionReason carries the detailed reason (idle_timeout, timeout, success, etc.)
                // so the server can apply the correct task lifecycle logic without parsing conclusion.
                completionReason,
                streamId = streamIdValue,
                logs = $"Job completed after {(DateTime.UtcNow - startTime).TotalMinutes:F1} minutes in {jobWorkTree} (reason: {completionReason})"
            });
            patchCompleted.Headers.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            await client.SendAsync(patchCompleted, ct);

            _console.MarkupLine($"[green]Job {job.Id} completed.[/]");

            // Enrich parent job span with final outcome
            jobSpan?.SetTag("agentics.conclusion", jobConclusion);
            jobSpan?.SetTag("agentics.completion_reason", completionReason);
            jobSpan?.SetTag("agentics.stream_id", streamIdValue ?? "");
            jobSpan?.SetTag("agentics.elapsed_minutes", Math.Round(elapsedMinutes, 2));
            if (jobConclusion != "success")
                jobSpan?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, completionReason);

            // Remove from active tracking (normal completion)
            lock (_activeJobsLock) { _activeJobs.RemoveAll(j => j.JobId == job.Id); }
        }
        finally
        {
            // Stop the answer injection loop
            await answerLoopCts.CancelAsync();
            if (answerLoopTask != null)
            {
                try { await answerLoopTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            answerLoopCts.Dispose();

            if (jobWorkTree != null)
            {
                await CleanupWorkTreeAsync(jobWorkTree, settings.Verbose, ct);
            }
        }
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
            // Strip leading selection cursor and whitespace
            var trimmed = line.TrimStart('❯', ' ', '\t');
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
            var trimmed = line.TrimStart('❯', ' ', '\t');
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
        if (!useWorktree)
        {
            var jobDir = Path.Combine(workDir, "jobs", job.Id);
            Directory.CreateDirectory(jobDir);

            if (!string.IsNullOrEmpty(repository))
            {
                _console.MarkupLine($"[cyan]Cloning {repository} → {jobDir}...[/]");
                var cloneResult = await RunGitAsync(
                    $"clone --depth=1 --branch {branch} {repository} {jobDir}",
                    null, verbose, ct, gitEnv);
                if (cloneResult != 0)
                    _console.MarkupLine($"[yellow]git clone failed (exit {cloneResult}) — using empty job directory[/]");
            }
            else
            {
                _console.MarkupLine($"[cyan]No repository URL — using empty job directory {jobDir}[/]");
            }

            return jobDir;
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

                // 4. Clean up worktree
                if (job.WorkTreePath != null)
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
    /// Replaces the ephemeral claude-code-config-${devcontainerId} volume with a stable
    /// named volume so credentials persist across container respawns for the same project.
    /// </summary>
    private static Dictionary<string, string>? PatchDevcontainerVolumes(
        Dictionary<string, string>? files, string owner, string project)
    {
        if (files == null) return null;
        static string Sanitize(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "-");
        var stableVolume = $"pks-claude-{Sanitize(owner)}-{Sanitize(project)}";
        var patched = new Dictionary<string, string>(files);
        const string key = ".devcontainer/devcontainer.json";
        if (patched.TryGetValue(key, out var content))
            patched[key] = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"source=claude-code-config-\$\{devcontainerId\}",
                $"source={stableVolume}");
        return patched;
    }

    /// <summary>
    /// Computes a short fingerprint for the devcontainer config.
    /// Same owner/project + same devcontainer files → same fingerprint → container can be reused.
    /// </summary>
    private static string ComputeDevcontainerFingerprint(
        string owner, string project, Dictionary<string, string>? devcontainerFiles)
    {
        var content = devcontainerFiles == null ? string.Empty :
            string.Concat(devcontainerFiles
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
        var input = System.Text.Encoding.UTF8.GetBytes($"{owner}/{project}/{content}");
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
        if (stream == null) return null;

        using var ms = new System.IO.MemoryStream();
        await stream.CopyToAsync(ms);
        _console.MarkupLine($"[dim]Injecting vibecast binary ({ms.Length / 1024} KB) into container via stdin pipe...[/]");

        // Pipe the raw binary via `docker exec -i ... cat > dest` — avoids docker cp
        // Windows path issues and base64 command-length limits.
        var dest = "/tmp/vibecast-embedded";

        // Remove any previous binary so we always inject the latest local build.
        await _spawnerService.ExecInContainerAsync(containerId, $"rm -f {dest}", timeoutSeconds: 5);

        var psi  = new ProcessStartInfo("docker")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
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

        _console.MarkupLine("[dim]Vibecast binary injected.[/]");

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
            var destDir  = System.IO.Path.GetDirectoryName(destPath)!.Replace('\\', '/');

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
    {
        try
        {
            var psi = new ProcessStartInfo("docker",
                $"ps --filter label={labelFilter} --filter status=running --format {{{{.ID}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
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
                // Already a full URL — embed token if available
                if (!string.IsNullOrEmpty(gitToken))
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

        // Use the repo name as ProjectName so the clone lands at /workspace/{repo}
        var repoName = job.AgentDef?.Repository?.Split('/').LastOrDefault()
            ?? job.ProjectName
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
        public List<string> Labels { get; set; } = new();
        public string? TaskId { get; set; }
        public string? StageId { get; set; }
        public int? IdleTimeoutMinutes { get; set; }
        public int? MaxTimeoutMinutes { get; set; }
        public string? StageGitUrl { get; set; }
        public string? StageGitToken { get; set; }
        public Dictionary<string, string>? DevcontainerFiles { get; set; }
        public List<PluginRef>? Plugins { get; set; }
        public List<AgentRef>? Agents { get; set; }
        /// <summary>W3C traceparent header from the server-side span that dispatched this job.</summary>
        public string? Traceparent { get; set; }
        /// <summary>claudeSessionId from a prior timed-out run — when set, runner injects VIBECAST_RESUME_SESSION_ID so vibecast can pass --resume to Claude.</summary>
        public string? ResumeSessionId { get; set; }
        /// <summary>When true, vibecast blocks Claude from stopping until the working tree is clean (no uncommitted changes).</summary>
        public bool AutoGit { get; set; }
        /// <summary>When true, runner creates a task-scoped branch (task-{taskId}) before launching Claude.</summary>
        public bool InitBranch { get; set; }
        /// <summary>Commit message hint shown to Claude when AutoGit blocks session end due to uncommitted changes.</summary>
        public string? CommitMessageTemplate { get; set; }
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
