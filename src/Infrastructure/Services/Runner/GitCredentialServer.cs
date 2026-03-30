using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Lightweight HTTP server running on a Unix socket that serves the locally stored
/// device-code OAuth token as a git credential.
/// </summary>
public class GitCredentialServer : IAsyncDisposable
{
    private readonly string _socketDir;
    private readonly string _socketPath;
    private readonly IGitHubAuthenticationService _githubAuth;
    private readonly Action<string>? _onLog;
    private readonly IJobTokenService? _tokenService;
    private readonly ICoolifyTokenStore? _tokenStore;
    private readonly IRegistryConfigurationService? _registryConfig;
    private WebApplication? _app;

    public GitCredentialServer(
        IGitHubAuthenticationService githubAuth,
        string socketId,
        Action<string>? onLog = null,
        IJobTokenService? tokenService = null,
        ICoolifyTokenStore? tokenStore = null,
        IRegistryConfigurationService? registryConfig = null)
    {
        // Use a stable directory so we can bind-mount the directory (not the file).
        // Directory mounts survive socket file recreation across runner restarts.
        _socketDir = Path.Combine(Path.GetTempPath(), $"pks-credentials-{socketId}");
        _socketPath = Path.Combine(_socketDir, "creds.sock");
        _githubAuth = githubAuth;
        _onLog = onLog;
        _tokenService = tokenService;
        _tokenStore = tokenStore;
        _registryConfig = registryConfig;
    }

    /// <summary>
    /// The directory containing the credential socket. Bind-mount this directory
    /// (not the socket file) so that containers survive runner restarts.
    /// </summary>
    public string SocketDirectory => _socketDir;

    public string SocketPath => _socketPath;

    private JobTokenClaims? ValidateRequest(HttpRequest request)
    {
        if (_tokenService == null) return null;
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        return _tokenService.ValidateToken(authHeader["Bearer ".Length..]);
    }

    private CoolifyAppMatch? ResolveApp(JobTokenClaims claims, HttpRequest request)
    {
        // Legacy path: token has a specific app_uuid
        if (!string.IsNullOrEmpty(claims.AppUuid))
            return _tokenStore?.GetByAppUuid(claims.AppUuid);

        // New path: resolve by job + environment query param
        var env = request.Query["environment"].FirstOrDefault();
        if (!string.IsNullOrEmpty(env))
            return _tokenStore?.GetByJobIdAndEnvironment(claims.JobId, env);

        // Fallback: first registered app for this job
        return _tokenStore?.GetByJobId(claims.JobId);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_socketDir);

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(_socketPath);
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        _app.MapGet("/git-credential", async (HttpRequest request) =>
        {
            var host = request.Query["host"].FirstOrDefault() ?? "unknown";
            _onLog?.Invoke($"Credential request received for host: {host}");

            var storedToken = await _githubAuth.GetStoredTokenAsync();
            if (storedToken is { IsValid: true, AccessToken: not null })
            {
                _onLog?.Invoke($"Credential served successfully for host: {host}");
                return Results.Json(new { password = storedToken.AccessToken });
            }

            _onLog?.Invoke($"Credential unavailable (503) for host: {host}");
            return Results.Problem(
                "No git credential available — run 'pks github runner register' first",
                statusCode: (int)HttpStatusCode.ServiceUnavailable);
        });

