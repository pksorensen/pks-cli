using System.Reflection;
using System.Text;

namespace PKS.Infrastructure.Services.Writing;

/// Reads the file-backed system-prompt template (embedded resource
/// `naturalness-extractor.system.md`) and substitutes <<PROFILE>>,
/// <<PATTERNS>>, <<SCHEMA>>. Mirrors the file-backed SKILL.md approach used
/// by [[BrainSkillReader]] so the operator can edit without recompiling.
public sealed class NaturalnessPromptBuilder : INaturalnessPromptBuilder
{
    private const string ResourceName = "naturalness-extractor.system.md";

    public async Task<NaturalnessPromptBundle> BuildAsync(
        NaturalnessPromptRequest request, CancellationToken ct = default)
    {
        var template = await LoadTemplateAsync(ct);

        var patternsBlock = RenderPatterns(request.Patterns);
        var schemaBlock = NaturalnessCandidatesSchema.SchemaExampleJson(request.MaxCandidates);

        var system = template
            .Replace("<<PROFILE>>", string.IsNullOrWhiteSpace(request.Profile)
                ? "(no writer profile configured — run `pks writing init`)"
                : request.Profile!.Trim())
            .Replace("<<PATTERNS>>", patternsBlock)
            .Replace("<<SCHEMA>>", schemaBlock);

        return new NaturalnessPromptBundle
        {
            System = system,
            User = BuildUser(request),
            Schema = NaturalnessCandidatesSchema.SchemaObject(request.MaxCandidates),
            Meta = new
            {
                source = request.SourcePath,
                maxCandidates = request.MaxCandidates,
                patternsIncluded = request.Patterns.Count,
                resource = ResourceName,
            },
        };
    }

    internal static string RenderPatterns(IReadOnlyList<Models.NaturalnessPattern> patterns)
    {
        if (patterns is null || patterns.Count == 0)
            return "(no accepted patterns yet — this is the first run; surface whatever you'd surface to a new author.)";

        var sb = new StringBuilder();
        foreach (var p in patterns)
        {
            sb.AppendLine($"- **trigger**: {p.TriggerSummary}  *(accepted {p.AcceptedCount}×)*");
            sb.AppendLine($"  - accepted: {p.AcceptedExample}");
            if (!string.IsNullOrWhiteSpace(p.RejectedExample))
                sb.AppendLine($"  - rejected: {p.RejectedExample}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildUser(NaturalnessPromptRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Source: {Path.GetFileName(r.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine("Apply the Naturalness review and return the JSON.");
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

    private static async Task<string> LoadTemplateAsync(CancellationToken ct)
    {
        // 1) override on disk wins
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var overrideCandidate = Path.Combine(home, ".pks-cli", "writing", ResourceName);
        if (File.Exists(overrideCandidate))
            return await File.ReadAllTextAsync(overrideCandidate, ct);

        // 2) embedded resource
        var asm = typeof(NaturalnessPromptBuilder).Assembly;
        await using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }

        // 3) fall back: read from source tree (dev runs without the resource embedded)
        var dev = Path.Combine(AppContext.BaseDirectory, "Resources", ResourceName);
        if (File.Exists(dev)) return await File.ReadAllTextAsync(dev, ct);

        throw new InvalidOperationException(
            $"Embedded resource '{ResourceName}' missing from pks-cli build.");
    }
}
