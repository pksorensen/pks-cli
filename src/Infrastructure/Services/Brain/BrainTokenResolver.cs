using PKS.Infrastructure.Services.Agentics;

namespace PKS.Infrastructure.Services.Brain;

/// Finds the credential `brain push` should present.
///
/// Deliberately *not* `IAgenticsAuthService.GetTokenAsync`: that chain tries a
/// GitHub Actions OIDC token second, and the brain API classifies a GitHub OIDC
/// bearer as an opaque upload token it has never seen — a 401 that looks like a
/// broken account rather than a wrong credential. CI therefore sets
/// `PKS_BRAIN_TOKEN` to a minted `bkt_` token and gets a deterministic answer.
public interface IBrainTokenResolver
{
    Task<BrainToken?> ResolveAsync(string? explicitToken, string endpoint, CancellationToken ct = default);
}

/// <param name="Value">The bearer value.</param>
/// <param name="Origin">Where it came from, for the CLI to print. Never the token.</param>
/// <param name="Warning">A mismatch worth showing, e.g. credentials for another host.</param>
public sealed record BrainToken(string Value, string Origin, string? Warning = null);

public sealed class BrainTokenResolver(
    IAgenticsAuthConfigurationService authConfig,
    IAgenticsAuthService auth) : IBrainTokenResolver
{
    public const string EnvVar = "PKS_BRAIN_TOKEN";

    public async Task<BrainToken?> ResolveAsync(string? explicitToken, string endpoint, CancellationToken ct = default)
    {
        if (explicitToken is { Length: > 0 }) return new BrainToken(explicitToken, "--token");

        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (fromEnv is { Length: > 0 }) return new BrainToken(fromEnv.Trim(), EnvVar);

        var creds = await authConfig.LoadAsync();
        if (creds is null || string.IsNullOrEmpty(creds.AccessToken)) return null;

        var warning = HostMismatch(creds.Server, endpoint);

        if (!creds.IsExpired) return new BrainToken(creds.AccessToken, "pks agentics init", warning);

        // Expired: hand it to the auth service, whose refresh saves the new
        // tokens back. Passing an audience it can use keeps that path unchanged.
        var refreshed = await auth.GetTokenAsync(endpoint, null, "", "");
        _ = ct;

        return refreshed is { Length: > 0 }
            ? new BrainToken(refreshed, "pks agentics init (refreshed)", warning)
            : null;
    }

    /// The stored credential names the host it was issued for. Pushing it
    /// somewhere else would hand a self-hosted instance a token for agentics.dk.
    private static string? HostMismatch(string? credentialServer, string endpoint)
    {
        if (string.IsNullOrEmpty(credentialServer)) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return null;

        var credentialHost = credentialServer.Contains("://")
            ? new Uri(credentialServer).Host
            : credentialServer.Trim('/');

        return string.Equals(credentialHost, uri.Host, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Your stored credentials were issued for {credentialHost}, but you are pushing to {uri.Host}. "
              + $"Mint an upload token on that host and set {EnvVar}, or pass --token.";
    }
}
