using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.AgenticsProxy;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// code_step jobs: the deterministic counterpart of an agent job, like a GitHub Actions
/// <c>run:</c> step. The job gets the same devcontainer an agent job gets (same proxies, claim,
/// credentials, warm-container reuse and checkout, via <see cref="ProvisionJobContainerAsync"/>),
/// then runs ONE shell command in the checkout -- no vibecast, no Claude -- and reports the exit
/// code as the job's conclusion.
///
/// Wire contract: <c>agentDefinition.jobType == "code_step"</c>,
/// <c>agentDefinition.codeStepPayload = { command, timeoutMs? }</c>, <c>needs: ["code-step:v1"]</c>.
/// Completion is a single PATCH <c>{status:"completed", conclusion:"success"|"failure", logs}</c>.
/// </summary>
public partial class AgenticsRunnerRunCommand
{
    /// <summary>Capability string a runner advertises when it can run code_step jobs.</summary>
    internal const string CodeStepCapability = "code-step:v1";

    /// <summary>Default wall-clock budget for one code step.</summary>
    internal const int CodeStepDefaultTimeoutMs = 10 * 60 * 1000;

    /// <summary>
    /// Upper bound on the <c>logs</c> a code_step completion carries. The server keeps the full
    /// string on the job, but the task comment it writes from a bare job's logs is the FIRST 2000
    /// characters (www-site <c>job-conclusion.ts</c>, <c>LOG_EXCERPT_MAX</c>). Capping at that size
    /// means the comment shows the <c>exit</c> line and the real end of the output rather than
    /// the middle of it.
    /// </summary>
    internal const int CodeStepLogsMaxChars = 2000;

    /// <summary><c>agentDefinition.codeStepPayload</c>.</summary>
    internal sealed class CodeStepPayloadModel
    {
        public string Command { get; set; } = "";
        public int? TimeoutMs { get; set; }
    }

    /// <summary>
    /// Test seam: deserializes an <c>agentDefinition</c> JSON object exactly the way the poll loop
    /// does (same <see cref="JsonOptions"/>) and returns its job type and code-step payload.
    /// </summary>
    internal static (string? JobType, CodeStepPayloadModel? Payload) ParseCodeStepAgentDefinition(string agentDefinitionJson)
    {
        var def = JsonSerializer.Deserialize<RunnerAgentDefinition>(agentDefinitionJson, JsonOptions);
        return (def?.JobType, def?.CodeStepPayload);
    }

    /// <summary>The timeout to apply, in whole seconds: <c>timeoutMs</c> when positive, else 10 minutes.</summary>
    /// <summary>
    /// The line files a job writes into its worktree: paths are made relative and anything
    /// that climbs out of the workspace is dropped. Files under <c>.agentics/scripts/</c> are
    /// executable (0755) so a station can name one as its command; everything else is 0644.
    /// </summary>
    internal static IReadOnlyList<(string RelPath, string Content, int Mode)> LineFilesToMaterialize(
        IReadOnlyDictionary<string, string>? files)
    {
        var result = new List<(string, string, int)>();
        if (files is null) return result;
        foreach (var (relPath, content) in files)
        {
            var safeRel = relPath.Replace("\\", "/").TrimStart('/');
            if (safeRel.Length == 0 || safeRel.Split('/').Any(seg => seg == "..")) continue;
            var mode = safeRel.StartsWith(".agentics/scripts/", StringComparison.Ordinal) ? 493 /* 0o755 */ : 420 /* 0o644 */;
            result.Add((safeRel, content, mode));
        }
        return result;
    }

    internal static int CodeStepTimeoutSeconds(int? timeoutMs)
    {
        var ms = timeoutMs is > 0 ? timeoutMs.Value : CodeStepDefaultTimeoutMs;
        return Math.Max(1, (int)Math.Ceiling(ms / 1000.0));
    }

