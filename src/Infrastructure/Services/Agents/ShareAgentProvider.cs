using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Agents;

/// <summary>
/// The <c>share</c> agent provider: enrolls a session as a "person" against an
/// Agent Share server (share.agentics.dk) using the OIDC login stored by
/// <c>pks share init</c>. Enrollment alone makes the agent appear in the desktop
/// Share panel — it mints an agent inbox owned by the user (server resolves the
/// owner from the OIDC bearer token; see share-server resolveOwner).
/// </summary>
public sealed class ShareAgentProvider : IAgentProvider
{
    private readonly IShareCredStore _creds;
    private readonly OidcLoopback _oidc;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public ShareAgentProvider(IShareCredStore creds, OidcLoopback oidc)
    {
        _creds = creds;
        _oidc = oidc;
    }

    public string Name => "share";
    public string Description => "Agent Share — appear in the Windows Share panel as a person";

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        => (await _creds.ListAsync(ct)).Count > 0;

    public async Task<AgentRegistration> RegisterAsync(AgentIdentity identity, CancellationToken ct = default)
    {
        var cred = await _creds.GetAsync(null, ct)
            ?? throw new InvalidOperationException("No Agent Share login. Run `pks share init` first.");

        // Fresh access token from the stored refresh token (rotate + persist).
        var refresh = await _creds.DecryptRefreshAsync(cred, ct);
        var tok = await _oidc.RefreshAsync(cred.Issuer, cred.ClientId, refresh, ct);
        if (tok.RefreshToken != refresh)
            await _creds.SaveAsync(cred, tok.RefreshToken, ct);

        // Enroll an agent inbox owned by the user (owner resolved from the bearer).
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{cred.Host.TrimEnd('/')}/api/agents/enroll")
        {
            Content = JsonContent.Create(new { name = identity.Name, role = identity.Role }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"enroll failed {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        var inboxId = Get("inboxId");
        var token = Get("token");
        var mcpUrl = Get("mcpUrl");
        if (string.IsNullOrEmpty(mcpUrl)) mcpUrl = $"{cred.Host.TrimEnd('/')}/mcp";

        return new AgentRegistration(Name, cred.Host, inboxId, mcpUrl, token);
    }
}
