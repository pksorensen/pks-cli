using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Strict reply-validator for the naturalness extractor critic. Mirrors the
/// EMIT + VALIDATE responsibilities of [[WritingScoreSchema]].
///
/// Required shape:
///   {
///     "post": "<source path>",
///     "critic_model": "<model id>",     (optional)
///     "extracted_at": "<ISO-8601>",     (optional)
///     "candidates": [
///       {
///         "id": "c1",
///         "line": <1-indexed line in the source>,
///         "original": "<sentence>",
///         "issue": "<one-liner why>",
///         "alternatives": [
///           { "label": "A", "text": "...", "rationale": "...", "authorlikeness": 0.65 },
///           { "label": "B", "text": "...", "rationale": "...", "authorlikeness": 0.40 },
///           { "label": "C", "text": "...", "rationale": "...", "authorlikeness": 0.70 }
///         ]
///       }
///     ]
///   }
public static class NaturalnessCandidatesSchema
{
    public const int MaxCandidates = 15;
    public static readonly string[] RequiredLabels = new[] { "A", "B", "C" };

    public static object SchemaObject(int maxCandidates = MaxCandidates) => new
    {
        type = "object",
        required = new[] { "post", "candidates" },
        properties = new
        {
            post = new { type = "string" },
            critic_model = new { type = "string" },
            extracted_at = new { type = "string" },
            candidates = new
            {
                type = "array",
                maxItems = maxCandidates,
                items = new
                {
                    type = "object",
                    required = new[] { "id", "line", "original", "issue", "alternatives" },
                    properties = new
                    {
                        id = new { type = "string" },
                        line = new { type = "integer", minimum = 1 },
                        original = new { type = "string" },
                        issue = new { type = "string" },
                        alternatives = new
                        {
                            type = "array",
                            minItems = 3,
                            maxItems = 3,
                            items = new
                            {
                                type = "object",
                                required = new[] { "label", "text", "rationale", "authorlikeness" },
                                properties = new
                                {
                                    label = new { type = "string", @enum = RequiredLabels },
                                    text = new { type = "string" },
                                    rationale = new { type = "string" },
                                    authorlikeness = new { type = "number", minimum = 0.0, maximum = 1.0 },
                                },
                            },
                        },
                    },
                },
            },
        },
    };

    public static string SchemaExampleJson(int maxCandidates = MaxCandidates) =>
$$"""
```json
{
  "post": "<absolute path to the post>",
  "critic_model": "<model id>",
  "extracted_at": "<ISO-8601 UTC>",
  "candidates": [
    {
      "id": "c1",
      "line": 47,
      "original": "<flagged sentence>",
      "issue": "<one-sentence why this hurts naturalness>",
      "alternatives": [
        { "label": "A", "text": "<rewrite>", "rationale": "<why>", "authorlikeness": 0.65 },
        { "label": "B", "text": "<rewrite>", "rationale": "<why>", "authorlikeness": 0.40 },
        { "label": "C", "text": "<rewrite>", "rationale": "<why>", "authorlikeness": 0.70 }
      ]
    }
  ]
}
```
Cap at {{maxCandidates}} candidates. Exactly 3 alternatives per candidate (labels A, B, C).
""";

    public sealed class ValidationResult
    {
        public bool Ok { get; init; }
        public List<ValidationError> Errors { get; init; } = new();
        public NaturalnessCandidatesFile? Parsed { get; init; }
    }

    public sealed class ValidationError
    {
        public required string Field { get; init; }
        public required string Code { get; init; }
        public string? Message { get; init; }
    }

    public static ValidationResult Validate(string responseText, int sourceLineCount,
        int maxCandidates = MaxCandidates)
    {
        var errors = new List<ValidationError>();
        var json = WritingScoreSchema.ExtractJson(responseText);
        if (json is null)
        {
            errors.Add(new() { Field = "$", Code = "no_json",
                Message = "No JSON object found. Expected raw JSON or ```json fenced block." });
            return new ValidationResult { Ok = false, Errors = errors };
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException jx)
        {
            errors.Add(new() { Field = "$", Code = "parse_error", Message = jx.Message });
            return new ValidationResult { Ok = false, Errors = errors };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new() { Field = "$", Code = "not_object" });
                return new ValidationResult { Ok = false, Errors = errors };
            }

            var file = new NaturalnessCandidatesFile
            {
                Post = TryStr(root, "post") ?? "",
                CriticModel = TryStr(root, "critic_model"),
                ExtractedAt = TryDate(root, "extracted_at") ?? DateTime.UtcNow,
            };