        _app.MapGet("/coolify/token", (HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            var requestedEnv = request.Query["environment"].FirstOrDefault() ?? "(none)";

            // Log all available apps for this job for debugging
            var allApps = _tokenStore?.GetAllByJobId(claims.JobId) ?? new List<CoolifyAppMatch>();
            _onLog?.Invoke($"Token request: job={claims.JobId}, requested_env={requestedEnv}, available_apps=[{string.Join(", ", allApps.Select(a => $"{a.Name}(uuid={a.Uuid}, env={a.EnvironmentName})"))}]");

            var app = ResolveApp(claims, request);
            if (app == null)
                return Results.Json(new { error = "app not found", requested_environment = requestedEnv, available = allApps.Select(a => new { a.Name, a.Uuid, environment = a.EnvironmentName }) }, statusCode: 404);

            var resolved = app.EnvironmentName == requestedEnv ? "exact" : "fallback";
            _onLog?.Invoke($"Resolved: {app.Name} (uuid={app.Uuid}, env={app.EnvironmentName}) [{resolved} match for '{requestedEnv}']");
            return Results.Json(new
            {
                webhook_url = app.WebhookUrl,
                fqdn = app.Fqdn,
                environment = app.EnvironmentName,
                resolved_from = resolved,
                available_environments = allApps.Select(a => new { a.Name, environment = a.EnvironmentName, a.Uuid })
            });
        });

        _app.MapPost("/coolify/deploy", async (HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            var app = ResolveApp(claims, request);
            if (app == null)
                return Results.Json(new { error = "app not found" }, statusCode: 404);

            _onLog?.Invoke($"Proxying deploy for app {claims.AppUuid} (job {claims.JobId})");

            try
            {
                using var httpClient = new HttpClient();
                var deployUrl = app.WebhookUrl;
                using var deployRequest = new HttpRequestMessage(HttpMethod.Get, deployUrl);
                deployRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", app.Token);
                var response = await httpClient.SendAsync(deployRequest);
                var body = await response.Content.ReadAsStringAsync();

                _onLog?.Invoke($"Deploy proxy response: {(int)response.StatusCode} for app {claims.AppUuid}");
                return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Deploy proxy error for app {claims.AppUuid}: {ex.Message}");
                return Results.Json(new { error = $"proxy error: {ex.Message}" }, statusCode: 502);
            }
        });

        _app.MapGet("/coolify/deployments/{uuid}", async (string uuid, HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            var app = ResolveApp(claims, request);
            if (app == null)
                return Results.Json(new { error = "app not found" }, statusCode: 404);

            try
            {
                using var httpClient = new HttpClient();
                var baseUrl = app.InstanceUrl.TrimEnd('/');
                using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/deployments/{uuid}");
                statusRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", app.Token);
                var response = await httpClient.SendAsync(statusRequest);
                var body = await response.Content.ReadAsStringAsync();

                return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Deployment status proxy error: {ex.Message}");
                return Results.Json(new { error = $"proxy error: {ex.Message}" }, statusCode: 502);
            }
        });

        _app.MapGet("/coolify/applications/{uuid}", async (string uuid, HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            var app = ResolveApp(claims, request);
            if (app == null)
                return Results.Json(new { error = "app not found" }, statusCode: 404);

            try
            {
                using var httpClient = new HttpClient();
                var baseUrl = app.InstanceUrl.TrimEnd('/');
                using var healthRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/applications/{uuid}");
                healthRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", app.Token);
                var response = await httpClient.SendAsync(healthRequest);
                var body = await response.Content.ReadAsStringAsync();

                return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Application status proxy error: {ex.Message}");
                return Results.Json(new { error = $"proxy error: {ex.Message}" }, statusCode: 502);
            }
        });

        _app.MapGet("/registry/credential", async (HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            var hostname = request.Query["hostname"].FirstOrDefault();
            if (string.IsNullOrEmpty(hostname))
                return Results.Json(new { error = "hostname required" }, statusCode: 400);

            if (_registryConfig == null)
                return Results.Json(new { error = "registry service unavailable" }, statusCode: 503);

            var entry = await _registryConfig.GetByHostnameAsync(hostname);
            if (entry == null)
                return Results.Json(new { error = $"No registry registered for {hostname}" }, statusCode: 404);

            _onLog?.Invoke($"Registry credential served for: {hostname}");
            return Results.Json(new { username = entry.Username, password = entry.Password });
        });

        await _app.StartAsync(ct);

        // Make socket world-accessible so container users (e.g. 'node') can connect
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (File.Exists(_socketPath))
            {
                File.SetUnixFileMode(_socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }

        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);
    }
}
