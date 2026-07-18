using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>Outcome of one handoff attempt (docs/remote-runner-targets-plan.md Phase 4, work items 4-5).</summary>
public sealed record SshHandoffResult(
    bool Success,
    string RunnerName,
    string TmuxSessionName,
    TimeSpan Elapsed,
    string? FailureReason,
    string? RemoteTmuxOutput);

/// <summary>
/// Hands off a project's runner to run on a registered SSH target instead of locally: mints the
/// registration on the laptop (which holds the GitHub identity), scp's it to the remote
/// <c>~/.pks-cli/agentics-runners.json</c>, launches <c>agentics runner start</c> there inside a
/// detached tmux session, then polls the server until the new runner reports online. Uses tmux, not
/// systemd/systemctl -- there is no such pattern anywhere in this codebase, and tmux is already a
/// hard vibecast dependency (see docs/remote-runner-targets-plan.md Phase 4, obstacle (c)).
/// </summary>
public interface IAgenticsRunnerSshHandoffService
{
    /// <summary>Probe docker/tmux/dotnet/dnx readiness on the target before committing to a handoff.</summary>
    Task<SshProbeResult> ProbeAsync(SshTarget target, CancellationToken ct = default);

    /// <summary>Deterministic tmux session name for a given owner/project, shared by the handoff and
    /// by the status/logs/stop commands so they always agree on which session to act on.</summary>
    string BuildTmuxSessionName(string owner, string project);

    /// <summary>
    /// Registers <paramref name="runnerName"/> on the server, ships that registration to the target,
    /// and launches it in a detached tmux session. Hard-refuses (returns <c>Success:false</c>,
    /// never throws) if <paramref name="runnerName"/> collides with the local machine's own runner
    /// name or an existing runner already registered for this project -- see
    /// <see cref="SshRunnerHandoffNaming"/>. Polls <c>GET .../runners</c> for up to two minutes for
    /// the new runner to report <c>online</c>; on timeout, captures the remote tmux pane so the
    /// caller can show the operator what actually happened.
    /// </summary>
    Task<SshHandoffResult> HandoffAsync(
        SshTarget target,
        string owner,
        string project,
        string server,
        string runnerName,
        Action<string>? onProgress = null,
        CancellationToken ct = default);

    /// <summary>Raw <c>tmux capture-pane -p</c> output for the session, or <c>null</c> if the SSH
    /// call itself failed (host unreachable) -- as opposed to an empty string, which means the
    /// session simply has no output yet.</summary>
    Task<string?> CapturePaneAsync(SshTarget target, string tmuxSessionName, CancellationToken ct = default);

    /// <summary>Kills the remote tmux session. Returns false if the SSH call failed or the session
    /// didn't exist.</summary>
    Task<bool> StopAsync(SshTarget target, string tmuxSessionName, CancellationToken ct = default);

    /// <summary>
    /// Checks whether the default project-scoped <c>pks-claude-*</c> Docker volume (see
    /// <see cref="ClaudeCredentialVolumes"/>) already exists on <paramref name="target"/>. Checks
    /// the "project" scope specifically -- the default a job uses when its
    /// <c>AgentDef.ClaudeCredentialsScope</c> is unset, and the only scope knowable ahead of any
    /// specific job dispatch (task-scoped volumes are named per <c>taskId</c>, which doesn't exist
    /// yet at handoff/status time). Returns <c>true</c>/<c>false</c> when the probe ran, or
    /// <c>null</c> if the target couldn't be reached at all (distinct from "definitely missing" --
    /// see docs/remote-runner-targets-plan.md Phase 5, work item 1).
    /// </summary>
    Task<bool?> DetectClaudeCredentialVolumeAsync(SshTarget target, string owner, string project, CancellationToken ct = default);

    /// <summary>
    /// Opt-in credential forwarding (docs/remote-runner-targets-plan.md Phase 5, work item 3):
    /// merges one key into the remote's <c>~/.pks-cli/settings.json</c> flat config dict (creating
    /// it if absent, preserving every other key already there), then re-restricts it to 0600.
    /// Callers pass the exact storage key the remote's own service reads
    /// (<c>github.auth.token</c> for <see cref="IGitHubAuthenticationService"/>,
    /// <c>foundry.auth.credentials</c> for <c>IAzureFoundryAuthService</c>) so forwarding actually
    /// un-degrades what the remote runner can advertise, rather than landing a decorative copy
    /// nothing reads. Mirrors the read-merge-write-scp-chmod pattern already used by
    /// <c>ShipRegistrationAsync</c> for <c>agentics-runners.json</c>. Returns <c>null</c> on
    /// success, or an error string describing what failed (never throws for ordinary SSH/scp
    /// failures).
    /// </summary>
    Task<string?> ForwardConfigValueAsync(SshTarget target, string key, string value, CancellationToken ct = default);
}

