namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// Resolves a bearer token for authenticating against agentics.dk APIs.
/// Tries multiple sources (CI OIDC, stored user OAuth, runner registration)
/// in priority order.
/// </summary>
public interface IAgenticsAuthService
{
    /// <summary>
    /// Returns a bearer token suitable for the given resource URL, or null if
    /// no source can provide one. Implementations request OIDC tokens whose
    /// `aud` claim equals <paramref name="audience"/>.
    /// </summary>
    /// <param name="audience">
    /// The exact resource URL the token will be presented to. For task
    /// submission this is the assembly-line URL; for runner registration this
    /// is the project URL.
    /// </param>
    /// <param name="explicitToken">
    /// When non-null, returned verbatim — used by `--token &lt;bearer&gt;` CLI flag.
    /// </param>
    /// <param name="owner">Owner slug for runner-token fallback lookup.</param>
    /// <param name="project">Project slug for runner-token fallback lookup.</param>
    Task<string?> GetTokenAsync(string audience, string? explicitToken, string owner, string project);

    /// <summary>
    /// The same chain minus the runner-token fallback: explicit token, GitHub Actions
    /// OIDC, then the stored user credential — a token that says who the human is.
    ///
    /// Separate because "no credential at all" and "a runner token that this endpoint
    /// refuses" are the same answer from <see cref="GetTokenAsync"/> and different
    /// problems: only the first one is fixed by signing in. Runner registration asks
    /// this before deciding whether to offer the device grant.
    /// </summary>
    Task<string?> GetUserTokenAsync(string audience, string? explicitToken);

    /// <summary>
    /// Redeems the stored refresh token even when the access token still looks
    /// valid locally, and saves the result. Returns null when there is nothing
    /// to refresh or the provider refused.
    ///
    /// Needed because "locally valid" and "the server accepts it" are different
    /// claims: after a 401 the only useful move is a new token, and
    /// <see cref="GetTokenAsync"/> would hand back the rejected one.
    /// </summary>
    Task<string?> ForceRefreshAsync(CancellationToken ct = default);
}
