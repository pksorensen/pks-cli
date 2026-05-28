using Azure.Core;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Adapts <see cref="IAzureFoundryAuthService"/>'s refresh-token flow to the
/// Azure SDK's <see cref="TokenCredential"/> contract, so AzureOpenAIClient
/// can fetch Bearer tokens for Foundry-hosted cognitive services endpoints
/// without needing `az login` / DefaultAzureCredential.
/// </summary>
public sealed class FoundryTokenCredential : TokenCredential
{
    private readonly IAzureFoundryAuthService _auth;

    public FoundryTokenCredential(IAzureFoundryAuthService auth)
    {
        _auth = auth;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        return GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // requestContext.Scopes for cognitive services is ["https://cognitiveservices.azure.com/.default"].
        // The Foundry refresh flow expects a single scope string.
        var scope = requestContext.Scopes.Length > 0
            ? requestContext.Scopes[0]
            : "https://cognitiveservices.azure.com/.default";

        var token = await _auth.GetAccessTokenAsync(scope, cancellationToken);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                "Could not obtain Foundry access token. Run `pks foundry login` first.");
        }
        // We don't know the exact expiry without parsing the JWT — refresh-token
        // flow tokens are typically valid for ~1 hour. Return a conservative 50-minute
        // expiry to encourage refresh on long sessions.
        return new AccessToken(token, DateTimeOffset.UtcNow.AddMinutes(50));
    }
}
