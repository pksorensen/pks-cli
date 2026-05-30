namespace PKS.Infrastructure.Services.Writing.Models;

/// One sentence the critic flags as a Naturalness defect, with three
/// alternative rewrites. Produced by `pks writing naturalness accept`,
/// consumed by `pks writing naturalness review`.
public sealed class NaturalnessCandidate
{
    public string Id { get; set; } = "";
    public int Line { get; set; }
    public string Original { get; set; } = "";
    public string Issue { get; set; } = "";
    public List<NaturalnessAlternative> Alternatives { get; set; } = new();

    /// Multi-critic extension fields used by the merged canonical sidecar.
    /// Null on per-critic sidecars (back-compat with the single-critic schema).
    public List<string>? CriticsFlagging { get; set; }
    public List<NaturalnessIssue>? Issues { get; set; }
}