    /// <summary>
    /// Maps a code step's outcome to the completion PATCH's conclusion and logs. Exit 0 is
    /// success; any other exit code, a timeout, or an error before the command could run is
    /// failure. Logs start with one <c>exit &lt;code&gt;</c> line, then <c>timed out after Ns</c> /
    /// the error when there is one, then as much of the END of the combined output as fits in
    /// <see cref="CodeStepLogsMaxChars"/>.
    /// </summary>
    internal static (string Conclusion, string Logs) FormatCodeStepCompletion(
        int exitCode, string? output, bool timedOut = false, int timeoutSeconds = 0, string? error = null)
    {
        var success = exitCode == 0 && !timedOut && string.IsNullOrEmpty(error);
        var header = new StringBuilder();
        header.Append("exit ").Append(exitCode).Append('\n');
        if (timedOut)
            header.Append("timed out after ").Append(timeoutSeconds).Append("s\n");
        if (!string.IsNullOrEmpty(error))
            header.Append(error.Trim()).Append('\n');

        var head = header.ToString();
        if (head.Length > CodeStepLogsMaxChars)
            head = head[..CodeStepLogsMaxChars];
        var body = (output ?? "").TrimEnd();
        var room = CodeStepLogsMaxChars - head.Length;
        if (body.Length > room)
            body = room > 0 ? body[^room..] : "";
        return (success ? "success" : "failure", (head + body).TrimEnd('\n'));
    }

    /// <summary>
    /// Whether an exit code from the <c>timeout</c>-wrapped exec means the limit was hit: 124
    /// (timeout(1) sent TERM) or 137 (it followed up with KILL), and only when the step actually
    /// ran that long -- a command OOM-killed after two seconds also exits 137.
    /// </summary>
    internal static bool IsCodeStepTimeout(int exitCode, TimeSpan elapsed, int timeoutSeconds) =>
        exitCode is 124 or 137 && elapsed >= TimeSpan.FromSeconds(Math.Max(0, timeoutSeconds - 1));

    /// <summary>
    /// The bash script a code step runs: the job's AGENTICS_* environment (the same lines start.sh
    /// gets), cd into the checkout, then the command in a login shell. Written to a file rather
    /// than passed on the exec command line, so the tokens never appear in a logged argv.
    /// </summary>
    internal static string BuildCodeStepScript(IEnumerable<string> exportLines, string workspaceFolder, string command, string? pidFile = null)
    {
        var sb = new StringBuilder();
        sb.Append("#!/bin/bash\n");
        // `exec` below keeps this PID, so the runner can kill the command by it if timeout(1)
        // is missing from the image.
        if (pidFile != null)
            sb.Append("echo $$ > ").Append(ShellSingleQuote(pidFile)).Append('\n');
        // Same user-local bin dirs start.sh puts first, so tools like agent-share resolve.
        sb.Append("export PATH=\"$HOME/.local/bin:/home/node/.local/bin:/usr/local/bin:/usr/bin:/bin:$PATH\"\n");
        sb.Append("export LANG=${LANG:-C.UTF-8}\n");
        sb.Append("export LC_ALL=${LC_ALL:-C.UTF-8}\n");
        foreach (var line in exportLines)
            sb.Append(line).Append('\n');
        sb.Append("cd ").Append(ShellSingleQuote(workspaceFolder)).Append(" || exit 1\n");
        sb.Append("exec bash -lc ").Append(ShellSingleQuote(command)).Append('\n');
        return sb.ToString();
    }

    private async Task ExecuteCodeStepJobAsync(
        AgenticsRunnerRegistration registration,
        RunnerJob job,
        GitCredentialServer credentialServer,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);
        var baseUrl = $"{registration.ApiBase}/api/owners/{registration.Owner}/projects/{registration.Project}";

        await using var container = new JobContainerContext();
        bool claimed;
        try
        {
            claimed = await ProvisionJobContainerAsync(container, registration, job, credentialServer, client, baseUrl, ct);
        }
        catch (Exception ex) when (container.RunId is null)
        {
            // Failed before the claim -- the job is still queued, nothing to report.
            _console.MarkupLine($"[red]code_step {job.Id.EscapeMarkup()}: could not start: {ex.Message.EscapeMarkup()}[/]");
            return;
        }
        catch (Exception ex)
        {
            await ConcludeCodeStepAsync(client, baseUrl, container.RunId!, registration, job.Id,
                FormatCodeStepCompletion(-1, null, error: $"container setup failed: {ex.Message}"));
            return;
        }
        if (!claimed) return;
        var runId = container.RunId!;

        if (container.FailureMessage is { } spawnFailure)
        {
            await ConcludeCodeStepAsync(client, baseUrl, runId, registration, job.Id,
                FormatCodeStepCompletion(-1, null, error: $"devcontainer spawn failed: {spawnFailure}"));
            return;
        }

