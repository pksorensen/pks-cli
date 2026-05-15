namespace PKS.Infrastructure.Services.Brain;

/// Locates a brain skill's SKILL.md body. Override hierarchy (first hit wins):
///   1. Explicit path passed in `overridePath`
///   2. ~/.claude/plugins/pks-brain/skills/&lt;name&gt;/SKILL.md  (marketplace install)
///   3. ~/.claude/skills/&lt;name&gt;/SKILL.md                   (user copy)
///   4. Embedded resource (always present — the shipped default)
///
/// The user can `cp` the embedded default to (2) or (3) and edit it to tune any
/// skill behavior without rebuilding pks-cli.
public interface IBrainSkillReader
{
    /// Convenience overload kept for backward compat with brain-extract callers.
    Task<BrainSkillSource> ReadAsync(string? overridePath = null, CancellationToken ct = default);

    /// Read a named skill (e.g. "brain-extract", "brain-synth-cluster", "brain-synth-habits").
    Task<BrainSkillSource> ReadAsync(string skillName, string? overridePath, CancellationToken ct = default);
}

public sealed record BrainSkillSource(string Body, string Source);
