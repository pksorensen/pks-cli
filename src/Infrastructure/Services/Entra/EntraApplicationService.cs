using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Entra;

/// <summary>
/// Provisions the app registration an application signs in with, and keeps its secret somewhere the
/// agent that asked for it cannot read.
///
/// The problem this exists for: an app that authenticates against Entra needs a client id and a client
/// secret, and the way that has always been done is a person opens the portal, clicks through six
/// screens, copies a secret out of a blue box, and pastes it into user secrets — then does it again in
/// six months when it expires, and again on the next machine. The secret ends up in a chat log, a
/// settings file, a terminal transcript, or all three.
///
/// So: pks creates the registration through Graph, mints the secret itself, and writes it straight
/// into the encrypted store. It is never printed, never returned as a string, and the only thing that
/// reads it back is the resolver, on its way into a child process's environment via
/// <see cref="SecretSink"/>. What a person sees is an alias.
/// </summary>
public interface IEntraApplicationService
{
    /// <summary>Whether there is a sign-in that can reach Graph at all.</summary>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>Who the Graph token belongs to, for the "acting as" line. Null when not signed in.</summary>
    Task<(string UserPrincipalName, string TenantId)?> WhoAmIAsync(CancellationToken ct = default);

    /// <summary>
    /// Adopt-or-create. Finds the registration by <c>AdoptAppId</c> or by display name, creates one if
    /// there is none, makes sure it has a service principal and the requested redirect URIs, mints a
    /// client secret unless a live one is already stored, and stores the result under the alias.
    /// </summary>
    Task<EntraAppResult> InitAsync(EntraAppRequest request, CancellationToken ct = default);

    /// <summary>Apps in the directory whose display name starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<EntraApplication>> ListDirectoryAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>What pks holds, newest first. The secrets stay behind <see cref="SecretValue"/>.</summary>
    Task<IReadOnlyList<EntraStoredApp>> ListStoredAsync();

    Task<EntraStoredApp?> GetStoredAsync(string alias);

    /// <summary>Forgets an alias locally. The registration in the directory is untouched.</summary>
    Task<bool> ForgetAsync(string alias);

