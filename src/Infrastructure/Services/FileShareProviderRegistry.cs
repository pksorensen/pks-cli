namespace PKS.Infrastructure.Services;

public class FileShareProviderRegistry
{
    private readonly IEnumerable<IFileShareProvider> _providers;

    public FileShareProviderRegistry(IEnumerable<IFileShareProvider> providers)
    {
        _providers = providers;
    }

    public IEnumerable<IFileShareProvider> GetAllProviders() => _providers;

    public async Task<IEnumerable<IFileShareProvider>> GetAuthenticatedProvidersAsync(CancellationToken ct = default)
    {
        var authenticated = new List<IFileShareProvider>();
        foreach (var provider in _providers)
        {
            if (await provider.IsAuthenticatedAsync())
                authenticated.Add(provider);
        }
        return authenticated;
    }
}
