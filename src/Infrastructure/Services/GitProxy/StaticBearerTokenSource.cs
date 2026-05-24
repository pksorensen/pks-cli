namespace PKS.Infrastructure.Services.GitProxy;

/// <summary>
/// Token source that always returns the same pre-issued Bearer JWT. Used for
/// marketplace flows where agentic-live has already negotiated a token via
/// OAuth client_credentials / authorization_code and shipped it to the runner
/// as part of the job spec.
///
/// No refresh — when the token expires the proxy will start returning 401
/// upstream responses to the container, which is the right signal that the
/// caller needs to re-mint and re-register.
/// </summary>
public sealed class StaticBearerTokenSource : IGitProxyTokenSource
{
    private readonly string _token;

    public StaticBearerTokenSource(string token)
    {
        // Empty is valid — signals "rewrite upstream, no auth". Null is not.
        _token = token ?? throw new ArgumentNullException(nameof(token));
    }

    public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(_token);
}
