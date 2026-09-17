using global::Azure.Core;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Adapts <see cref="IAzureFoundryAuthService"/>'s refresh-token flow to the
/// Azure SDK's <see cref="TokenCredential"/> contract, so AzureOpenAIClient
/// can fetch Bearer tokens for Foundry-hosted cognitive services endpoints
/// without needing `az login` / DefaultAzureCredential.
///
/// Caching (process-wide L1 plus the cross-process on-disk L2 that collapses a
/// fan-out of short-lived <c>pks</c> processes into one refresh per lifetime)
/// lives in <see cref="AzureTokenCache"/>, under the key <c>"foundry"</c>. The
/// single-argument constructor uses <see cref="AzureTokenCache.Default"/>, so
/// every hand-constructed instance still shares one L1 as it did when the
/// cache fields were static here.
/// </summary>
public sealed class FoundryTokenCredential : TokenCredential
{
    /// <summary>The cache key for the single Foundry credential (<c>foundry.auth.credentials</c>).</summary>
    public const string CacheKey = "foundry";

    private readonly IAzureFoundryAuthService _auth;
    private readonly AzureTokenCache _cache;

    public FoundryTokenCredential(IAzureFoundryAuthService auth)
        : this(auth, AzureTokenCache.Default)
    {
    }

    public FoundryTokenCredential(IAzureFoundryAuthService auth, AzureTokenCache cache)
    {
        _auth = auth;
        _cache = cache;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var scope = requestContext.Scopes.Length > 0
            ? requestContext.Scopes[0]
            : "https://cognitiveservices.azure.com/.default";

        var (token, expiresOn) = await _cache.GetOrRefreshWithExpiryAsync(CacheKey, scope, async ct =>
        {
            var fresh = await _auth.GetAccessTokenAsync(scope, ct);
            if (string.IsNullOrEmpty(fresh))
            {
                throw new InvalidOperationException(
                    "Could not obtain Foundry access token. Run `pks foundry login` first.");
            }
            // The cache caps this at its own lifetime (50 min), which is what this type always used.
            return (fresh, DateTimeOffset.UtcNow.AddHours(1));
        }, cancellationToken);

        return new AccessToken(token, expiresOn);
    }
}
