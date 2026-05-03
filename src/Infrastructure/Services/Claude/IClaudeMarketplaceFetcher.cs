namespace PKS.Infrastructure.Services.Claude;

public record MarketplaceJson(
    string Id,
    string? Label,
    List<MarketplacePluginInfo> Plugins);

public record MarketplacePluginInfo(string Name, string? Version, string? Description);

public interface IClaudeMarketplaceFetcher
{
    ClaudeMarketplaceSource ParseSource(string input);
    Task<MarketplaceJson> FetchAsync(ClaudeMarketplaceSource source);
}
