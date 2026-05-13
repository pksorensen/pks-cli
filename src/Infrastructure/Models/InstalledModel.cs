using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Models;

public sealed class InstalledModel
{
    [JsonPropertyName("name")]            public string Name { get; set; } = "";
    [JsonPropertyName("displayName")]     public string DisplayName { get; set; } = "";
    [JsonPropertyName("version")]         public string Version { get; set; } = "";
    [JsonPropertyName("capabilities")]    public List<string> Capabilities { get; set; } = new();
    [JsonPropertyName("languages")]       public List<string> Languages { get; set; } = new();
    [JsonPropertyName("installPath")]     public string InstallPath { get; set; } = "";
    [JsonPropertyName("installedAt")]     public DateTime InstalledAt { get; set; }
    [JsonPropertyName("sizeBytes")]       public long SizeBytes { get; set; }
    [JsonPropertyName("sherpaModelType")] public string SherpaModelType { get; set; } = "";
    [JsonPropertyName("files")]           public Dictionary<string, string> Files { get; set; } = new();
    [JsonPropertyName("source")]          public InstalledModelSource? Source { get; set; }
}

public sealed class InstalledModelSource
{
    [JsonPropertyName("downloadUrl")] public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")]      public string? Sha256 { get; set; }
}
