using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Expo;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Lightweight HTTP server running on a Unix socket that serves git credentials to job
/// containers.
///
/// It serves one of two things, and which one is a deliberate configuration choice rather
/// than an accident of what happens to be available:
///
///   * <b>GitHub App mode</b> — when an <see cref="IGitHubAppTokenService"/> is supplied, every
///     request is answered with an installation token scoped to the single repository being
///     cloned, valid for an hour, able to write only contents and pull requests. The App's
///     private key never leaves the runner process.
///   * <b>Operator mode</b> — otherwise, the locally stored device-code OAuth token. This is
///     the operator's own identity with the operator's full scope, so everything the job does
///     is attributed to a person and reaches every repo that person can reach.
///
/// App mode never silently degrades to operator mode. If the App is configured and a token
/// cannot be minted, the request fails: falling back would attribute the App's work to a human
/// and hand a job far more access than it asked for, which is the outcome App mode exists to
/// prevent.
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
    private readonly ICertStore? _certStore;
    private readonly IExpoCredentialService? _expoCredentials;
    private readonly IGitHubAppTokenService? _appTokens;
    private WebApplication? _app;

    /// <summary>
    /// The repository App-mode tokens are scoped to when a request does not name one itself.
    ///
    /// It exists because the two ways a container asks for a credential carry different amounts
    /// of information. A git credential helper is handed the host and path on stdin and can say
    /// which repository it wants; GIT_ASKPASS is handed a prompt string and cannot. The runner
    /// sets this when it resolves a job's git URL, so the askpass path still gets a narrowed
    /// token instead of a broad one.
    /// </summary>
    private volatile Tuple<string, string>? _defaultRepository;

    public GitCredentialServer(
        IGitHubAuthenticationService githubAuth,
        string socketId,
        Action<string>? onLog = null,
        IJobTokenService? tokenService = null,
        ICoolifyTokenStore? tokenStore = null,
        IRegistryConfigurationService? registryConfig = null,
        ICertStore? certStore = null,
        IExpoCredentialService? expoCredentials = null,
        IGitHubAppTokenService? appTokens = null)
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
        _certStore = certStore;
        _expoCredentials = expoCredentials;
        _appTokens = appTokens;
    }

    /// <summary>True when this server issues GitHub App tokens rather than the operator's own.</summary>
    public bool ActsAsGitHubApp => _appTokens != null;

    public const string EnforceEntitlementVariable = "PKS_CREDENTIAL_ENFORCE_ENTITLEMENT";

    /// <summary>
    /// Whether a credential request that is not covered by a job token is refused rather than
    /// served-and-logged.
    ///
    /// It is off by default and read from the environment on purpose. We do not yet know how
    /// spread the legitimate use is — a station that clones a repo and then fetches a submodule
    /// asks for two repositories under one job — so the honest order is to log the mismatches
    /// first and refuse them once the log says which ones are real. An environment variable can
    /// be turned on, and back off, on the box without cutting a release.
    /// </summary>
    public bool EnforceEntitlement { get; set; } =
        IsTruthy(System.Environment.GetEnvironmentVariable(EnforceEntitlementVariable));

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value == "1" ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Names the repository that App-mode credentials default to. Called by the runner once a
    /// job's git URL is known. Ignored entirely in operator mode.
    /// </summary>
    public void SetDefaultRepository(string owner, string repo)
    {
        _defaultRepository = string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)
            ? null
            : Tuple.Create(owner, repo);
    }

    /// <summary>
    /// Works out which repository a credential request is for.
    ///
    /// Git hands a credential helper a <c>path</c> like <c>pksorensen/commuteconnects.git</c>
    /// when <c>credential.useHttpPath</c> is set; the helper forwards it as <c>repo</c>. Anything
    /// deeper than owner/name (or shallower) is not a repository path and is refused rather than
    /// guessed at, because a wrong guess here mints a token for the wrong repository.
    /// </summary>
    internal static Tuple<string, string>? ParseRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim().Trim('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? Tuple.Create(parts[0], parts[1]) : null;
    }

    /// <summary>
    /// The directory containing the credential socket. Bind-mount this directory
    /// (not the socket file) so that containers survive runner restarts.
    /// </summary>
    public string SocketDirectory => _socketDir;

    public string SocketPath => _socketPath;

    /// <summary>
    /// Answers a credential request with a GitHub App installation token scoped to one repository.
    ///
    /// Every failure path here returns an error rather than the operator's token. That is the
    /// whole point of the mode, and it is why "which repository?" is answered from the request or
    /// from a repository the runner explicitly named, never from a default that happens to be
    /// lying around.
    /// </summary>
    private async Task<IResult> ServeAppCredentialAsync(HttpRequest request, string host)
    {
        var (auth, claims) = ClassifyRequest(request);

        var repository = ParseRepository(request.Query["repo"].FirstOrDefault());
        if (repository == null)
        {
            // A job token answers the askpass case — the one that cannot name a repository —
            // without consulting _defaultRepository at all. That field is a single slot on a
            // server shared by every concurrent job, so one job's SetDefaultRepository overwrites
            // another's; entitlement that travels with the request cannot be raced. The default
            // stays as the fallback for requests that carry no token.
            repository = claims != null
                ? (claims.Repos.Count == 1 ? ParseRepository(claims.Repos[0]) : null)
                : _defaultRepository;
        }

        if (repository == null)
        {
            _onLog?.Invoke($"Credential unavailable (503) for host {host}: App mode is on but the request named no repository");
            return Results.Problem(
                "This runner acts as a GitHub App, which issues tokens scoped to a single repository, " +
                "but the request did not say which one. The credential helper sends it as 'repo=owner/name' " +
                "(git supplies the path when credential.useHttpPath is set).",
                statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }

        var (owner, repo) = (repository.Item1, repository.Item2);

        var entitled = claims != null && claims.IsEntitledTo(owner, repo);
        // Deliberately one line and deliberately loud: this is the entire yield of the
        // observation phase, and someone has to read weeks of it before enforcement is turned
        // on. It never carries the token itself, only what the token said.
        _onLog?.Invoke(
            $"Credential entitlement: auth={auth.ToString().ToLowerInvariant()} " +
            $"job={(claims == null ? "(none)" : claims.JobId)} requested={owner}/{repo} " +
            $"entitled=[{(claims == null ? "" : string.Join(" ", claims.Repos))}] " +
            $"match={(claims == null ? "n/a" : entitled.ToString().ToLowerInvariant())} " +
            $"enforce={EnforceEntitlement.ToString().ToLowerInvariant()}");

        if (EnforceEntitlement && !entitled)
        {
            var why = claims == null
                ? $"the request carried no valid job token ({auth.ToString().ToLowerInvariant()})"
                : $"{owner}/{repo} is not among the repositories job {claims.JobId} was entitled to";
            _onLog?.Invoke($"Credential refused (403) for {owner}/{repo}: {why}");

            return Results.Problem(
                $"This runner refuses credentials outside a job's entitlement, and {why}.",
                statusCode: (int)HttpStatusCode.Forbidden);
        }

        try
        {
            var token = await _appTokens!.GetInstallationTokenAsync(owner, repo);
            _onLog?.Invoke($"Credential served for {owner}/{repo} as GitHub App (expires {token.ExpiresAt:u})");
            // Revealed for the same reason as the operator token below: this response *is* the handoff.
            return Results.Json(new { username = "x-access-token", password = token.Token });
        }
        catch (GitHubAppNotInstalledException ex)
        {
            // Actionable and not our failure to fix: someone has to install the App on this repo.
            _onLog?.Invoke($"Credential unavailable for {owner}/{repo}: the App is not installed. {ex.InstallUrl}");
            return Results.Problem(ex.Message, statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
        catch (Exception ex)
        {
            _onLog?.Invoke($"Credential unavailable for {owner}/{repo}: {ex.Message}");
            return Results.Problem(
                $"Could not mint a GitHub App token for {owner}/{repo}: {ex.Message}",
                statusCode: (int)HttpStatusCode.ServiceUnavailable);
        }
    }

    /// <summary>
    /// Why a request has no claims, kept separate from whether it has any.
    ///
    /// "No header at all" and "a header we rejected" are different failures with different
    /// fixes, and both will occur in production: the signing key is generated per process, so a
    /// container that outlives a runner restart presents a token signed with a key that no
    /// longer exists, and a job running longer than the token's four hours presents an expired
    /// one. Collapsing them into a single null makes the observation logs uninterpretable.
    /// </summary>
    private enum CredentialAuth
    {
        None,
        Rejected,
        Ok
    }

    private (CredentialAuth Outcome, JobTokenClaims? Claims) ClassifyRequest(HttpRequest request)
    {
        if (_tokenService == null) return (CredentialAuth.None, null);

        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return (CredentialAuth.None, null);

        var claims = _tokenService.ValidateToken(authHeader["Bearer ".Length..]);

        return claims == null ? (CredentialAuth.Rejected, null) : (CredentialAuth.Ok, claims);
    }

    private JobTokenClaims? ValidateRequest(HttpRequest request) => ClassifyRequest(request).Claims;

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

            if (_appTokens != null)
                return await ServeAppCredentialAsync(request, host);

            var storedToken = await _githubAuth.GetStoredTokenAsync();
            if (storedToken is { IsValid: true, AccessToken.HasValue: true })
            {
                _onLog?.Invoke($"Credential served successfully for host: {host}");
                // Revealed explicitly: this response *is* the credential handoff to git, and an
                // anonymous object serialized with default options would quietly ship "***" and leave
                // every push failing with an authentication error that names the wrong cause.
                return Results.Json(new { password = storedToken.AccessToken.Reveal() });
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

            // claims.AppUuid is only set when the job named an app outright; the usual path resolves
            // it from the environment, so log what we are actually deploying, not the empty claim.
            _onLog?.Invoke($"Proxying deploy for app {app.Name} (uuid={app.Uuid}, job {claims.JobId})");

            try
            {
                using var httpClient = new HttpClient();
                var deployUrl = app.WebhookUrl;

                // POST, not GET. Coolify 4.3.14 moved every action endpoint (/deploy, and the
                // application start/restart/stop routes) to POST and answers GET with a 405 and
                // `Allow: POST` from OtherController::post_required. The uuid stays in the query
                // string — deploy() reads it with $request->input(), which covers both.
                using var deployRequest = new HttpRequestMessage(HttpMethod.Post, deployUrl);
                deployRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", app.Token);
                var response = await httpClient.SendAsync(deployRequest);
                var body = await response.Content.ReadAsStringAsync();

                _onLog?.Invoke($"Deploy proxy response: {(int)response.StatusCode} for app {app.Uuid}");
                return Results.Text(body, "application/json", statusCode: (int)response.StatusCode);
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"Deploy proxy error for app {app.Uuid}: {ex.Message}");
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

        // Vend a short-lived, materialized signing PFX to an in-container `pks sign`. The encrypted
        // blob + KEK stay on the host; only a one-shot PFX (random password) crosses the socket.
        _app.MapGet("/cert/pfx", async (HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            if (_certStore == null)
                return Results.Json(new { error = "cert service unavailable" }, statusCode: 503);

            var id = request.Query["id"].FirstOrDefault();
            CertRecord? record;
            if (!string.IsNullOrWhiteSpace(id)) record = await _certStore.FindAsync(id);
            else
            {
                var all = await _certStore.ListAsync();
                record = all.Count == 1 ? all[0] : null;
            }

            if (record == null)
                return Results.Json(new { error = "no certificate available" }, statusCode: 404);

            using var pfx = await _certStore.MaterializePfxAsync(record.Id);
            var bytes = await File.ReadAllBytesAsync(pfx.Path);
            _onLog?.Invoke($"Signing cert served: {record.Id} ({record.Thumbprint})");
            return Results.Json(new
            {
                pfxBase64 = Convert.ToBase64String(bytes),
                password = pfx.Password,
                thumbprint = record.Thumbprint,
                publicCertPem = record.PublicCertPem,
            });
        });

        // Vend the host's Expo robot token to a job that asked for it. Scoped twice: the per-job JWT
        // proves which repository the job belongs to, and that repository must have been registered
        // with `--expo`. Without the second check every repo on the box could spend the token.
        _app.MapGet("/expo/token", async (HttpRequest request) =>
        {
            var claims = ValidateRequest(request);
            if (claims == null)
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);

            if (_expoCredentials == null)
                return Results.Json(new { error = "expo service unavailable" }, statusCode: 503);

            if (!await _expoCredentials.IsRepoAllowedAsync(claims.Owner, claims.Repo))
            {
                _onLog?.Invoke($"Expo token DENIED for {claims.Owner}/{claims.Repo} (not registered with --expo)");
                return Results.Json(
                    new { error = $"{claims.Owner}/{claims.Repo} is not registered for Expo access" },
                    statusCode: 403);
            }

            var token = await _expoCredentials.RevealTokenAsync();
            if (string.IsNullOrEmpty(token))
                return Results.Json(
                    new { error = "No Expo token stored — run 'pks expo init' on the runner host" },
                    statusCode: 404);

            _onLog?.Invoke($"Expo token served for {claims.Owner}/{claims.Repo} (job {claims.JobId})");
            // Revealed explicitly, for the same reason as /git-credential: this response *is* the
            // credential handoff, and a default-serialized SecretValue would ship "***" and fail
            // later with an error naming the wrong cause.
            return Results.Json(new { token });
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
