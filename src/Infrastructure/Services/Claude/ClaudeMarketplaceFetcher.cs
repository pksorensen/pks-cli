using System.Text.Json;

namespace PKS.Infrastructure.Services.Claude;

public class ClaudeMarketplaceFetcher : IClaudeMarketplaceFetcher
{
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ClaudeMarketplaceFetcher(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public ClaudeMarketplaceSource ParseSource(string input)
    {
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return new ClaudeMarketplaceSource { SourceType = "url", Url = input };
        }

        if (input.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input["github:".Length..];
            string repo;
            string @ref = "main";

            var atIdx = rest.IndexOf('@');
            if (atIdx >= 0)
            {
                repo = rest[..atIdx];
                @ref = rest[(atIdx + 1)..];
            }
            else
            {
                repo = rest;
            }

            return new ClaudeMarketplaceSource
            {
                SourceType = "github",
                Repo = repo,
                Ref = @ref
            };
        }

        // Default: treat as URL
        return new ClaudeMarketplaceSource { SourceType = "url", Url = input };
    }

    public async Task<MarketplaceJson> FetchAsync(ClaudeMarketplaceSource source)
    {
        string url = source.SourceType switch
        {
            "github" => $"https://raw.githubusercontent.com/{source.Repo}/{source.Ref ?? "main"}/marketplace.json",
            "url" => source.Url ?? throw new InvalidOperationException("URL source requires a URL"),
            _ => throw new NotSupportedException($"Source type '{source.SourceType}' is not supported for fetching")
        };

        var response = await _httpClient.GetStringAsync(url);
        var dto = JsonSerializer.Deserialize<MarketplaceJsonDto>(response, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse marketplace JSON");

        var plugins = (dto.Plugins ?? new List<MarketplacePluginDto>())
            .Select(p => new MarketplacePluginInfo(p.Name ?? "", p.Version, p.Description))
            .ToList();

        return new MarketplaceJson(dto.Id ?? "", dto.Label, plugins);
    }

    // Private DTOs for deserialization
    private class MarketplaceJsonDto
    {
        public string? Id { get; set; }
        public string? Label { get; set; }
        public List<MarketplacePluginDto>? Plugins { get; set; }
    }

    private class MarketplacePluginDto
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
    }
}
