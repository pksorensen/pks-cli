namespace PKS.Infrastructure.Models;

public sealed class CatalogEntry
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required long ExpectedSizeBytes { get; init; }
    public required string SherpaModelType { get; init; }
    public required IReadOnlyDictionary<string, string> Files { get; init; }
    public required string DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
}
