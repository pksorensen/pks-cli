using System.Reflection;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainSkillReader : IBrainSkillReader
{
    public Task<BrainSkillSource> ReadAsync(string? overridePath = null, CancellationToken ct = default) =>
        ReadAsync("brain-extract", overridePath, ct);

    public async Task<BrainSkillSource> ReadAsync(string skillName, string? overridePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            throw new ArgumentException("skillName required", nameof(skillName));

        if (overridePath is { Length: > 0 } op)
        {
            if (!File.Exists(op))
                throw new FileNotFoundException($"--skill-path file not found: {op}", op);
            return new BrainSkillSource(await File.ReadAllTextAsync(op, ct), op);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            Path.Combine(home, ".claude", "plugins", "pks-brain", "skills", skillName, "SKILL.md"),
            Path.Combine(home, ".claude", "skills", skillName, "SKILL.md"),
        ];
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return new BrainSkillSource(await File.ReadAllTextAsync(c, ct), c);
        }

        var resourceName = skillName + ".SKILL.md";
        var asm = typeof(BrainSkillReader).Assembly;
        await using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded skill '{resourceName}' missing from pks-cli build.");
        using var reader = new StreamReader(stream);
        return new BrainSkillSource(await reader.ReadToEndAsync(ct), $"embedded:{skillName}");
    }
}
