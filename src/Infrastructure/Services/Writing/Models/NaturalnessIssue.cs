namespace PKS.Infrastructure.Services.Writing.Models;

/// One critic's wording of why a line is unnatural. Persisted in the merged
/// canonical sidecar so the `review` picker can display per-critic issues.
public sealed class NaturalnessIssue
{
    /// Critic name (e.g. "opus", "gpt5"). Always set in the merged file.
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";
}
