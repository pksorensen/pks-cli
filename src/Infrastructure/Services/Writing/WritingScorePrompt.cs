using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Builds the structured score prompt + its strict reply schema. The pks-cli
/// command `pks writing prompt` calls this and emits the result; an agent
/// reads it, calls *its own* LLM (we do NOT spawn one), then submits the
/// reply via `pks writing accept` which validates against [[WritingScoreSchema]].
public static class WritingScorePrompt
{
    public sealed class Request
    {
        public required string SourcePath { get; init; }
        public required string Content { get; init; }
        public required string Channel { get; init; }
        public string? Profile { get; init; }
        public string? ChannelRubric { get; init; }
        public IReadOnlyList<ReferenceSample> References { get; init; } = Array.Empty<ReferenceSample>();
        public IReadOnlyList<AnglicismEntry> Anglicisms { get; init; } = Array.Empty<AnglicismEntry>();
        public int MaxReferences { get; init; } = 10;
        public int MaxAnglicisms { get; init; } = 60;
        public int MaxFindings { get; init; } = 12;
    }

    /// Bundle: system+user prompt + the strict reply schema the agent must follow.
    public sealed class Bundle
    {
        public required string System { get; init; }
        public required string User { get; init; }
        public required object Schema { get; init; }
        public required object Meta { get; init; }
    }

    public static Bundle Build(Request r)
    {
        return new Bundle
        {
            System = BuildSystem(r),
            User = BuildUser(r),
            Schema = WritingScoreSchema.SchemaObject(r.MaxFindings),
            Meta = new
            {
                source = r.SourcePath,
                channel = r.Channel,
                modelHint = "haiku",   // BGA-style — pattern matching, not deliberation
                maxFindings = r.MaxFindings,
                referencesIncluded = Math.Min(r.References.Count, r.MaxReferences),
                anglicismsIncluded = Math.Min(r.Anglicisms.Count, r.MaxAnglicisms),
            },
        };
    }

    public static string BuildSystem(Request r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a strict but kind Danish-writing critic for the `pks writing` tool.");
        sb.AppendLine("You apply a fixed rubric to one piece of writing and return ONE JSON object.");
        sb.AppendLine("You do NOT rewrite the post. You flag and grade. The author rewrites.");
        sb.AppendLine();
        sb.AppendLine("# Rubric — score each dimension 1 (worst) to 5 (best)");
        sb.AppendLine();
        sb.AppendLine("- **Naturalness** — does the Danish read native, or like a translation from English?");
        sb.AppendLine("- **Tone**        — does the voice match the writer profile below?");
        sb.AppendLine("- **Terminology** — anglicisms, kalkerede vendinger, on-brand vocabulary.");
        sb.AppendLine("- **Hook**        — does the opening sentence earn the read?");
        sb.AppendLine("- **Value**       — does the post deliver what the headline/opening promises?");
        sb.AppendLine();
        sb.AppendLine("Be honest. A 3 is competent. A 5 is rare. Never give all 5s out of politeness.");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(r.Profile))
        {
            sb.AppendLine("# Writer profile");
            sb.AppendLine();
            sb.AppendLine(r.Profile.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(r.ChannelRubric))
        {
            sb.AppendLine($"# Channel rubric ({r.Channel})");
            sb.AppendLine();
            sb.AppendLine(r.ChannelRubric.Trim());
            sb.AppendLine();
        }

        if (r.References.Count > 0)
        {
            sb.AppendLine("# Reference samples — this is what the author actually sounds like");
            sb.AppendLine();
            foreach (var sample in r.References.Take(r.MaxReferences))
            {
                sb.AppendLine($"## reference: {sample.Id}");
                sb.AppendLine();
                sb.AppendLine(Truncate(sample.Content.Trim(), 1500));
                sb.AppendLine();
            }
        }

        if (r.Anglicisms.Count > 0)
        {
            sb.AppendLine("# Known anglicisms (terminology hint — the linter already flags these)");
            sb.AppendLine();
            foreach (var a in r.Anglicisms.Take(r.MaxAnglicisms))
            {
                var alts = string.Join(", ", a.DanishAlternatives);
                sb.AppendLine($"- {a.English} → {alts}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"# Output format — return ONLY this JSON, no prose. Cap findings at {r.MaxFindings}.");
        sb.AppendLine();
        sb.AppendLine(WritingScoreSchema.SchemaExampleJson(r.MaxFindings));
        sb.AppendLine();
        sb.AppendLine("Pick the findings that move the needle most. Skip nitpicks.");
        return sb.ToString();
    }

    public static string BuildUser(Request r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Source: {Path.GetFileName(r.SourcePath)}  (channel: {r.Channel})");
        sb.AppendLine();
        sb.AppendLine("Apply the rubric to the post below and return the JSON.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        var lines = r.Content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(4));
            sb.Append("  ");
            sb.AppendLine(lines[i]);
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
