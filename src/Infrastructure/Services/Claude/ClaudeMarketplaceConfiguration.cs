using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Claude;

public class ClaudeMarketplaceSource
{
    [JsonPropertyName("source")]
    public string SourceType { get; set; } = "url"; // url, github, git, directory, file

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("repo")]
    public string? Repo { get; set; }

    [JsonPropertyName("ref")]
    public string? Ref { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public class ClaudeMarketplacePluginSnapshot
{
    public string Name { get; set; } = "";
    public string? Version { get; set; }
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public bool Required { get; set; }
}

public class ClaudeMarketplace
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public ClaudeMarketplaceSource Source { get; set; } = new();
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastFetchedAt { get; set; }
    public List<ClaudeMarketplacePluginSnapshot> Plugins { get; set; } = new();
}

public class ClaudeMarketplaceConfiguration
{
    public List<ClaudeMarketplace> Marketplaces { get; set; } = new();
    public DateTime? LastModified { get; set; }
}
