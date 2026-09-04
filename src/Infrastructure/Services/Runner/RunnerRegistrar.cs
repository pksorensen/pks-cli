using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Oidc;

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

    /// <summary>
    /// The credential registration presents. <see cref="IAgenticsAuthService.GetTokenAsync"/>'s
    /// own docs name this case ("for runner registration this is the project URL"), but the call
    /// was never wired up, so registration went out bare and the server let it through whenever
    /// AUTH_REQUIRED was false — which is how anyone who knew a project name could mint a live
    /// runner token. The server now requires Keycloak or GitHub OIDC here (a runner token is
    /// refused, so one credential cannot mint another), so the bearer is no longer optional.
    /// <para>
    /// Built inline rather than injected to keep this class static: the two callers construct
    /// no auth service, and threading one through would break the fixtures that build the run
    /// command positionally — the reason this class is static in the first place.
    /// </para>
    /// </summary>
    private static IAgenticsAuthService DefaultAuth(IAgenticsRunnerConfigurationService configService) =>
        new AgenticsAuthService(
            configService,
            new AgenticsAuthConfigurationService(NullLogger<AgenticsAuthConfigurationService>.Instance));

    /// <param name="canPrompt">
    /// Whether a human is watching. When registration is refused for want of a
    /// credential this is what decides between running the device grant here and
    /// telling the caller to run `pks agentics init` — a detached daemon or a CI job
    /// that stopped for ten minutes waiting on a browser would look like a hang.
    /// </param>
    /// <param name="handler">Test seam for the registration POST.</param>
    /// <param name="signIn">Test seam for the device grant; returns a bearer or null.</param>
    public static async Task<AgenticsRunnerRegistration> ResolveOrRegisterAsync(
        IAgenticsRunnerConfigurationService configService,
        string ownerProject,
        string? serverOverride,
        Action<string>? onInfo = null,
        CancellationToken ct = default,
        IAgenticsAuthService? auth = null,
        bool canPrompt = false,
        HttpMessageHandler? handler = null,
        Func<string, Action<string>?, CancellationToken, Task<string?>>? signIn = null)
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

        using var httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        var registerUrl = $"{serverUrl}/api/owners/{owner}/projects/{project}/runners";
        var requestBody = new { name = runnerName, labels = BuildDefaultRunnerLabels() };
        var authService = auth ?? DefaultAuth(configService);
        var audience = $"{serverUrl}/p/{owner}/{project}";

        async Task<HttpResponseMessage> PostAsync(string? bearer)
        {
            httpClient.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(bearer)
                ? null
                : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

            return await httpClient.PostAsJsonAsync(registerUrl, requestBody, ct);
        }

        // Asked separately from the full chain so the retry below can tell "nobody is
        // signed in" from "signed in, but not allowed here" — GetTokenAsync answers null
        // for the first and a runner token for both.
        var userToken = await authService.GetUserTokenAsync(audience, null);
        var httpResponse = await PostAsync(userToken ?? await authService.GetTokenAsync(audience, null, owner, project));

        // Nobody was signed in and the server said so. Running the grant here is the
        // whole difference between "register a runner" and "run `pks agentics init`,
        // then register a runner" — and the credential it writes is the same one init
        // would have written, so this is a shortcut, not a second way in.
        if (userToken is null && canPrompt && NeedsCredential(httpResponse.StatusCode) && CanSignIn(serverUrl))
        {
            httpResponse.Dispose();
            var bearer = await (signIn ?? SignInAsync)(serverUrl, onInfo, ct);
            httpResponse = await PostAsync(bearer ?? userToken);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
            // 401/403 here is nearly always "no usable credential", not "wrong project": the
            // chain falls back to a stored runner token, which this endpoint refuses on purpose.
            var hint = NeedsCredential(httpResponse.StatusCode)
                ? " — run `pks agentics init` to sign in; runner registration accepts only a user "
                  + "credential or GitHub Actions OIDC, never another runner's token"
                : "";
            throw new InvalidOperationException(
                $"Auto-registration failed ({(int)httpResponse.StatusCode}): {errorBody}{hint}");
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

    private static bool NeedsCredential(System.Net.HttpStatusCode status)
        => status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;

    /// <summary>
    /// Whether the device grant has anywhere to go. A loopback server does not front a
    /// public realm, so probing `login.localhost` would spend the operator's attention on
    /// a host that cannot answer; `--keycloak` on `pks agentics init` is the way in there.
    /// <para>
    /// CI needs no rule of its own. Actions redirects stdout, so <c>canPrompt</c> is
    /// already false there — and the OIDC step supplies a user token before the POST goes
    /// out at all, which skips this branch entirely.
    /// </para>
    /// </summary>
    private static bool CanSignIn(string serverUrl) => !new Uri(serverUrl).IsLoopback;

    /// <summary>
    /// The device grant, printed rather than framed: this runs over SSH on a headless box
    /// as often as not, and the registrar has a line-at-a-time callback, not a console.
    /// </summary>
    private static async Task<string?> SignInAsync(string serverUrl, Action<string>? onInfo, CancellationToken ct)
    {
        var host = new Uri(serverUrl).Host;
        onInfo?.Invoke($"Not signed in to {host} — signing in first.");

        using var discoveryHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // No short timeout on the login client: the grant polls until the human is done.
        using var loginHttp = new HttpClient();

        var result = await AgenticsSignIn.SignInAsync(
            new OidcDiscovery(discoveryHttp),
            new DeviceCodeLogin(loginHttp),
            new AgenticsAuthConfigurationService(NullLogger<AgenticsAuthConfigurationService>.Instance),
            host,
            null,
            AgenticsSignIn.DefaultRealm,
            AgenticsSignIn.DefaultClientId,
            prompt =>
            {
                onInfo?.Invoke($"Open {prompt.BestUri}");
                onInfo?.Invoke($"Code: {prompt.UserCode}");
            },
            ct);

        if (!result.Ok)
        {
            onInfo?.Invoke($"Sign-in failed: {result.Error ?? "no access token"}");
            return null;
        }

        onInfo?.Invoke("Signed in — credentials saved to ~/.pks-cli/agentics-auth.json.");
        return result.AccessToken;
    }

    private sealed class RunnerRegistrationResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
