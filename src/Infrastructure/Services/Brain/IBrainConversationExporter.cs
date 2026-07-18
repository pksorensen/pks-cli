namespace PKS.Infrastructure.Services.Brain;

public interface IBrainConversationExporter
{
    Task<BrainConversationExportResult> ExportAsync(
        BrainConversationExportOptions options,
        CancellationToken ct = default);
}

public sealed class BrainConversationExportOptions
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public int MaxVisibleCharsPerBlock { get; init; } = 12_000;
    public bool IncludeIntermediateAssistantText { get; init; }
}

public sealed class BrainConversationExportResult
{
    public required string SessionId { get; init; }
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required string SourceSha256 { get; init; }
    public long SourceBytes { get; init; }
    public long SourceLines { get; init; }
    public int HumanMessages { get; init; }
    public int AssistantTextBlocks { get; init; }
    public int OmittedBlocks { get; init; }
}
