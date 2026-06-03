using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Resolves the <see cref="IVmProvider"/> that owns a given VM record. Mirrors the
/// <c>FileShareProviderRegistry</c> pattern — injected with all registered providers.
/// </summary>
public class VmProviderRegistry
{
    private readonly IReadOnlyList<IVmProvider> _providers;

    public VmProviderRegistry(IEnumerable<IVmProvider> providers)
    {
        _providers = providers.ToList();
    }

    public IReadOnlyList<IVmProvider> GetAllProviders() => _providers;

    public IVmProvider Resolve(string providerKey)
    {
        var match = _providers.FirstOrDefault(p =>
            string.Equals(p.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new InvalidOperationException(
                $"No VM provider registered for '{providerKey}'. Known providers: {string.Join(", ", _providers.Select(p => p.ProviderKey))}.");
        return match;
    }

    public IVmProvider Resolve(AzureVmRecord record) =>
        Resolve(string.IsNullOrEmpty(record.Provider) ? "azure" : record.Provider);

    /// <summary>
    /// Merge locally-tracked records with live discovery from each authenticated
    /// provider, so VMs created outside <c>pks vm init</c> (e.g. in a cloud console)
    /// still appear. Local records win on conflict. Dedup key: provider + (ServerId or VmName).
    /// </summary>
    public async Task<List<AzureVmRecord>> MergeWithDiscoveryAsync(IEnumerable<AzureVmRecord> localRecords)
    {
        var merged = localRecords.ToList();
        var seen = new HashSet<string>(merged.Select(Key), StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            bool authed;
            try { authed = await provider.IsAuthenticatedAsync(); }
            catch { authed = false; }
            if (!authed) continue;

            IReadOnlyList<AzureVmRecord> discovered;
            try { discovered = await provider.DiscoverAsync(); }
            catch { continue; }

            foreach (var rec in discovered)
                if (seen.Add(Key(rec)))
                    merged.Add(rec);
        }

        return merged;
    }

    private static string Key(AzureVmRecord r)
    {
        var provider = string.IsNullOrEmpty(r.Provider) ? "azure" : r.Provider;
        var id = !string.IsNullOrEmpty(r.ServerId) ? r.ServerId : r.VmName;
        return $"{provider}:{id}";
    }
}
