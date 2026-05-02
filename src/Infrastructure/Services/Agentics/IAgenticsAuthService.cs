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
}
