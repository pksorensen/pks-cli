namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// Where the Log Analytics and Application Insights query services get a bearer token for one
/// registered resource.
///
/// The tenant-keyed store is the source: the entry's own tenant, or the only signed-in tenant for
/// entries lifted from the legacy single-resource keys (those never recorded one). The Foundry
/// credential is a fallback for exactly one situation — the store knows no tenant at all — so a
/// user upgraded from the pre-tenant-store world keeps querying until they run <c>init --reauth</c>.
/// It is never consulted once a tenant is signed in, because then it would silently query one
/// tenant's workspace with another tenant's token.
/// </summary>
public sealed class AzureQueryTokenProvider
{
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAzureFoundryAuthService _foundry;
    private readonly string _initCommand;

    /// <param name="initCommand">The command that registers and signs in for this vertical
    /// (<c>pks loganalytics init</c> / <c>pks appinsights init</c>), used in error messages.</param>
    public AzureQueryTokenProvider(IAzureTenantCredentialStore tenants, IAzureFoundryAuthService foundry, string initCommand)
    {
        _tenants = tenants;
        _foundry = foundry;
        _initCommand = initCommand;
    }

    /// <summary>A bearer token for <paramref name="scope"/> valid in <paramref name="resource"/>'s
    /// tenant. Throws <see cref="AzureAuthExpiredException"/> when that tenant's sign-in is dead
    /// and <see cref="InvalidOperationException"/> when nothing is signed in at all.</summary>
    public async Task<string> GetTokenAsync(AzureResourceEntry resource, string scope, CancellationToken ct = default)
    {
        var known = await _tenants.ListTenantsAsync();
        if (known.Count == 0)
        {
            var legacy = await _foundry.GetAccessTokenAsync(scope, ct);
            return legacy ?? throw new InvalidOperationException(
                $"Not authenticated. Run '{_initCommand}' to sign in.");
        }

        string tenantId;
        try
        {
            tenantId = await _tenants.ResolveTenantAsync(resource.TenantId);
        }
        catch (InvalidOperationException ex) when (string.IsNullOrWhiteSpace(resource.TenantId))
        {
            // The store's own message says "pass --tenant", which the query commands do not have.
            throw new InvalidOperationException(
                $"'{resource.Name}' has no tenant recorded and {known.Count} tenants are signed in. " +
                $"Run '{_initCommand}' to register it again with its tenant.", ex);
        }

        return await _tenants.GetAccessTokenAsync(tenantId, scope, ct);
    }
}
