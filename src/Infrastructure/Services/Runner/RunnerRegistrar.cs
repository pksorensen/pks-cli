using System.Net.Http.Json;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Resolving <c>owner/project</c> to a usable registration, auto-registering when there isn't one.
///
/// Static rather than injected on purpose: both callers (the foreground <c>runner run</c> and the
/// detached <c>runner start</c>) already hold an <see cref="IAgenticsRunnerConfigurationService"/>,
/// and threading a new dependency through the run command's constructor would break the two test
/// fixtures that construct it positionally.
/// </summary>
public static class RunnerRegistrar
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static (string Owner, string Project) ParseOwnerProject(string ownerProject)
    {
        var parts = (ownerProject ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException($"--project must be in owner/project format, got: '{ownerProject}'");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Turns whatever the operator typed (<c>agentics.dk</c>, <c>localhost:3000</c>,
    /// <c>https://…</c>) into a base URL. localhost stays http; everything else gets https.
    /// </summary>
    public static string NormalizeServer(string? serverOverride)
    {
        var serverHost = serverOverride
            ?? Environment.GetEnvironmentVariable("AGENTICS_SERVER")
            ?? Environment.GetEnvironmentVariable("AGENTIC_SERVER")
            ?? "agentics.dk";

        if (serverHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            serverHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return serverHost.TrimEnd('/');
        }

        var scheme = serverHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     serverHost.StartsWith("127.0.0.1")
            ? "http"
            : "https";
        return $"{scheme}://{serverHost}";
    }

    /// <summary>
    /// Default job-targeting labels sent at registration. "self-hosted" matches the convention
    /// used by the (unrelated, GitHub Actions) runner daemon.
    /// </summary>
    public static string[] BuildDefaultRunnerLabels()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        return new[] { "self-hosted", os };
    }

    public static async Task<AgenticsRunnerRegistration> ResolveOrRegisterAsync(
        IAgenticsRunnerConfigurationService configService,
        string ownerProject,
        string? serverOverride,
        Action<string>? onInfo = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configService);

        var (owner, project) = ParseOwnerProject(ownerProject);

        var registrations = await configService.ListRegistrationsAsync();
        var existing = registrations.FirstOrDefault(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // An explicit --server that differs from the stored URL wins: pointing an existing
            // registration at a dev instance is a normal thing to do, and silently polling the old
            // server would look like "the runner sees no jobs".
            if (!string.IsNullOrEmpty(serverOverride))
            {
                var normalized = NormalizeServer(serverOverride);
                if (!string.Equals(existing.Server, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Server = normalized;
                    await configService.AddRegistrationAsync(existing);
                    onInfo?.Invoke($"Updated server URL for {owner}/{project}: {normalized}");
                }
            }

            return existing;
        }

        onInfo?.Invoke($"No saved registration for {owner}/{project}, registering now...");

        var serverUrl = NormalizeServer(serverOverride);
        var runnerName = System.Net.Dns.GetHostName();

        using var httpClient = new HttpClient();
        var requestBody = new { name = runnerName, labels = BuildDefaultRunnerLabels() };
        var httpResponse = await httpClient.PostAsJsonAsync(
            $"{serverUrl}/api/owners/{owner}/projects/{project}/runners", requestBody, ct);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Auto-registration failed ({(int)httpResponse.StatusCode}): {errorBody}");
        }

        var json = await httpResponse.Content.ReadAsStringAsync(ct);
        var resp = JsonSerializer.Deserialize<RunnerRegistrationResponse>(json, JsonOptions)
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

        await configService.AddRegistrationAsync(registration);
        onInfo?.Invoke($"Registered runner '{registration.Name}' for {owner}/{project}");
        return registration;
    }

    private sealed class RunnerRegistrationResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
