using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Discovery;
using PKS.Infrastructure.Services.Oidc;

namespace PKS.Infrastructure.Services.Brain;

/// Finds the credential `brain push` should present — to any receiver, not just
/// agentics.dk.
///
/// Deliberately *not* `IAgenticsAuthService.GetTokenAsync`: that chain tries a
/// GitHub Actions OIDC token second, and the brain API classifies a GitHub OIDC
/// bearer as an opaque upload token it has never seen — a 401 that looks like a
/// broken account rather than a wrong credential. CI therefore sets
/// `PKS_BRAIN_TOKEN` to a minted `bkt_` token and gets a deterministic answer.
///
/// The resolution order is a trust order, not a convenience order:
///
///   1. `--token`            the user said exactly what to use
///   2. `PKS_BRAIN_TOKEN`    the machine was configured to use exactly this
///   3. `pks agentics init`  but ONLY for the host it was issued for
///   4. per-issuer store     a previous login to this receiver's provider
///   5. interactive login    device grant, then browser, if the caller allows it
///
/// Step 3's host check used to be a warning. It is now a hard rule: warning and
/// then sending an agentics.dk access token to `--server evil.example.com`
/// hands that host a working credential for the user's real account, and no
/// amount of yellow text undoes it. A mismatch drops through to step 4 instead.
public interface IBrainTokenResolver
{
    Task<BrainToken?> ResolveAsync(BrainTokenRequest request, CancellationToken ct = default);
}

/// <param name="ExplicitToken">`--token`, if given.</param>
/// <param name="Endpoint">Where the token is going. Its `Resource` is what a credential is bound to.</param>
/// <param name="AllowInteractiveLogin">
/// False for the daemon and for CI: an unattended run that stops to print a
/// device code hangs forever, which is worse than failing.
/// </param>
/// <param name="ClientId">
/// Overrides the CIMD client_id. Needed for providers that neither accept a
/// client-id URL nor let the user register one — the operator hands out an ID
/// and this carries it.
/// </param>
/// <param name="OnPrompt">Shows the device code. Required when interactive login is allowed.</param>
/// <param name="OnAuthorizeUrl">
/// Shows the browser URL for the loopback fallback. Null means "no browser here",
/// which is the right answer for a daemon and leaves the device grant as the
/// only path.
/// </param>
/// <param name="ForceRefresh">
/// Skip the "still valid locally" shortcut and go straight to the refresh token.
/// Set after a 401: the server has just voted on that access token, and its vote
/// beats our arithmetic. Clock skew, a token revoked early, or an `ExpiresAt` the
/// provider did not honour all look identical from here — locally valid, remotely
/// dead — and without this flag the renewal hands back the very token that was
/// rejected, so a push that outlives its token can never recover.
/// </param>
public sealed record BrainTokenRequest(
    string? ExplicitToken,
    BrainEndpoint Endpoint,
    bool AllowInteractiveLogin = false,
    string? ClientId = null,
    Action<DeviceCodePrompt>? OnPrompt = null,
    Action<string>? OnAuthorizeUrl = null,
    bool ForceRefresh = false);

/// <param name="Value">The bearer value.</param>
/// <param name="Origin">Where it came from, for the CLI to print. Never the token.</param>
/// <param name="Warning">Something worth showing that did not stop the push.</param>
public sealed record BrainToken(string Value, string Origin, string? Warning = null);