public sealed class AgenticsRunnerSshHandoffService : IAgenticsRunnerSshHandoffService
{
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions RegistrationJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ResponseJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ISshCommandRunner _sshRunner;
    private readonly ISshKeyStore _keyStore;
    private readonly IHttpClientFactory _httpClientFactory;

    public AgenticsRunnerSshHandoffService(
        ISshCommandRunner sshRunner, ISshKeyStore keyStore, IHttpClientFactory httpClientFactory)
    {
        _sshRunner = sshRunner ?? throw new ArgumentNullException(nameof(sshRunner));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    public string BuildTmuxSessionName(string owner, string project) =>
        $"pks-agentics-{Sanitize(owner)}-{Sanitize(project)}";

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Write secret content (tokens, credentials, registrations) to a staging file with
    /// owner-only permissions applied BEFORE any bytes hit the disk.
    /// <para>
    /// These files live in the shared temp directory, so a plain
    /// <c>File.WriteAllTextAsync</c> followed by a chmod leaves a window in which the
    /// secret is world-readable. <c>FileMode.CreateNew</c> + <c>FileShare.None</c> also
    /// makes the create fail rather than clobber if the path somehow already exists.
    /// Mirrors <see cref="SshKeyStore"/>'s MaterializeAsync, which is the reference
    /// implementation for this pattern in this repo.
    /// </para>
    /// </summary>
    private static async Task WriteSecretFileAsync(string path, string content, CancellationToken ct)
    {
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            SecurityFiles.Restrict(path);
            await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content), ct);
        }
        SecurityFiles.Restrict(path);
    }

    public async Task<SshProbeResult> ProbeAsync(SshTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (hostConfig, materialized) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            return await SshRunnerProbe.ProbeAsync(_sshRunner, hostConfig, ct);
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    public async Task<SshHandoffResult> HandoffAsync(
        SshTarget target,
        string owner,
        string project,
        string server,
        string runnerName,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(runnerName);

        var sessionName = BuildTmuxSessionName(owner, project);
        var started = DateTime.UtcNow;

        // 1. Hard name-collision refusal -- BEFORE anything touches the network or the target.
        //    registerRunner upserts by name and rotates the token in place, so registering under a
        //    name already in use would silently invalidate that runner's own credentials.
        var localHostName = System.Net.Dns.GetHostName();
        List<string> existingNames;
        try
        {
            existingNames = await FetchServerRunnerNamesAsync(server, owner, project, ct);
        }
        catch (Exception ex)
        {
            return Fail(runnerName, sessionName, started, $"Could not list existing runners for {owner}/{project}: {ex.Message}");
        }

        if (SshRunnerHandoffNaming.IsCollision(runnerName, localHostName, existingNames))
        {
            return Fail(runnerName, sessionName, started,
                $"Runner name '{runnerName}' collides with an existing runner (this machine's own name, or one already registered for {owner}/{project}). Choose a different name.");
        }

        onProgress?.Invoke($"Registering '{runnerName}' for {owner}/{project}...");

        AgenticsRunnerRegistration registration;
        try
        {
            registration = await RegisterOnServerAsync(server, owner, project, runnerName, ct);
        }
        catch (Exception ex)
        {
            return Fail(runnerName, sessionName, started, $"Registration failed: {ex.Message}");
        }

        registration.Profile = new RunnerProfile
        {
            SshTargetLabel = target.Label ?? target.Host,
            ConfiguredAt = DateTime.UtcNow,
        };

        var (hostConfig, materializedKey) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            onProgress?.Invoke($"Shipping registration to {target.Username}@{target.Host}...");

            var shipError = await ShipRegistrationAsync(hostConfig, owner, project, registration, ct);
            if (shipError != null)
                return Fail(runnerName, sessionName, started, shipError);

            onProgress?.Invoke($"Launching tmux session '{sessionName}' on {target.Host}...");

            var launchCommand =
                $"tmux new-session -d -s {sessionName} 'dnx pks-cli -- agentics runner start --project {owner}/{project} --server {server}'";
            var launchResult = await _sshRunner.RunAsync(hostConfig, launchCommand, ct);
            if (!launchResult.Success)
            {
                return Fail(runnerName, sessionName, started,
                    $"Failed to launch tmux session on {target.Host}: {launchResult.StdErr.Trim()}");
            }

            onProgress?.Invoke("Waiting for the runner to report online...");

            var deadline = DateTime.UtcNow + ReadinessTimeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (await IsOnlineAsync(server, owner, project, runnerName, ct))
                {
                    return new SshHandoffResult(true, runnerName, sessionName, DateTime.UtcNow - started, null, null);
                }

                await Task.Delay(PollInterval, ct);
            }

            var pane = await CapturePaneAsync(target, sessionName, ct);
            return Fail(runnerName, sessionName, started,
                $"Timed out after {ReadinessTimeout.TotalSeconds:0}s waiting for '{runnerName}' to report online.", pane);
        }
        finally
        {
            materializedKey?.Dispose();
        }
    }

    public async Task<string?> CapturePaneAsync(SshTarget target, string tmuxSessionName, CancellationToken ct = default)
    {
        var (hostConfig, materialized) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            var result = await _sshRunner.RunAsync(hostConfig, $"tmux capture-pane -p -t {tmuxSessionName}", ct);
            return result.Success ? result.StdOut : null;
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    public async Task<bool> StopAsync(SshTarget target, string tmuxSessionName, CancellationToken ct = default)
    {
        var (hostConfig, materialized) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            var result = await _sshRunner.RunAsync(hostConfig, $"tmux kill-session -t {tmuxSessionName}", ct);
            return result.Success;
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    public async Task<bool?> DetectClaudeCredentialVolumeAsync(SshTarget target, string owner, string project, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(project);

        var volumeName = ClaudeCredentialVolumes.ResolveVolumeName(owner, project, taskId: null, scope: "project");
        var (hostConfig, materialized) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            var result = await _sshRunner.RunAsync(hostConfig, ClaudeCredentialVolumes.BuildDetectCommand(volumeName), ct);
            if (!result.Success) return null;
            return ClaudeCredentialVolumes.ParseDetectOutput(result.StdOut);
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    public async Task<string?> ForwardConfigValueAsync(SshTarget target, string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var (hostConfig, materialized) = await target.ToRemoteHostConfigAsync(_keyStore, ct);
        try
        {
            var mkdirResult = await _sshRunner.RunAsync(hostConfig, "mkdir -p ~/.pks-cli", ct);
            if (!mkdirResult.Success)
                return $"Could not create ~/.pks-cli on {hostConfig.Host}: {mkdirResult.StdErr.Trim()}";

            var catResult = await _sshRunner.RunAsync(hostConfig, "cat ~/.pks-cli/settings.json 2>/dev/null || true", ct);

            var settings = new Dictionary<string, string>();
            if (catResult.Success && !string.IsNullOrWhiteSpace(catResult.StdOut))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<Dictionary<string, string>>(catResult.StdOut)
                        ?? new Dictionary<string, string>();
                }
                catch (JsonException)
                {
                    // Remote file is corrupt/unparseable -- proceed with a fresh dict rather than
                    // failing the forward (same handling as ShipRegistrationAsync).
                    settings = new Dictionary<string, string>();
                }
            }

            settings[key] = value;
            var mergedJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            var tempPath = Path.Combine(Path.GetTempPath(), $"pks-agentics-credential-{Guid.NewGuid():n}.json");
            try
            {
                await WriteSecretFileAsync(tempPath, mergedJson, ct);

                var scpResult = await _sshRunner.ScpAsync(hostConfig, tempPath, "~/.pks-cli/settings.json", recursive: false, ct);
                if (!scpResult.Success)
                    return $"Failed to copy credentials to {hostConfig.Host}: {scpResult.StdErr.Trim()}";

                var chmodResult = await _sshRunner.RunAsync(hostConfig, "chmod 600 ~/.pks-cli/settings.json", ct);
                if (!chmodResult.Success)
                    return $"Copied credentials but failed to restrict their permissions on {hostConfig.Host}: {chmodResult.StdErr.Trim()}";

                return null;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────

    private static SshHandoffResult Fail(string runnerName, string sessionName, DateTime started, string reason, string? remoteTmuxOutput = null) =>
        new(false, runnerName, sessionName, DateTime.UtcNow - started, reason, remoteTmuxOutput);

    private async Task<List<string>> FetchServerRunnerNamesAsync(string server, string owner, string project, CancellationToken ct)
    {
        var entries = await FetchServerRunnersAsync(server, owner, project, ct);
        return entries.Where(e => e.Name != null).Select(e => e.Name!).ToList();
    }

    private async Task<bool> IsOnlineAsync(string server, string owner, string project, string runnerName, CancellationToken ct)
    {
        var entries = await FetchServerRunnersAsync(server, owner, project, ct);
        return entries.Any(e =>
            string.Equals(e.Name, runnerName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(e.Status, "online", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<List<RunnerListEntry>> FetchServerRunnersAsync(string server, string owner, string project, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"{server}/api/owners/{owner}/projects/{project}/runners", ct);
        response.EnsureSuccessStatusCode();
        var entries = await response.Content.ReadFromJsonAsync<List<RunnerListEntry>>(ResponseJsonOptions, ct);
        return entries ?? new List<RunnerListEntry>();
    }

    private async Task<AgenticsRunnerRegistration> RegisterOnServerAsync(
        string server, string owner, string project, string runnerName, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        var body = new { name = runnerName, labels = new[] { "self-hosted", "ssh-handoff" } };
        var response = await client.PostAsJsonAsync($"{server}/api/owners/{owner}/projects/{project}/runners", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"registration failed ({(int)response.StatusCode}): {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var resp = JsonSerializer.Deserialize<RegisterRunnerResponse>(json, ResponseJsonOptions)
            ?? throw new InvalidOperationException("failed to parse registration response");

        return new AgenticsRunnerRegistration
        {
            Id = resp.Id ?? Guid.NewGuid().ToString(),
            Name = resp.Name ?? runnerName,
            Token = resp.Token ?? "",
            Owner = owner,
            Project = project,
            Server = server,
            RegisteredAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Upserts <paramref name="registration"/> into the remote <c>~/.pks-cli/agentics-runners.json</c>
    /// (creating the directory/file if absent, preserving any other registrations already there) so
    /// that when <c>agentics runner start --project owner/project</c> runs on the target, its own
    /// <c>ResolveOrRegisterAsync</c> finds this registration and reuses it instead of auto-registering
    /// a second, colliding one under the target's own hostname.
    /// </summary>
    private async Task<string?> ShipRegistrationAsync(
        RemoteHostConfig hostConfig, string owner, string project, AgenticsRunnerRegistration registration, CancellationToken ct)
    {
        var mkdirResult = await _sshRunner.RunAsync(hostConfig, "mkdir -p ~/.pks-cli", ct);
        if (!mkdirResult.Success)
            return $"Could not create ~/.pks-cli on {hostConfig.Host}: {mkdirResult.StdErr.Trim()}";

        var catResult = await _sshRunner.RunAsync(hostConfig, "cat ~/.pks-cli/agentics-runners.json 2>/dev/null || true", ct);

        var config = new AgenticsRunnerConfiguration();
        if (catResult.Success && !string.IsNullOrWhiteSpace(catResult.StdOut))
        {
            try
            {
                config = JsonSerializer.Deserialize<AgenticsRunnerConfiguration>(catResult.StdOut, RegistrationJsonOptions)
                    ?? new AgenticsRunnerConfiguration();
            }
            catch (JsonException)
            {
                // Remote file is corrupt/unparseable -- proceed with a fresh config rather than
                // failing the handoff. This mirrors AgenticsRunnerConfigurationService's own
                // corrupt-file handling, minus the .bak rename (nothing local to rename here).
                config = new AgenticsRunnerConfiguration();
            }
        }

        var idx = config.Registrations.FindIndex(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) config.Registrations[idx] = registration;
        else config.Registrations.Add(registration);
        config.LastModified = DateTime.UtcNow;

        var mergedJson = JsonSerializer.Serialize(config, RegistrationJsonOptions);

        var tempPath = Path.Combine(Path.GetTempPath(), $"pks-agentics-handoff-{Guid.NewGuid():n}.json");
        try
        {
            await WriteSecretFileAsync(tempPath, mergedJson, ct);

            var scpResult = await _sshRunner.ScpAsync(hostConfig, tempPath, "~/.pks-cli/agentics-runners.json", recursive: false, ct);
            if (!scpResult.Success)
                return $"Failed to copy registration to {hostConfig.Host}: {scpResult.StdErr.Trim()}";

            var chmodResult = await _sshRunner.RunAsync(hostConfig, "chmod 600 ~/.pks-cli/agentics-runners.json", ct);
            if (!chmodResult.Success)
                return $"Copied registration but failed to restrict its permissions on {hostConfig.Host}: {chmodResult.StdErr.Trim()}";

            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private sealed class RunnerListEntry
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
    }

    private sealed class RegisterRunnerResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