            if (string.IsNullOrWhiteSpace(file.Post))
                errors.Add(new() { Field = "post", Code = "missing" });

            if (!root.TryGetProperty("candidates", out var candEl) || candEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new() { Field = "candidates", Code = "missing_or_not_array" });
                return new ValidationResult { Ok = false, Errors = errors, Parsed = file };
            }

            var candidates = candEl.EnumerateArray().ToList();
            if (candidates.Count > maxCandidates)
            {
                errors.Add(new() { Field = "candidates", Code = "too_many",
                    Message = $"got {candidates.Count}, max {maxCandidates}" });
            }

            int i = 0;
            foreach (var c in candidates)
            {
                var prefix = $"candidates[{i}]";
                var cand = new NaturalnessCandidate
                {
                    Id = TryStr(c, "id") ?? "",
                    Original = TryStr(c, "original") ?? "",
                    Issue = TryStr(c, "issue") ?? "",
                };
                if (string.IsNullOrWhiteSpace(cand.Id))
                    errors.Add(new() { Field = $"{prefix}.id", Code = "missing" });
                if (string.IsNullOrWhiteSpace(cand.Original))
                    errors.Add(new() { Field = $"{prefix}.original", Code = "missing" });

                int line = 0;
                if (!c.TryGetProperty("line", out var lineEl) || !lineEl.TryGetInt32(out line))
                    errors.Add(new() { Field = $"{prefix}.line", Code = "missing_or_not_integer" });
                else if (line < 1 || line > sourceLineCount)
                    errors.Add(new() { Field = $"{prefix}.line", Code = "out_of_range",
                        Message = $"got {line}, expected 1..{sourceLineCount}" });
                cand.Line = line;

                if (!c.TryGetProperty("alternatives", out var altEl) || altEl.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new() { Field = $"{prefix}.alternatives", Code = "missing_or_not_array" });
                }
                else
                {
                    var alts = altEl.EnumerateArray().ToList();
                    if (alts.Count != 3)
                        errors.Add(new() { Field = $"{prefix}.alternatives", Code = "wrong_count",
                            Message = $"got {alts.Count}, expected exactly 3" });

                    var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int j = 0;
                    foreach (var a in alts)
                    {
                        var ap = $"{prefix}.alternatives[{j}]";
                        var alt = new NaturalnessAlternative
                        {
                            Label = TryStr(a, "label") ?? "",
                            Text = TryStr(a, "text") ?? "",
                            Rationale = TryStr(a, "rationale") ?? "",
                        };
                        if (string.IsNullOrWhiteSpace(alt.Label) ||
                            Array.IndexOf(RequiredLabels, alt.Label.ToUpperInvariant()) < 0)
                        {
                            errors.Add(new() { Field = $"{ap}.label", Code = "unknown_enum",
                                Message = $"got '{alt.Label}', expected one of A/B/C" });
                        }
                        else
                        {
                            alt.Label = alt.Label.ToUpperInvariant();
                            if (!seenLabels.Add(alt.Label))
                                errors.Add(new() { Field = $"{ap}.label", Code = "duplicate",
                                    Message = $"label '{alt.Label}' used more than once" });
                        }
                        if (string.IsNullOrWhiteSpace(alt.Text))
                            errors.Add(new() { Field = $"{ap}.text", Code = "missing" });

                        if (!a.TryGetProperty("authorlikeness", out var alEl) ||
                            (alEl.ValueKind != JsonValueKind.Number))
                        {
                            errors.Add(new() { Field = $"{ap}.authorlikeness", Code = "missing_or_not_number" });
                        }
                        else
                        {
                            var v = alEl.GetDouble();
                            if (v < 0.0 || v > 1.0)
                                errors.Add(new() { Field = $"{ap}.authorlikeness", Code = "out_of_range",
                                    Message = $"got {v}, expected [0,1]" });
                            alt.Authorlikeness = v;
                        }
                        cand.Alternatives.Add(alt);
                        j++;
                    }
                }

                file.Candidates.Add(cand);
                i++;
            }

            return new ValidationResult
            {
                Ok = errors.Count == 0,
                Errors = errors,
                Parsed = file,
            };
        }
    }

    private static string? TryStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? TryDate(JsonElement el, string name)
    {
        var s = TryStr(el, name);
        if (s is null) return null;
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d : null;
    }
}
