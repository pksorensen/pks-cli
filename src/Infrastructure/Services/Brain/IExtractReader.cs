namespace PKS.Infrastructure.Services.Brain;

/// Parses one of the markdown extracts written by `pks brain extract` into a typed
/// record the synthesis pipeline can cluster on.
public interface IExtractReader
{
    Task<ParsedExtract?> ReadAsync(string mdFilePath, CancellationToken ct = default);
    Task<List<ParsedExtract>> ReadAllAsync(string extractsDir, CancellationToken ct = default);
}

public sealed class ParsedExtract
{
    public required string SessionId { get; set; }
    public required string FilePath { get; set; }
    /// First-line H1 minus the "Session <id> — " prefix.
    public string? Title { get; set; }
    public string? WhatWasWorkedOn { get; set; }
    public List<string> WhatWorked { get; set; } = new();
    public List<string> WhatStruggled { get; set; } = new();
    public List<string> Bottlenecks { get; set; } = new();
    public List<string> PromptObservations { get; set; } = new();
    public string? UserStory { get; set; }
    public List<string> Tags { get; set; } = new();
    /// Optional sidecar metadata (cost, tokens, model) if &lt;id&gt;.meta.json exists.
    public ExtractMetadata? Sidecar { get; set; }
}