        // Like mcp_task: a poll may carry only `needs`, with agentDefinition on the claim's job.
        var definition = job.AgentDef;
        if (definition?.CodeStepPayload is null
            && container.ClaimData is { ValueKind: JsonValueKind.Object } claim
            && claim.TryGetProperty("job", out var claimedJob) && claimedJob.ValueKind == JsonValueKind.Object
            && claimedJob.TryGetProperty("agentDefinition", out var claimedDef) && claimedDef.ValueKind == JsonValueKind.Object)
        {
            definition = claimedDef.Deserialize<RunnerAgentDefinition>(JsonOptions) ?? definition;
        }
        var payload = definition?.CodeStepPayload;
        if (string.IsNullOrWhiteSpace(payload?.Command))
        {
            await ConcludeCodeStepAsync(client, baseUrl, runId, registration, job.Id,
                FormatCodeStepCompletion(-1, null, error: "codeStepPayload.command is missing"));
            return;
        }

        var containerId = container.ContainerId!;
        var timeoutSeconds = CodeStepTimeoutSeconds(payload.TimeoutMs);
        string? scriptDir = null;
        (string Conclusion, string Logs) completion;
        try
        {
            var workspaceFolder = $"/workspaces/{container.SpawnOptions!.ProjectName}";
            var agentUser = await ResolveAgentUserAsync(containerId, workspaceFolder, container.AgentUser);

            var home = await _spawnerService.ExecInContainerAsync(containerId,
                "printf %s \"$HOME\"", timeoutSeconds: 10, user: agentUser);
            if (!home.Success || string.IsNullOrWhiteSpace(home.Output))
                throw new InvalidOperationException($"could not resolve $HOME in the container (exit {home.ExitCode})");
            // The line's own files (.agentics/specs, .agentics/scripts) are part of the line,
            // not of the project repo the job checked out — without them a station whose
            // command is `.agentics/scripts/watch.sh` finds nothing to run.
            var lineFiles = LineFilesToMaterialize(definition?.AgenticsSpecFiles);
            foreach (var (relPath, content, mode) in lineFiles)
            {
                await _spawnerService.CopyFileToContainerAsync(containerId, $"{workspaceFolder}/{relPath}",
                    Encoding.UTF8.GetBytes(content), mode: mode, ct);
            }
            if (lineFiles.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(agentUser))
                {
                    await _spawnerService.ExecInContainerAsync(containerId,
                        $"chown -R \"$(id -u {agentUser}):$(id -g {agentUser})\" {ShellSingleQuote($"{workspaceFolder}/.agentics")}",
                        timeoutSeconds: 30, user: "root");
                }
                _console.MarkupLine($"  [green]✓[/] materialized {lineFiles.Count} line file(s) → {workspaceFolder.EscapeMarkup()}/.agentics/");
            }

            scriptDir = $"{home.Output.Trim()}/.code-step-jobs/{job.Id}";
            var scriptPath = $"{scriptDir}/run.sh";
            var pidFile = $"{scriptDir}/pid";

            var (serverUri, _, _, agenticServerForContainer) = ResolveContainerServer(registration);
            var exportLines = BuildJobAgenticsExportLines(registration, job.Id, container.JobToken,
                agenticServerForContainer, job.AgentDef?.OperatorConfig?.ServerUrl, serverUri);
            await _spawnerService.CopyFileToContainerAsync(containerId, scriptPath,
                Encoding.UTF8.GetBytes(BuildCodeStepScript(exportLines, workspaceFolder, payload.Command, pidFile)),
                mode: 420 /* 0o644 */, ct);
            // The archive endpoint lands the file owned by root; hand it to the user that runs it
            // and close it to everyone else, since it carries the job's tokens. With no known user
            // it stays 0644 so the image's default user can still read it.
            if (!string.IsNullOrWhiteSpace(agentUser))
            {
                await _spawnerService.ExecInContainerAsync(containerId,
                    $"chown -R \"$(id -u {agentUser}):$(id -g {agentUser})\" {ShellSingleQuote(scriptDir)} && chmod 600 {ShellSingleQuote(scriptPath)}",
                    timeoutSeconds: 30, user: "root");
            }

            _console.MarkupLine($"[cyan]code_step {job.Id.EscapeMarkup()}: running in {workspaceFolder.EscapeMarkup()} (timeout {timeoutSeconds}s)[/]");
            // `timeout` kills the command inside the container; ExecInContainerAsync's own timeout
            // only stops reading, so it is set a little later as a backstop. 2>&1 keeps stdout and
            // stderr in one ordered stream.
            var quotedScript = ShellSingleQuote(scriptPath);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var exec = await _spawnerService.ExecInContainerAsync(containerId,
                $"if command -v timeout >/dev/null 2>&1; then timeout -k 10 {timeoutSeconds} bash {quotedScript}; else bash {quotedScript}; fi 2>&1",
                workingDir: workspaceFolder,
                timeoutSeconds: timeoutSeconds + 30,
                user: agentUser);
            stopwatch.Stop();

