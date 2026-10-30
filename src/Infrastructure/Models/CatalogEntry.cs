namespace PKS.Infrastructure.Models;

/// <summary>How the download is laid out.</summary>
public enum CatalogEntryKind
{
    /// <summary>A .tar.bz2 with one top-level directory holding <see cref="CatalogEntry.Files"/>.</summary>
    TarBz2,
    /// <summary>The download IS the single file named by the one entry in <see cref="CatalogEntry.Files"/>.</summary>
    SingleFile,
}

public sealed class CatalogEntry
{
    public CatalogEntryKind Kind { get; init; } = CatalogEntryKind.TarBz2;
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