public sealed class BrainTokenResolver(
    IAgenticsAuthConfigurationService authConfig,
    IAgenticsAuthService auth,
    IOidcDiscovery discovery,
    IIssuerCredentialStore issuerStore,
    IDeviceCodeLogin deviceLogin,
    ILoopbackAuthCodeLogin loopbackLogin) : IBrainTokenResolver
{
    public const string EnvVar = "PKS_BRAIN_TOKEN";

    /// The client_id pks-cli presents by default: a Client ID Metadata Document
    /// (CIMD) URL. Providers that support CIMD fetch it and need no
    /// registration; providers that do not will reject it, and the user passes
    /// `--client-id`. Dynamic client registration is not attempted — it is a
    /// standard almost nobody enabled.
    public const string DefaultClientId = "https://agentics.dk/oauth/clients/pks-cli.json";

    private const string Scope = "openid profile email offline_access";

    public async Task<BrainToken?> ResolveAsync(BrainTokenRequest request, CancellationToken ct = default)
    {
        // Steps 1 and 2 ignore `ForceRefresh`: there is nothing to refresh. A
        // token the caller named by hand or by environment has no refresh token
        // behind it, so re-resolving after a 401 returns the same value and the
        // 401 stands — which is the truthful answer, because that token really
        // was rejected.
        if (request.ExplicitToken is { Length: > 0 } explicitToken)
            return new BrainToken(explicitToken, "--token");

        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (fromEnv is { Length: > 0 }) return new BrainToken(fromEnv.Trim(), EnvVar);

        var endpoint = request.Endpoint;

        // 3. The single-login credential, if it belongs to this host.
        var creds = await authConfig.LoadAsync();
        if (creds is { AccessToken.Length: > 0 } && HostOf(creds.Server) == HostOf(endpoint.Origin))
        {
            if (!creds.IsExpired && !request.ForceRefresh)
                return new BrainToken(creds.AccessToken, "pks agentics init");

            var refreshed = request.ForceRefresh
                ? await auth.ForceRefreshAsync(ct)
                : await auth.GetTokenAsync(endpoint.Origin, null, "", "");

            if (refreshed is { Length: > 0 })
                return new BrainToken(refreshed, "pks agentics init (refreshed)");
        }

        // 4/5. Anything else needs to know which authorization server this
        //      receiver trusts, which is exactly what the PRM says.
        var prm = await discovery.ProtectedResourceAsync(endpoint.ApiBase, ct);
        var issuer = prm?.AuthorizationServers.FirstOrDefault();
        if (string.IsNullOrEmpty(issuer))
        {
            // No PRM, or a PRM with no authorization server: the receiver has
            // not said how to log in, so there is nothing to do but ask for a
            // token. Reported by the caller, which knows how to say it.
            return null;
        }

        // The resource the token will be bound to. Prefer the receiver's own
        // spelling — trailing slashes and casing have to match on comparison —
        // but only when it points back at the API we are actually calling.
        var resource = prm!.Resource is { Length: > 0 } r && SameOrigin(endpoint.Origin, r) ? r : endpoint.Resource;

        var stored = await issuerStore.LoadAsync(issuer, ct);
        if (stored is { AccessToken.Length: > 0 } && ResourceMatches(stored.Resource, resource))
        {
            if (!stored.IsExpired && !request.ForceRefresh)
                return new BrainToken(stored.AccessToken, $"saved login ({HostOf(issuer)})");

            if (stored.RefreshToken is { Length: > 0 } &&
                await discovery.EndpointsAsync(issuer, ct) is { } endpointsForRefresh)
            {
                var refreshed = await deviceLogin.RefreshAsync(
                    endpointsForRefresh, stored.ClientId, stored.RefreshToken, resource, ct);
                if (refreshed.Ok)
                {
                    stored.AccessToken = refreshed.AccessToken!;
                    // Keycloak returns a rotated refresh token; providers with
                    // rotation off return none. Overwriting unconditionally
                    // would erase the only credential that can renew anything,
                    // turning the second refresh into a fresh device login.
                    stored.RefreshToken = refreshed.RefreshToken ?? stored.RefreshToken;
                    stored.IdToken = refreshed.IdToken ?? stored.IdToken;
                    stored.ExpiresAt = refreshed.ExpiresAtUnix;
                    await issuerStore.SaveAsync(stored, ct);

                    return new BrainToken(stored.AccessToken, $"saved login ({HostOf(issuer)}, refreshed)");
                }
            }
            // Fall through: the saved login is dead, so log in again if allowed.
        }

        if (!request.AllowInteractiveLogin || request.OnPrompt is null) return null;

        var endpoints = await discovery.EndpointsAsync(issuer, ct);
        if (endpoints is null) return null;

        // `--client-id` wins, then whatever the receiver advertised for us, then
        // our own CIMD URL. The middle step is the one that makes a self-hosted
        // receiver work at all: our document names agentics.dk, and a CIMD
        // document whose client_id is not the URL it was fetched from is invalid
        // — so only the receiver can name an id its own issuer will accept.
        var clientId = request.ClientId ?? endpoint.ClientId ?? DefaultClientId;

        var result = await LoginAsync(endpoints, clientId, resource, request, ct);
        if (result is null || !result.Ok) return null;

        await issuerStore.SaveAsync(new IssuerCredentials
        {
            Issuer = endpoints.Issuer,
            Resource = resource,
            ClientId = clientId,
            AccessToken = result.AccessToken!,
            RefreshToken = result.RefreshToken,
            IdToken = result.IdToken,
            ExpiresAt = result.ExpiresAtUnix,
        }, ct);

        return new BrainToken(result.AccessToken!, $"login ({HostOf(issuer)})");
    }

    /// Device grant first, browser second.
    ///
    /// The device grant is the better flow for a CLI — it works over SSH, and
    /// the loopback redirect does not — so it gets the first attempt whenever
    /// the provider advertises it. But Keycloak wires its CIMD support into the
    /// authorization endpoint only: a client-id-URL presented at the device
    /// endpoint comes back `invalid_client` before any human sees anything.
    /// That failure is cheap and recognisable, so it falls through to the
    /// browser instead of ending the login.
    ///
    /// The fallback is deliberately gated on `prompted`: once a user code has
    /// been shown, a failure means the human declined, cancelled or ran out of
    /// time, and opening a browser at them after that is the wrong answer.
    private async Task<OidcLoginResult?> LoginAsync(
        OidcEndpoints endpoints,
        string clientId,
        string resource,
        BrainTokenRequest request,
        CancellationToken ct)
    {
        if (endpoints.SupportsDeviceGrant)
        {
            var prompted = false;
            var device = await deviceLogin.LoginAsync(
                new DeviceLoginRequest(endpoints, clientId, Scope, resource, p =>
                {
                    prompted = true;
                    request.OnPrompt!(p);
                }), ct);

            if (device.Ok || prompted) return device;
        }

        if (request.OnAuthorizeUrl is null) return null;

        return await loopbackLogin.LoginAsync(new LoopbackLoginRequest(
            endpoints,
            clientId,
            Scope,
            resource,
            LoopbackLoginRequest.DefaultPorts,
            LoopbackLoginRequest.DefaultPath,
            request.OnAuthorizeUrl), ct);
    }

    /// A credential saved before `Resource` was recorded has an empty one; it is
    /// accepted rather than discarded, because the alternative is logging every
    /// existing user out to gain a field.
    private static bool ResourceMatches(string stored, string wanted)
        => string.IsNullOrEmpty(stored)
           || string.Equals(stored.TrimEnd('/'), wanted.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool SameOrigin(string origin, string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && string.Equals($"{u.Scheme}://{u.Authority}", origin, StringComparison.OrdinalIgnoreCase);

    /// Accepts both spellings the config has used over time: a bare host
    /// ("agentics.dk") and a full URL.
    private static string HostOf(string serverOrUrl)
    {
        if (string.IsNullOrEmpty(serverOrUrl)) return "";

        return serverOrUrl.Contains("://") && Uri.TryCreate(serverOrUrl, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : serverOrUrl.Trim('/').ToLowerInvariant();
    }
}
