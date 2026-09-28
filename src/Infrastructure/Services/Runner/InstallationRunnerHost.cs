using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>A project the platform wants a runner for, as the managed-runner control plane lists it.</summary>
public sealed record ManagedRunnerProject(string Owner, string Project, ManagedRunnerModelPolicy? ModelPolicy);

public sealed record ManagedRunnerModelPolicy(List<string>? AllowedModels, string? DefaultModel);

/// <summary>
/// The platform side of a host runner: which projects exist, and a runner credential for one of
/// them. Both calls authenticate with a freshly minted installation JWT — the box's own key, no
/// shared control token.
/// </summary>
public interface IManagedRunnerControlClient
{
    Task<IReadOnlyList<ManagedRunnerProject>> ListProjectsAsync(InstallationContext installation, CancellationToken ct);
    Task<AgenticsRunnerRegistration> BootstrapAsync(InstallationContext installation, string owner, string project, CancellationToken ct);
}

public sealed class ManagedRunnerControlClient : IManagedRunnerControlClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IInstallationTokenSigner _signer;

    public ManagedRunnerControlClient(HttpClient http, IInstallationTokenSigner signer)
    {
        _http = http;
        _signer = signer;
    }

    public async Task<IReadOnlyList<ManagedRunnerProject>> ListProjectsAsync(InstallationContext installation, CancellationToken ct)
    {
        using var request = await RequestAsync(installation, HttpMethod.Get, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, "listing projects", ct);
        var body = await response.Content.ReadFromJsonAsync<ProjectsResponse>(Json, ct);
        return body?.Projects ?? [];
    }

    public async Task<AgenticsRunnerRegistration> BootstrapAsync(InstallationContext installation, string owner, string project, CancellationToken ct)
    {
        using var request = await RequestAsync(installation, HttpMethod.Post, ct);
        request.Content = JsonContent.Create(new { owner, project });
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"registering for {owner}/{project}", ct);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>(Json, ct)
            ?? throw new InvalidOperationException("empty answer from the managed-runner control plane");

        return new AgenticsRunnerRegistration
        {
            Id = body.Id ?? "",
            Name = body.Name ?? $"Host runner ({installation.Id})",
            Token = body.Token ?? "",
            Owner = owner,
            Project = project,
            Server = installation.Server,
            InternalServer = installation.InternalServer,
            RegisteredAt = DateTime.UtcNow,
        };
    }

    private async Task<HttpRequestMessage> RequestAsync(InstallationContext installation, HttpMethod method, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, $"{installation.ApiBase}/api/internal/managed-runners");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _signer.MintAsync(installation, installation.Audience, ct));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var hint = response.StatusCode is HttpStatusCode.Unauthorized
            ? " — the platform does not know this box's installation key (INSTALLATION_ID / INSTALLATION_PUBLIC_KEY in its environment)"
            : "";
        throw new InvalidOperationException(
            $"{what} failed ({(int)response.StatusCode}): {(body.Length > 300 ? body[..300] : body)}{hint}");
    }

    private sealed record ProjectsResponse(List<ManagedRunnerProject>? Projects);

    private sealed record BootstrapResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("token")] string? Token);
}

/// <summary>Starts and stops the per-project runner processes. A seam so the host's bookkeeping can
/// be tested without spawning anything.</summary>
public interface IProjectRunnerLauncher
{
    IProjectRunnerProcess Start(AgenticsRunnerRegistration registration);
}

public interface IProjectRunnerProcess
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task StopAsync(TimeSpan grace);
}