            var output = exec.Output + (string.IsNullOrEmpty(exec.Error) ? "" : "\n" + exec.Error);
            var hostTimedOut = exec.ExitCode == -1 && exec.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase);
            if (hostTimedOut)
            {
                // No `timeout` in the image, or it did not get the process down: kill it, and its
                // children, by the PID the script recorded before exec'ing the command.
                var quotedPid = ShellSingleQuote(pidFile);
                await _spawnerService.ExecInContainerAsync(containerId,
                    $"p=$(cat {quotedPid} 2>/dev/null) && [ -n \"$p\" ] && {{ pkill -KILL -P \"$p\" 2>/dev/null; kill -KILL \"$p\" 2>/dev/null; }}; true",
                    timeoutSeconds: 10, user: "root");
            }
            var timedOut = hostTimedOut || IsCodeStepTimeout(exec.ExitCode, stopwatch.Elapsed, timeoutSeconds);
            completion = timedOut
                ? FormatCodeStepCompletion(exec.ExitCode, exec.Output, timedOut: true, timeoutSeconds: timeoutSeconds)
                : exec.ExitCode == -1 && !string.IsNullOrEmpty(exec.Error)
                    ? FormatCodeStepCompletion(-1, exec.Output, error: exec.Error)
                    : FormatCodeStepCompletion(exec.ExitCode, output);
            _console.MarkupLine(completion.Conclusion == "success"
                ? $"[green]code_step {job.Id.EscapeMarkup()}: exit 0[/]"
                : $"[yellow]code_step {job.Id.EscapeMarkup()}: {completion.Logs.Split('\n')[0].EscapeMarkup()}{(timedOut ? $" (timed out after {timeoutSeconds}s)" : "")}[/]");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            completion = FormatCodeStepCompletion(-1, null, error: "runner shut down while the step was running");
        }
        catch (Exception ex)
        {
            completion = FormatCodeStepCompletion(-1, null, error: ex.Message);
        }
        finally
        {
            if (scriptDir != null)
            {
                try
                {
                    await _spawnerService.ExecInContainerAsync(containerId,
                        $"rm -rf {ShellSingleQuote(scriptDir)}", timeoutSeconds: 10, user: "root");
                }
                catch { /* best-effort: the script holds this job's tokens, which expire anyway */ }
            }
        }

        await ConcludeCodeStepAsync(client, baseUrl, runId, registration, job.Id, completion);
        // The container itself is left warm, exactly like an agent job's (ADR 0002/0003); the
        // reaper and `pks agentics runner cleanup` own its removal.
    }

    private async Task ConcludeCodeStepAsync(
        HttpClient client, string baseUrl, string runId, AgenticsRunnerRegistration registration,
        string jobId, (string Conclusion, string Logs) completion)
    {
        // CancellationToken.None: a shutdown must still be able to conclude the job it claimed.
        await PatchJobStatusWithLogsAsync(client, baseUrl, runId, jobId,
            "completed", completion.Conclusion, completion.Logs, CancellationToken.None);
        await ReportJobResultAsync(registration,
            completion.Conclusion == "success" ? "success" : "failed",
            completion.Conclusion == "success" ? null : completion.Logs.Split('\n')[0],
            CancellationToken.None);
    }

    // ── Shared with the agent spawn path ─────────────────────────────────────────────────────

    /// <summary>
    /// The per-job resources a devcontainer job holds for its whole life, plus what
    /// <see cref="ProvisionJobContainerAsync"/> produced. Disposing it stops the proxies in the
    /// reverse order they were started (git, OTLP, Agentics) -- what the agent path's three
    /// <c>await using</c> locals did before they moved here.
    /// </summary>
    private sealed class JobContainerContext : IAsyncDisposable
    {
        public AgenticsProxy? AgenticsProxy { get; set; }
        public AgenticsProxyOptions? AgenticsProxyOptions { get; set; }
        public string? ClaudeVolumeName { get; set; }
        public OtlpProxy? OtlpProxy { get; set; }
        public PKS.Infrastructure.Services.GitProxy.GitProxyDaemon? GitProxy { get; set; }
        public bool HasMarketplaceTokens { get; set; }
        /// <summary>Set once the job is claimed.</summary>
        public string? RunId { get; set; }
        public string? ContainerId { get; set; }
        public string? AgentUser { get; set; }
        public DevcontainerSpawnOptions? SpawnOptions { get; set; }
        public string? JobToken { get; set; }
        /// <summary>The claim response (generate-jitconfig), once the job is claimed.</summary>
        public JsonElement? ClaimData { get; set; }
        /// <summary>Set when the job was claimed but the devcontainer could not be spawned.</summary>
        public string? FailureMessage { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (GitProxy is not null) await GitProxy.DisposeAsync();
            if (OtlpProxy is not null) await OtlpProxy.DisposeAsync();
            if (AgenticsProxy is not null) await AgenticsProxy.DisposeAsync();
        }
    }

    /// <summary>
    /// How the container reaches the Agentics server. localhost on the host is not localhost
    /// inside the container: host.docker.internal only resolves on Docker Desktop, so plain Linux
    /// Docker uses the bridge gateway IP; AGENTICS_CONTAINER_HOST overrides for custom subnets.
    /// </summary>
    private static (Uri ServerUri, string ContainerHostIp, string HostForContainer, string AgenticServerForContainer)
        ResolveContainerServer(AgenticsRunnerRegistration registration)
    {
        var serverUri = new Uri(registration.Server);
        var containerHostIp = Environment.GetEnvironmentVariable("AGENTICS_CONTAINER_HOST")
            ?? (OperatingSystem.IsLinux() ? "172.17.0.1" : "host.docker.internal");
        var hostForContainer = serverUri.Host is "localhost" or "127.0.0.1"
            ? containerHostIp
            : serverUri.Host;
        return (serverUri, containerHostIp, hostForContainer, $"{hostForContainer}:{serverUri.Port}");
    }

    /// <summary>
    /// A warm container was not spawned this run, so there is no devcontainer CLI answer for the
    /// user. The workspace's own owner is the same thing by another route: the spawner chowns the
    /// volume to the remote user, so whoever owns the checkout is who should be running in it.
    /// </summary>
    private async Task<string?> ResolveAgentUserAsync(string containerId, string workspaceFolder, string? agentUser)
    {
        if (!string.IsNullOrWhiteSpace(agentUser)) return agentUser;
        var owner = await _spawnerService.ExecInContainerAsync(containerId,
            $"stat -c %U {workspaceFolder} 2>/dev/null", timeoutSeconds: 10);
        var resolved = owner.Output?.Trim();
        return !string.IsNullOrWhiteSpace(resolved) && resolved != "UNKNOWN" ? resolved : agentUser;
    }

    /// <summary>
    /// The AGENTICS_* environment of a container job, as shell export lines: AGENTICS_SERVER and
    /// its deprecated alias AGENTIC_SERVER, AGENTICS_PROJECT, AGENTICS_JOB_ID, PKS_TOKEN (when a job
    /// token was minted), AGENTICS_TOKEN, AGENTICS_OWNER, AGENTICS_PROJECT_NAME and
    /// AGENTICS_BASE_URL. start.sh and the code_step script both write exactly these lines.
    ///
    /// PKS_TOKEN is the bearer for this job's credential socket. The helper and askpass scripts
    /// read it first and fall back to a value baked in when they were generated -- and those
    /// scripts are written by <c>devcontainer up</c>, which a warm container skips. A reused
    /// container therefore carries the previous job's baked token (and after a runner restart one
    /// nobody can verify, since the signing key is per process); exporting it per job is what
    /// makes the current job's token the one that is sent.
    /// </summary>
    internal static IReadOnlyList<string> BuildJobAgenticsExportLines(
        AgenticsRunnerRegistration registration,
        string jobId,
        string? jobToken,
        string agenticServerForContainer,
        string? operatorServerUrl,
        Uri serverUri)
    {
        var lines = new List<string>(BuildAgenticsServerExportLines(agenticServerForContainer, operatorServerUrl))
        {
            $"export AGENTICS_PROJECT={registration.Owner}/{registration.Project}",
            $"export AGENTICS_JOB_ID={jobId}",
        };
        if (jobToken is not null)
            lines.Add($"export PKS_TOKEN='{jobToken}'");
        lines.Add($"export AGENTICS_TOKEN={ShellSingleQuote(registration.Token)}");
        lines.Add($"export AGENTICS_OWNER='{registration.Owner}'");
        lines.Add($"export AGENTICS_PROJECT_NAME='{registration.Project}'");
        var agenticsBaseUrl = $"{serverUri.Scheme}://{serverUri.Host}{(serverUri.IsDefaultPort ? "" : $":{serverUri.Port}")}";
        lines.Add($"export AGENTICS_BASE_URL='{agenticsBaseUrl}'");
        return lines;
    }
}