    /// <summary>
    /// Removes a credential from the registration itself. Destructive and remote — anything already
    /// running on that secret stops working — so callers confirm first.
    /// </summary>
    Task RemoveSecretAsync(string objectId, string keyId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class EntraApplicationService : IEntraApplicationService
{
    private const string GraphRoot = "https://graph.microsoft.com/v1.0";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>`entra.app.{alias}.credentials` — the trailing word is what routes it into the
    /// encrypted store, see <see cref="SecretKeys"/>.</summary>
    private const string KeyPrefix = "entra.app.";
    private const string KeySuffix = ".credentials";

    private readonly HttpClient _http;
    private readonly IAzureFoundryAuthService _foundryAuth;
    private readonly IConfigurationService _configuration;
    private readonly ISecretStore _secretStore;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<EntraApplicationService> _logger;

    public EntraApplicationService(
        HttpClient http,
        IAzureFoundryAuthService foundryAuth,
        IConfigurationService configuration,
        ISecretStore secretStore,
        ISecretResolver secrets,
        ILogger<EntraApplicationService> logger)
    {
        _http = http;
        _foundryAuth = foundryAuth;
        _configuration = configuration;
        _secretStore = secretStore;
        _secrets = secrets;
        _logger = logger;
    }

    // ─────────────────────────────────────────────
    //  Auth
    // ─────────────────────────────────────────────

    public Task<bool> IsAuthenticatedAsync() => _foundryAuth.IsAuthenticatedAsync();

    /// <summary>
    /// A Graph token from the Azure sign-in pks already has. The refresh token was obtained for a
    /// different resource, which is fine: the client is the Azure CLI's own public client, and a
    /// refresh token there is per user and per client, not per resource — the same trade `az` makes
    /// when it moves between ARM and Graph without asking you to sign in twice.
    /// </summary>
    private async Task<string> GraphTokenAsync(CancellationToken ct)
    {
        var token = await _foundryAuth.GetAccessTokenAsync(GraphScope, ct);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                "not signed in to Azure — run `pks foundry init` (or `pks azure login`) first");
        }
        return token;
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GraphTokenAsync(ct));
        return request;
    }

    public async Task<(string UserPrincipalName, string TenantId)?> WhoAmIAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = await RequestAsync(HttpMethod.Get, $"{GraphRoot}/me?$select=userPrincipalName", ct);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var upn = doc.RootElement.TryGetProperty("userPrincipalName", out var u) ? u.GetString() ?? "" : "";
            var tenant = (await _foundryAuth.GetStoredCredentialsAsync())?.TenantId ?? "";
            return (upn, tenant);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graph /me failed");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  The one command that matters
    // ─────────────────────────────────────────────

    public async Task<EntraAppResult> InitAsync(EntraAppRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ArgumentException("an app registration needs a display name", nameof(request));
        }

        var alias = string.IsNullOrWhiteSpace(request.Alias) ? Slug(request.DisplayName) : Slug(request.Alias);
        var result = new EntraAppResult();

        // Adopt, never duplicate. A second registration with the same name and none of the first one's
        // redirect URIs is the failure mode here — it looks like it worked and then nothing can sign in.
        var application = !string.IsNullOrWhiteSpace(request.AdoptAppId)
            ? await FindByAppIdAsync(request.AdoptAppId!, ct)
              ?? throw new InvalidOperationException($"no app registration with appId {request.AdoptAppId}")
            : await FindByDisplayNameAsync(request.DisplayName, ct);

        if (application is null)
        {
            application = await CreateApplicationAsync(request, ct);
            result.CreatedApplication = true;
        }
        else
        {
            var added = await EnsureRedirectUrisAsync(application, request, ct);
            result.AddedRedirectUris = added;
        }

        // Without a service principal the registration is a definition nothing can sign in against —
        // `az ad app create` leaves you in exactly that state and the error much later is unhelpful.
        result.CreatedServicePrincipal = await EnsureServicePrincipalAsync(application.AppId, ct);

        var stored = await GetStoredAsync(alias);
        var needsSecret = request.Rotate
            || stored is null
            || !stored.ClientSecret.HasValue
            || stored.IsExpired
            || !string.Equals(stored.AppId, application.AppId, StringComparison.OrdinalIgnoreCase);

        if (!needsSecret)
        {
            result.App = stored!;
            return result;
        }

        var secret = await AddPasswordAsync(
            application.ObjectId,
            $"pks {alias}",
            DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.SecretDays, 1, 730)),
            ct);
        result.MintedSecret = true;

        var tenantId = request.TenantId
            ?? stored?.TenantId
            ?? (await _foundryAuth.GetStoredCredentialsAsync())?.TenantId
            ?? "";

        var updated = new EntraStoredApp
        {
            Alias = alias,
            DisplayName = application.DisplayName,
            AppId = application.AppId,
            ObjectId = application.ObjectId,
            TenantId = tenantId,
            SecretKeyId = secret.KeyId,
            SecretExpiresOn = secret.ExpiresOn,
            ClientSecret = secret.Value,
            CreatedAt = stored?.CreatedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await StoreAsync(updated);
        result.App = updated;

        // A rotation that leaves the old credential live has not rotated anything. Only the one pks
        // minted itself is removed — a credential somebody else added is theirs.
        if (request.Rotate
            && stored is not null
            && !string.IsNullOrEmpty(stored.SecretKeyId)
            && stored.SecretKeyId != secret.KeyId
            && string.Equals(stored.ObjectId, application.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RemoveSecretAsync(application.ObjectId, stored.SecretKeyId, ct);
                result.RemovedSecretKeyId = stored.SecretKeyId;
            }
            catch (Exception ex)
            {
                // The new secret is already stored and working; failing the whole command here would
                // be worse than an old credential the operator can remove in the portal.
                _logger.LogWarning(ex, "could not remove the previous credential {KeyId}", stored.SecretKeyId);
            }
        }

        return result;
    }

    // ─────────────────────────────────────────────
    //  Graph calls
    // ─────────────────────────────────────────────

    private async Task<EntraApplication?> FindByDisplayNameAsync(string displayName, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"displayName eq '{OData(displayName)}'");
        using var request = await RequestAsync(HttpMethod.Get,
            $"{GraphRoot}/applications?$filter={filter}&$select=id,appId,displayName,signInAudience,web,spa", ct);
        using var response = await _http.SendAsync(request, ct);
        await ThrowOnGraphErrorAsync(response, "search app registrations", ct);

        var page = await response.Content.ReadFromJsonAsync<GraphCollection<EntraApplication>>(cancellationToken: ct);
        return page?.Value?.FirstOrDefault();
    }

    private async Task<EntraApplication?> FindByAppIdAsync(string appId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"appId eq '{OData(appId)}'");
        using var request = await RequestAsync(HttpMethod.Get,
            $"{GraphRoot}/applications?$filter={filter}&$select=id,appId,displayName,signInAudience,web,spa", ct);
        using var response = await _http.SendAsync(request, ct);
        await ThrowOnGraphErrorAsync(response, "look up app registration", ct);

        var page = await response.Content.ReadFromJsonAsync<GraphCollection<EntraApplication>>(cancellationToken: ct);
        return page?.Value?.FirstOrDefault();
    }

    public async Task<IReadOnlyList<EntraApplication>> ListDirectoryAsync(string? prefix = null, CancellationToken ct = default)
    {
        var url = $"{GraphRoot}/applications?$select=id,appId,displayName,signInAudience&$top=50";
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            url += "&$filter=" + Uri.EscapeDataString($"startswith(displayName,'{OData(prefix)}')");
        }

        using var request = await RequestAsync(HttpMethod.Get, url, ct);
        using var response = await _http.SendAsync(request, ct);
        await ThrowOnGraphErrorAsync(response, "list app registrations", ct);

        var page = await response.Content.ReadFromJsonAsync<GraphCollection<EntraApplication>>(cancellationToken: ct);
        return page?.Value ?? new List<EntraApplication>();
    }

    private async Task<EntraApplication> CreateApplicationAsync(EntraAppRequest request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["displayName"] = request.DisplayName,
            ["signInAudience"] = request.SignInAudience,
        };
        if (request.RedirectUris.Count > 0)
        {
            body["web"] = new { redirectUris = request.RedirectUris };
        }
        if (request.SpaRedirectUris.Count > 0)
        {
            body["spa"] = new { redirectUris = request.SpaRedirectUris };
        }

        using var message = await RequestAsync(HttpMethod.Post, $"{GraphRoot}/applications", ct);
        message.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(message, ct);
        await ThrowOnGraphErrorAsync(response, "create the app registration", ct);

        return await response.Content.ReadFromJsonAsync<EntraApplication>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Graph created the app but returned nothing");
    }

    /// <summary>
    /// Adds the requested redirect URIs to whatever is already there. A PATCH replaces the whole
    /// collection, so this reads first and unions — an adopted registration keeps the URIs somebody
    /// else registered.
    /// </summary>
    private async Task<bool> EnsureRedirectUrisAsync(EntraApplication application, EntraAppRequest request, CancellationToken ct)
    {
        var web = (application.Web?.RedirectUris ?? new List<string>()).ToList();
        var spa = (application.Spa?.RedirectUris ?? new List<string>()).ToList();

        var newWeb = request.RedirectUris.Where(u => !web.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
        var newSpa = request.SpaRedirectUris.Where(u => !spa.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList();
        if (newWeb.Count == 0 && newSpa.Count == 0)
        {
            return false;
        }

        var body = new Dictionary<string, object?>();
        if (newWeb.Count > 0)
        {
            web.AddRange(newWeb);
            body["web"] = new { redirectUris = web };
        }
        if (newSpa.Count > 0)
        {
            spa.AddRange(newSpa);
            body["spa"] = new { redirectUris = spa };
        }

        using var message = await RequestAsync(HttpMethod.Patch, $"{GraphRoot}/applications/{application.ObjectId}", ct);
        message.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(message, ct);
        await ThrowOnGraphErrorAsync(response, "add the redirect URI", ct);

        application.Web = new EntraWebSection { RedirectUris = web };
        application.Spa = new EntraWebSection { RedirectUris = spa };
        return true;
    }

    /// <summary>True when it had to create one.</summary>
    private async Task<bool> EnsureServicePrincipalAsync(string appId, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString($"appId eq '{OData(appId)}'");
        using (var probe = await RequestAsync(HttpMethod.Get, $"{GraphRoot}/servicePrincipals?$filter={filter}&$select=id", ct))
        using (var found = await _http.SendAsync(probe, ct))
        {
            await ThrowOnGraphErrorAsync(found, "look for the service principal", ct);
            var page = await found.Content.ReadFromJsonAsync<GraphCollection<GraphIdOnly>>(cancellationToken: ct);
            if (page?.Value?.Count > 0)
            {
                return false;
            }
        }

        using var message = await RequestAsync(HttpMethod.Post, $"{GraphRoot}/servicePrincipals", ct);
        message.Content = JsonContent.Create(new { appId });
        using var response = await _http.SendAsync(message, ct);
        await ThrowOnGraphErrorAsync(response, "create the service principal", ct);
        return true;
    }

    private async Task<EntraSecret> AddPasswordAsync(string objectId, string displayName, DateTimeOffset expires, CancellationToken ct)
    {
        using var message = await RequestAsync(HttpMethod.Post, $"{GraphRoot}/applications/{objectId}/addPassword", ct);
        message.Content = JsonContent.Create(new
        {
            passwordCredential = new
            {
                displayName,
                endDateTime = expires.UtcDateTime.ToString("O"),
            },
        });

        using var response = await _http.SendAsync(message, ct);
        await ThrowOnGraphErrorAsync(response, "add a client secret", ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("secretText", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Graph added the credential but returned no secret");
        }

        return new EntraSecret
        {
            KeyId = root.TryGetProperty("keyId", out var k) ? k.GetString() ?? "" : "",
            DisplayName = displayName,
            ExpiresOn = root.TryGetProperty("endDateTime", out var e) && e.TryGetDateTimeOffset(out var end)
                ? end
                : expires,
            Value = SecretValue.From(text),
        };
    }

    public async Task RemoveSecretAsync(string objectId, string keyId, CancellationToken ct = default)
    {
        using var message = await RequestAsync(HttpMethod.Post, $"{GraphRoot}/applications/{objectId}/removePassword", ct);
        message.Content = JsonContent.Create(new { keyId });
        using var response = await _http.SendAsync(message, ct);
        await ThrowOnGraphErrorAsync(response, "remove the client secret", ct);
    }

    /// <summary>
    /// Graph's errors are the useful half of this API — "Insufficient privileges", "Another object with
    /// the same value for property identifierUris already exists" — and losing them behind a status
    /// code costs an hour every time. The message is surfaced; the request body never is, because it
    /// held a secret on the way in.
    /// </summary>
    private static async Task ThrowOnGraphErrorAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                detail = string.Join(": ", new[] { code, message }.Where(x => !string.IsNullOrEmpty(x)));
            }
        }
        catch (JsonException)
        {
            // Not JSON — an HTML sign-in page from a proxy, most likely. Keep it short.
            detail = detail.Length > 200 ? detail[..200] : detail;
        }

        throw new InvalidOperationException($"could not {what}: {(int)response.StatusCode} {detail}");
    }

    // ─────────────────────────────────────────────
    //  Storage
    // ─────────────────────────────────────────────

    public async Task<EntraStoredApp?> GetStoredAsync(string alias)
    {
        // Slugged here rather than at each call site, because the call sites are not all commands:
        // the resolver looks an alias up straight from a capability's name, and a capability called
        // "Margin V1" would otherwise never find what `--alias "Margin V1"` stored as `margin-v1` —
        // a loop where the hint tells you to run the command you already ran.
        var json = await _secrets.RevealAsync(StorageKey(Slug(alias)));
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EntraStoredApp>(json, SecretJson.Persistence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task StoreAsync(EntraStoredApp app)
    {
        // The one place a client secret is written unmasked, and it writes into the encrypted store.
        var json = JsonSerializer.Serialize(app, SecretJson.Persistence);
        await _configuration.SetAsync(StorageKey(app.Alias), json, global: true);
    }

    public async Task<IReadOnlyList<EntraStoredApp>> ListStoredAsync()
    {
        var descriptors = await _secretStore.ListAsync();
        var apps = new List<EntraStoredApp>();

        foreach (var descriptor in descriptors)
        {
            if (!descriptor.Key.StartsWith(KeyPrefix, StringComparison.Ordinal)
                || !descriptor.Key.EndsWith(KeySuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var alias = descriptor.Key[KeyPrefix.Length..^KeySuffix.Length];
            var app = await GetStoredAsync(alias);
            if (app is not null)
            {
                apps.Add(app);
            }
        }

        return apps.OrderByDescending(a => a.UpdatedAt).ToList();
    }

    public async Task<bool> ForgetAsync(string alias)
    {
        var key = StorageKey(Slug(alias));
        if (!await _secretStore.HasAsync(key))
        {
            return false;
        }

        await _configuration.DeleteAsync(key);
        return true;
    }

    private static string StorageKey(string alias) => $"{KeyPrefix}{alias}{KeySuffix}";

    /// <summary>
    /// An alias is part of a storage key and part of what an operator types, so it is folded to
    /// lowercase words joined by dashes. "Margin v1 (dev)" and "margin-v1-dev" are the same alias.
    /// </summary>
    internal static string Slug(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        return slug.Trim('-');
    }

    /// <summary>OData string literals escape a quote by doubling it. Without this a display name with
    /// an apostrophe is a filter syntax error, or worse, an injected clause.</summary>
    private static string OData(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class GraphCollection<T>
    {
        [JsonPropertyName("value")]
        public List<T>? Value { get; set; }
    }

    private sealed class GraphIdOnly
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}
