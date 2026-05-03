namespace PKS.Infrastructure.Services.Claude;

public interface IClaudeMarketplaceConfigurationService
{
    Task<ClaudeMarketplaceConfiguration> LoadAsync();
    Task SaveAsync(ClaudeMarketplaceConfiguration config);
    Task<ClaudeMarketplace> AddOrUpdateMarketplaceAsync(ClaudeMarketplace marketplace);
    Task RemoveMarketplaceAsync(string id);
    Task<List<ClaudeMarketplace>> ListMarketplacesAsync();
    Task<ClaudeMarketplace?> FindMarketplaceAsync(string id);
}
