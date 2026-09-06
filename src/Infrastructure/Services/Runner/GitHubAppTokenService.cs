using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Where a GitHub App's identity comes from. All three fields are non-secret except
/// <see cref="PrivateKeyPem"/>, which is the App's signing key and must never be logged,
/// echoed, or handed to anything running inside a job container.
/// </summary>
/// <param name="AppId">Numeric App ID (the JWT's <c>iss</c>), from the App's settings page.</param>
/// <param name="Slug">
/// The App's URL slug — <c>si14-x</c> for <c>github.com/apps/si14-x</c>. This is what the
/// bot login follows (<c>si14-x[bot]</c>) and what <see cref="InstallUrl"/> is built from,
/// so a typo here produces a working token and a 404 install link.
/// </param>
/// <param name="PrivateKeyPem">PEM-encoded RSA private key, PKCS#1 or PKCS#8.</param>
public sealed record GitHubAppConfig(string AppId, string Slug, string PrivateKeyPem);

/// <summary>An installation access token and the moment GitHub says it dies.</summary>
public sealed record InstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Thrown when the App is configured correctly but simply is not installed on the repo.
/// This is not a failure of ours and not something the runner can fix: installation is an
/// act the repository owner performs in a browser. The message carries the URL that does it.
/// </summary>
public sealed class GitHubAppNotInstalledException : Exception
{
    public GitHubAppNotInstalledException(string owner, string repo, string installUrl)
        : base($"The GitHub App is not installed on {owner}/{repo}. Install it here, then start the runner again:\n  {installUrl}")
    {
        Owner = owner;
        Repo = repo;
        InstallUrl = installUrl;
    }

    public string Owner { get; }
    public string Repo { get; }
    public string InstallUrl { get; }
}

public interface IGitHubAppTokenService
{
    /// <summary>The page a human visits to install the App. Printed, never fetched.</summary>
    string InstallUrl { get; }

    /// <summary>
    /// The installation id for a repo, or <c>null</c> when the App is not installed on it.
    /// Null is the answer to "should the runner block and ask the owner to install?" — every
    /// other failure throws.
    /// </summary>
    Task<long?> FindInstallationIdAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// A token scoped to exactly one repository with contents+PR write, cached until shortly
    /// before it expires. Throws <see cref="GitHubAppNotInstalledException"/> when the App is
    /// not installed on that repo.
    /// </summary>
    Task<InstallationToken> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mints short-lived, repo-scoped GitHub App installation tokens from the App's private key.
///
/// The point of this class is the boundary it draws. The private key lives here, in the
/// runner's own process on the runner's own host, and never moves; what crosses into a job
/// container is a token that expires in an hour and can only write contents and pull requests
/// on one named repository. An agent that leaks its token leaks an hour of access to one repo,
/// not a permanent identity across every repo the operator can reach.
///
/// GitHub's auth chain has two steps and they are not interchangeable:
///
///   private key --RS256--> App JWT (<=10 min, iss=AppId)  ... can ONLY read installations
///                                                             and mint installation tokens
///   App JWT --------------> installation token (1 h)      ... can actually touch repos
///
/// Both <c>GET /repos/{o}/{r}/installation</c> and <c>POST /app/installations/{id}/access_tokens</c>
/// take the JWT, which is why the key has to be in hand before we can even ask whether the App
/// is installed. There is no cheaper probe that skips the key.
/// </summary>
public sealed class GitHubAppTokenService : IGitHubAppTokenService, IDisposable
{
    private const string ApiBase = "https://api.github.com";

    /// <summary>
    /// GitHub caps App JWTs at 10 minutes and rejects anything beyond it outright. Nine minutes
    /// leaves a minute of headroom; the JWT is minted per call and never stored.
    /// </summary>
    private static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(9);

    /// <summary>
    /// How early a cached token is treated as spent. An installation token lives an hour; five
    /// minutes covers a slow clone that started just before expiry, plus clock skew against
    /// GitHub, without re-minting on every call.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly GitHubAppConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action<string>? _onLog;
    private readonly TimeProvider _time;

    private readonly SemaphoreSlim _mintLock = new(1, 1);
    private readonly Dictionary<string, InstallationToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _installations = new(StringComparer.OrdinalIgnoreCase);

