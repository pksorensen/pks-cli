namespace PKS.Infrastructure.Services.Oidc;

/// RFC 8628 device authorization grant with RFC 7636 PKCE, against any issuer.
///
/// Extracted from `pks agentics init` the moment a second caller appeared:
/// `pks brain push --server <someone-elses-host>` has to be able to sign the
/// user in to an identity provider we have never seen. Two copies of a device
/// flow is two places to get PKCE wrong, and the first copy already had that
/// bug.
///
/// The service owns the protocol and nothing else — presenting the user code is
/// a callback, so the command layer keeps its Spectre panels and this stays
/// testable against a fake HTTP handler.
public interface IDeviceCodeLogin
{
    Task<OidcLoginResult> LoginAsync(DeviceLoginRequest request, CancellationToken ct = default);

    /// Same client, same token endpoint, `refresh_token` grant. Here rather
    /// than in its own service because the alternative is a second place that
    /// has to know the client_id and the resource indicator, and the two would
    /// drift.
    Task<OidcLoginResult> RefreshAsync(
        OidcEndpoints endpoints,
        string clientId,
        string refreshToken,
        string? resource,
        CancellationToken ct = default);
}

/// <param name="Endpoints">Where to talk. Discovered, never guessed.</param>
/// <param name="ClientId">
/// A CIMD URL for agentics.dk-aware providers, or whatever the operator handed
/// the user. No dynamic client registration: RFC 7591 never got adopted by the
/// providers people actually run, and a CLI that depends on it fails on the
/// majority of installs.
/// </param>
/// <param name="Scope">Space-separated. `offline_access` is what makes the daily daemon possible.</param>
/// <param name="Resource">
/// RFC 8707 resource indicator — the API this token is for. Providers that
/// implement it bind the audience, which is what keeps a token minted for one
/// receiver from being replayable against another. Keycloak ignores it today;
/// it is sent anyway because the cost is one form field and the alternative is
/// remembering to add it later.
/// </param>
/// <param name="OnUserCode">Called once, with the code and URL to show the human.</param>
public sealed record DeviceLoginRequest(
    OidcEndpoints Endpoints,
    string ClientId,
    string Scope,
    string? Resource,
    Action<DeviceCodePrompt> OnUserCode);

public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, string? VerificationUriComplete)
{
    /// What to actually put in front of the user: the complete URI carries the
    /// code already, so it turns a transcription into a click.
    public string BestUri => VerificationUriComplete ?? VerificationUri;
}
