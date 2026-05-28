using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public interface INaturalnessPromptBuilder
{
    Task<NaturalnessPromptBundle> BuildAsync(NaturalnessPromptRequest request, CancellationToken ct = default);
}

public sealed class NaturalnessPromptRequest
{
    public required string SourcePath { get; init; }
    public required string Content { get; init; }
    public string? Profile { get; init; }
    public IReadOnlyList<NaturalnessPattern> Patterns { get; init; } = Array.Empty<NaturalnessPattern>();
    public int MaxCandidates { get; init; } = NaturalnessCandidatesSchema.MaxCandidates;
}

public sealed class NaturalnessPromptBundle
{
    public required string System { get; init; }
    public required string User { get; init; }
    public required object Schema { get; init; }
    public required object Meta { get; init; }
}