/// <summary>
/// One runner for every project on a self-hosted box. It asks the platform which projects exist,
/// gets a runner credential for each new one, and keeps one ordinary
/// <c>pks agentics runner run --project owner/project</c> alive per project — the same runner a
/// developer starts by hand, so a job behaves identically on a box and on a laptop.
///
/// All of its own traffic goes to <see cref="InstallationContext.InternalServer"/>; the
/// registrations it writes carry the public <see cref="InstallationContext.Server"/> for jobs and
/// vibecast. A child that exits with an error is re-registered before it is restarted: the
/// commonest cause is a rotated credential, and re-registering is idempotent on the platform.
/// </summary>
public sealed class InstallationRunnerHost
{
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);

    private readonly InstallationContext _installation;
    private readonly IManagedRunnerControlClient _control;
    private readonly IAgenticsRunnerConfigurationService _registrations;
    private readonly IProjectRunnerLauncher _launcher;
    private readonly Action<string> _log;
    private readonly Dictionary<string, IProjectRunnerProcess> _running = new(StringComparer.OrdinalIgnoreCase);

    public InstallationRunnerHost(
        InstallationContext installation,
        IManagedRunnerControlClient control,
        IAgenticsRunnerConfigurationService registrations,
        IProjectRunnerLauncher launcher,
        Action<string> log)
    {
        _installation = installation;
        _control = control;
        _registrations = registrations;
        _launcher = launcher;
        _log = log;
    }

    public IReadOnlyCollection<string> RunningProjects => _running.Keys;

    public async Task RunAsync(CancellationToken ct)
    {
        _log($"Host runner for installation {_installation.Id}: platform {_installation.Server}, runner traffic via {_installation.ApiBase}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ReconcileOnceAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log($"Reconcile failed: {ex.Message}");
                }

                try { await Task.Delay(ReconcileInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            await StopAllAsync();
        }
    }

    /// <summary>One pass: bring the set of running project runners in line with the platform.</summary>
    public async Task ReconcileOnceAsync(CancellationToken ct)
    {
        var projects = await _control.ListProjectsAsync(_installation, ct);
        var wanted = projects.ToDictionary(p => Key(p.Owner, p.Project), StringComparer.OrdinalIgnoreCase);

        foreach (var key in _running.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _log($"{key}: project is gone — stopping its runner");
            await _running[key].StopAsync(TimeSpan.FromSeconds(30));
            _running.Remove(key);
        }

        var saved = await _registrations.ListRegistrationsAsync();
        foreach (var (key, project) in wanted)
        {
            if (_running.TryGetValue(key, out var process) && !process.HasExited) continue;

            var crashed = process is { HasExited: true, ExitCode: not 0 };
            if (process != null)
                _log($"{key}: runner exited ({process.ExitCode?.ToString() ?? "?"}) — restarting");

            var registration = saved.FirstOrDefault(r => Key(r.Owner, r.Project).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (registration != null && !IsOurs(registration))
            {
                // Someone registered this project on this machine against another server (a
                // developer pointing at agentics.dk, say). Registrations are keyed by owner/project,
                // so taking it over would silently repoint their runner.
                _log($"{key}: a registration for {registration.Server} already exists on this machine — leaving it alone");
                continue;
            }

            if (registration == null || crashed || string.IsNullOrEmpty(registration.Token))
            {
                registration = await _control.BootstrapAsync(_installation, project.Owner, project.Project, ct);
                await _registrations.AddRegistrationAsync(registration);
                _log($"{key}: registered as {registration.Name}");
            }
            else if (!string.Equals(registration.InternalServer, _installation.InternalServer, StringComparison.OrdinalIgnoreCase))
            {
                registration.InternalServer = _installation.InternalServer;
                await _registrations.AddRegistrationAsync(registration);
            }

            _running[key] = _launcher.Start(registration);
            _log($"{key}: runner started");
        }
    }

    private bool IsOurs(AgenticsRunnerRegistration registration) =>
        string.Equals(registration.Server.TrimEnd('/'), _installation.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private async Task StopAllAsync()
    {
        await Task.WhenAll(_running.Values.Select(p => p.StopAsync(TimeSpan.FromSeconds(30))));
        _running.Clear();
    }

    private static string Key(string owner, string project) => $"{owner}/{project}";
}

/// <summary>Runs each project's runner as a child <c>pks agentics runner run</c>, its output
/// forwarded line by line with a project prefix (into the journal, under systemd).</summary>
public sealed class ChildProcessProjectRunnerLauncher : IProjectRunnerLauncher
{
    private readonly RunnerLauncherCommand _self;
    private readonly Action<string> _log;

    public ChildProcessProjectRunnerLauncher(RunnerLauncherCommand self, Action<string> log)
    {
        _self = self;
        _log = log;
    }

    public IProjectRunnerProcess Start(AgenticsRunnerRegistration registration)
    {
        var project = $"{registration.Owner}/{registration.Project}";
        var workDir = LocalRunnerSupervisor.DefaultWorkDir(registration.Owner, registration.Project);
        Directory.CreateDirectory(workDir);

        var arguments = $"--no-logo agentics runner run --project {RunnerLauncher.Quote(project)} --no-prompt --work-dir {RunnerLauncher.Quote(workDir)}";
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workDir,
        };
        psi.ArgumentList.Add("-c");
        // exec: the shell becomes the runner, so a signal to the child reaches pks, not sh.
        psi.ArgumentList.Add($"exec {_self.BuildCommandLine(arguments)}");
        // The child is an ordinary per-project runner; it must not become a host itself.
        psi.Environment.Remove(InstallationContext.IdVariable);

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"could not start the runner for {project}");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) _log($"[{project}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) _log($"[{project}] {e.Data}"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ChildProcess(process);
    }

    private sealed class ChildProcess(Process process) : IProjectRunnerProcess
    {
        public bool HasExited => process.HasExited;
        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public async Task StopAsync(TimeSpan grace)
        {
            if (process.HasExited) return;
            try
            {
                // SIGINT, not SIGTERM: the runner's graceful shutdown hangs off Ctrl+C and it does not
                // react to SIGTERM at all (measured). Process.Kill is SIGKILL, the last resort.
                using (var interrupt = Process.Start("kill", ["-INT", process.Id.ToString()])) interrupt?.WaitForExit();
                using var timeout = new CancellationTokenSource(grace);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }
    }
}
