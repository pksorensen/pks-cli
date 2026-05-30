using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

public sealed class PersonaLinter : IPersonaLinter
{
    private static readonly HashSet<string> KnownBuckets = new(StringComparer.Ordinal)
    {
        "developer", "decision-maker", "builder", "in-transition", "executive",
    };

    /// <summary>
    /// Required heading text per locale. Keep aligned with the persona md
    /// files we authored under <c>personas/&lt;locale&gt;/</c>.
    /// </summary>
    private static readonly Dictionary<string, string[]> RequiredSectionsByLocale = new(StringComparer.Ordinal)
    {
        ["da"] = new[] { "Beskrivelse", "Opgaver", "Behov", "Smertepunkter" },
        ["en"] = new[] { "Description", "Tasks", "Needs", "Pain Points" },
    };

    private const int MinBullets = 3;
    private const int MaxBullets = 8;
    private const int MinDescriptionChars = 80;
    private const int MaxDescriptionChars = 800;

    public async Task<PersonaLintResult> LintAsync(string personaPath, CancellationToken ct = default)
    {
        var result = new PersonaLintResult { SourcePath = Path.GetFullPath(personaPath) };
        if (!File.Exists(personaPath))
        {
            result.Errors.Add(new PersonaLintError
            {
                Field = "path",
                Code = "not-found",
                Message = $"persona file not found: {personaPath}",
            });
            return result;
        }

        var raw = await File.ReadAllTextAsync(personaPath, ct);
        var parsed = FrontmatterParser.Parse(raw);
        var fields = parsed.Fields;

        // ── frontmatter ─────────────────────────────────────────────────
        RequireString(fields, "id", result);
        RequireString(fields, "name", result);
        RequireString(fields, "segment", result);
        var bucket = FrontmatterParser.GetString(fields, "bucket");
        if (string.IsNullOrWhiteSpace(bucket))
        {
            result.Errors.Add(new PersonaLintError { Field = "bucket", Code = "missing", Message = "frontmatter field 'bucket' is required" });
        }
        else if (!KnownBuckets.Contains(bucket))
        {
            result.Errors.Add(new PersonaLintError
            {
                Field = "bucket",
                Code = "invalid-enum",
                Message = $"bucket '{bucket}' is not one of: {string.Join(", ", KnownBuckets)}",
            });
        }

        var lang = FrontmatterParser.GetString(fields, "lang") ?? "";
        if (string.IsNullOrWhiteSpace(lang))
        {
            result.Errors.Add(new PersonaLintError { Field = "lang", Code = "missing", Message = "frontmatter field 'lang' is required (da or en)" });
        }

        // Filename slug should match id
        var slug = Path.GetFileNameWithoutExtension(personaPath);
        var id = FrontmatterParser.GetString(fields, "id");
        if (!string.IsNullOrEmpty(id) && !string.Equals(id, slug, StringComparison.Ordinal))
        {
            result.Warnings.Add(new PersonaLintError
            {
                Field = "id",
                Code = "slug-mismatch",
                Message = $"frontmatter id '{id}' does not match filename slug '{slug}'",
            });
        }

        // ── body sections ───────────────────────────────────────────────
        var sections = FrontmatterParser.ParseSections(parsed.Body);
        if (!string.IsNullOrWhiteSpace(lang) && RequiredSectionsByLocale.TryGetValue(lang, out var required))
        {
            foreach (var section in required)
            {
                if (!sections.TryGetValue(section, out var body) || string.IsNullOrWhiteSpace(body))
                {
                    result.Errors.Add(new PersonaLintError
                    {
                        Field = $"sections.{section}",
                        Code = "missing-section",
                        Message = $"required body section '## {section}' is missing or empty",
                    });
                    continue;
                }

                // First section ("Beskrivelse" / "Description") is prose; rest are bullets.
                if (string.Equals(section, required[0], StringComparison.Ordinal))
                {
                    var chars = body.Trim().Length;
                    if (chars < MinDescriptionChars)
                    {
                        result.Warnings.Add(new PersonaLintError
                        {
                            Field = $"sections.{section}",
                            Code = "section-too-short",
                            Message = $"'## {section}' is {chars} chars (recommend ≥ {MinDescriptionChars})",
                        });
                    }
                    else if (chars > MaxDescriptionChars)
                    {
                        result.Warnings.Add(new PersonaLintError
                        {
                            Field = $"sections.{section}",
                            Code = "section-too-long",
                            Message = $"'## {section}' is {chars} chars (recommend ≤ {MaxDescriptionChars})",
                        });
                    }
                }
                else
                {
                    var bullets = FrontmatterParser.CountBullets(body);
                    if (bullets < MinBullets)
                    {
                        result.Warnings.Add(new PersonaLintError
                        {
                            Field = $"sections.{section}",
                            Code = "too-few-bullets",
                            Message = $"'## {section}' has {bullets} bullets (recommend ≥ {MinBullets})",
                        });
                    }
                    else if (bullets > MaxBullets)
                    {
                        result.Warnings.Add(new PersonaLintError
                        {
                            Field = $"sections.{section}",
                            Code = "too-many-bullets",
                            Message = $"'## {section}' has {bullets} bullets (recommend ≤ {MaxBullets})",
                        });
                    }
                }
            }
        }

        // ── placeholder markers ────────────────────────────────────────
        if (parsed.Body.Contains("<TODO>", StringComparison.OrdinalIgnoreCase) ||
            parsed.Body.Contains("TKTK", StringComparison.OrdinalIgnoreCase) ||
            parsed.Body.Contains("Lorem ipsum", StringComparison.OrdinalIgnoreCase))
        {
            result.Errors.Add(new PersonaLintError
            {
                Field = "body",
                Code = "placeholder-text",
                Message = "body contains placeholder markers (<TODO>, TKTK, Lorem ipsum) — replace before publishing",
            });
        }

        // ── card variants on disk ──────────────────────────────────────
        var folder = Path.GetDirectoryName(personaPath)!;
        var hasM = File.Exists(Path.Combine(folder, "card.m.jpg"));
        var hasF = File.Exists(Path.Combine(folder, "card.f.jpg"));
        var hasLegacy = File.Exists(Path.Combine(folder, "card.jpg"));
        if (!hasM && !hasF && !hasLegacy)
        {
            result.Warnings.Add(new PersonaLintError
            {
                Field = "assets",
                Code = "missing-card",
                Message = "no card.m.jpg, card.f.jpg, or legacy card.jpg next to the persona — UI will render without a portrait",
            });
        }
        else if (!hasLegacy && (!hasM || !hasF))
        {
            result.Warnings.Add(new PersonaLintError
            {
                Field = "assets",
                Code = "single-gender-card",
                Message = "only one gendered card variant present — random rendering will always show that one",
            });
        }

        return result;
    }

    private static void RequireString(Dictionary<string, object?> fields, string key, PersonaLintResult result)
    {
        var v = FrontmatterParser.GetString(fields, key);
        if (string.IsNullOrWhiteSpace(v))
        {
            result.Errors.Add(new PersonaLintError
            {
                Field = key,
                Code = "missing",
                Message = $"frontmatter field '{key}' is required",
            });
        }
    }
}