    public GitHubAppTokenService(
        GitHubAppConfig config,
        HttpClient? httpClient = null,
        Action<string>? onLog = null,
        TimeProvider? timeProvider = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (string.IsNullOrWhiteSpace(config.AppId))
            throw new ArgumentException("GitHub App ID is required.", nameof(config));
        if (string.IsNullOrWhiteSpace(config.Slug))
            throw new ArgumentException("GitHub App slug is required — it is what the install URL and the bot login are built from.", nameof(config));

        // Parse the key once, at construction, so a malformed key fails loudly at startup
        // rather than on the first git operation half an hour into a job.
        using (var probe = RSA.Create())
        {
            try
            {
                probe.ImportFromPem(config.PrivateKeyPem);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    "The GitHub App private key could not be parsed. Expected a PEM-encoded RSA key " +
                    "(the .pem GitHub hands you when you generate one), PKCS#1 or PKCS#8.", nameof(config), ex);
            }
        }

        _ownsHttp = httpClient == null;
        _http = httpClient ?? new HttpClient();
        _onLog = onLog;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string InstallUrl => $"https://github.com/apps/{_config.Slug}/installations/new";

    /// <summary>
    /// The commit author address for this App's bot. The numeric user id is not derivable from
    /// the App ID — it comes from <c>GET /users/{slug}[bot]</c> — so this takes it as a parameter
    /// rather than pretending to know it.
    /// </summary>
    public string BotCommitEmail(long botUserId) => $"{botUserId}+{_config.Slug}[bot]@users.noreply.github.com";

    public async Task<long?> FindInstallationIdAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        var key = $"{owner}/{repo}";
        lock (_installations)
        {
            if (_installations.TryGetValue(key, out var cached)) return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/repos/{owner}/{repo}/installation");
        Decorate(request, CreateAppJwt());

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 404 is the "not installed" answer. GitHub also returns 404 for a repo the App cannot
        // see at all, which is the same situation from the runner's side: installing grants it.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _onLog?.Invoke($"GitHub App is not installed on {key}");
            return null;
        }

        await ThrowIfFailedAsync(response, $"look up the App installation for {key}", cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var id = body.GetProperty("id").GetInt64();

        lock (_installations)
        {
            _installations[key] = id;
        }
        return id;
    }

    public async Task<InstallationToken> GetInstallationTokenAsync(string owner, string repo, CancellationToken cancellationToken = default)
    {
        var key = $"{owner}/{repo}";

        if (TryGetLiveToken(key, out var cached)) return cached;

        await _mintLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: several git operations start at once at job startup, and
            // without this they would each mint a token for the same repo.
            if (TryGetLiveToken(key, out cached)) return cached;

            var installationId = await FindInstallationIdAsync(owner, repo, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubAppNotInstalledException(owner, repo, InstallUrl);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{ApiBase}/app/installations/{installationId}/access_tokens");
            Decorate(request, CreateAppJwt());

            // The narrowing that makes this whole design worth having. The installation may well
            // be granted more repos and more permissions than this; the token is not. Repositories
            // are named rather than given by id so no extra lookup is needed.
            request.Content = JsonContent.Create(new
            {
                repositories = new[] { repo },
                permissions = new { contents = "write", pull_requests = "write" }
            });

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, $"mint an installation token for {key}", cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
            var token = body.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("GitHub returned an installation token response with no token.");
            var expiresAt = body.TryGetProperty("expires_at", out var exp) && exp.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(exp.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : _time.GetUtcNow().AddHours(1);

            var minted = new InstallationToken(token, expiresAt);
            lock (_tokens)
            {
                _tokens[key] = minted;
            }

            // Deliberately logs the expiry and not the token.
            _onLog?.Invoke($"Minted GitHub App token for {key}, contents+pull_requests write, expires {expiresAt:u}");
            return minted;
        }
        finally
        {
            _mintLock.Release();
        }
    }

    private bool TryGetLiveToken(string key, out InstallationToken token)
    {
        lock (_tokens)
        {
            if (_tokens.TryGetValue(key, out var found) && found.ExpiresAt - RefreshMargin > _time.GetUtcNow())
            {
                token = found;
                return true;
            }
        }
        token = null!;
        return false;
    }

    /// <summary>
    /// Builds and signs the App JWT. <c>iat</c> is backdated a minute because GitHub rejects a
    /// token issued in its own future, and the runner's clock is not GitHub's.
    /// </summary>
    private string CreateAppJwt()
    {
        var now = _time.GetUtcNow();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = now.AddSeconds(-60).ToUnixTimeSeconds(),
            exp = now.Add(JwtLifetime).ToUnixTimeSeconds(),
            iss = _config.AppId
        }));

        var signingInput = $"{header}.{payload}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_config.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static void Decorate(HttpRequestMessage request, string bearer)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        // GitHub rejects API requests with no User-Agent.
        request.Headers.UserAgent.ParseAdd("pks-cli");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 500) body = body[..500];
        throw new InvalidOperationException($"Failed to {what}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _mintLock.Dispose();
    }
}
