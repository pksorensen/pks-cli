using System.Security.Cryptography;
using System.Text;

namespace PKS.Infrastructure.Services.Brain;

/// Enumerates every brain skill the CLI knows about, so `pks brain skill list`
/// can present a static catalog without scattering string-typed skill names
/// across the codebase.
public interface IBrainSkillCatalog
{
    IReadOnlyList<BrainSkillEntry> AllSkills { get; }
    BrainSkillEntry? Get(string name);
}

public sealed class BrainSkillEntry
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Command { get; init; }   // e.g. "pks brain extract"
}

public sealed class BrainSkillCatalog : IBrainSkillCatalog
{
    public IReadOnlyList<BrainSkillEntry> AllSkills { get; } =
    [
        new()
        {
            Name = "brain-extract",
            DisplayName = "Per-session AI extract",
            Description = "Turn one session's deterministic summary into a markdown extract (what was worked on, what struggled, prompt-techniques, user story, tags).",
            Command = "pks brain extract",
        },
        new()
        {
            Name = "brain-synth-cluster",
            DisplayName = "Theme cluster summary",
            Description = "Synthesise a cluster of related sessions into one theme section in themes.md.",
            Command = "pks brain synth",
        },
        new()
        {
            Name = "brain-synth-habits",
            DisplayName = "Habits dedupe + rank",
            Description = "Deduplicate and rank prompt-technique observations across all sessions into bad-habits.md.",
            Command = "pks brain synth",
        },
        new()
        {
            Name = "brain-wiki-page",
            DisplayName = "Per-cluster wiki page",
            Description = "Render a cluster's extracts into a detailed wiki page (overview, user stories, what's built, open threads, hot files).",
            Command = "pks brain wiki",
        },
        new()
        {
            Name = "brain-adr",
            DisplayName = "Cluster → ADR",
            Description = "Distil a cluster of architectural sessions into a standard ADR (Status/Context/Decision/Alternatives/Consequences/Evidence).",
            Command = "pks brain adr",
        },
    ];

    public BrainSkillEntry? Get(string name) =>
        AllSkills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// Utility lifted out of pipelines so the skill subcommand can hash bodies the
/// same way pipelines hash them (first 8 bytes of SHA256 hex).
internal static class BrainHash
{
    public static string Short(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
