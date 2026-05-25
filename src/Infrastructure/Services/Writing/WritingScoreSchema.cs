using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Owns the JSON shape the agent's LLM must return after consuming the score
/// prompt. Two responsibilities:
///   1. EMIT a self-describing schema object (used by `pks writing prompt`)
///   2. VALIDATE a candidate reply with field-level errors (used by `pks writing accept`)
///
/// The schema is intentionally not full JSON-Schema — we keep it small enough
/// that an LLM can match it in one shot. The validator is what enforces correctness.
public static class WritingScoreSchema
{
    public static readonly string[] Dimensions = new[]
    {
        "Naturalness", "Tone", "Terminology", "Hook", "Value",
    };

    /// Compact self-describing schema. Embedded in the prompt + emitted in the
    /// bundle so the agent can show it to the model if needed.
    public static object SchemaObject(int maxFindings) => new
    {
        type = "object",
        required = new[] { "dimensions", "findings", "notes" },
        properties = new
        {
            dimensions = new
            {
                type = "object",
                required = Dimensions,
                description = "Per-dimension integer score 1..5 inclusive.",
                properties = Dimensions.ToDictionary(
                    d => d,
                    d => (object)new { type = "integer", minimum = 1, maximum = 5 }),
            },
            findings = new
            {
                type = "array",
                maxItems = maxFindings,
                items = new
                {
                    type = "object",
                    required = new[] { "dimension", "line", "match", "message", "suggestions" },
                    properties = new
                    {
                        dimension = new { type = "string", @enum = Dimensions },
                        line = new { type = "integer", minimum = 1 },
                        match = new { type = "string", maxLength = 200 },
                        message = new { type = "string", maxLength = 400 },
                        suggestions = new { type = "array", items = new { type = "string" } },
                    },
                },
            },
            notes = new { type = "string", maxLength = 800 },
        },
    };

    /// Example JSON the model should imitate. Easier signal than the schema alone.
    public static string SchemaExampleJson(int maxFindings) =>
$$"""
```json
{
  "dimensions": {
    "Naturalness": 1,
    "Tone":        1,
    "Terminology": 1,
    "Hook":        1,
    "Value":       1
  },
  "findings": [
    {
      "dimension":   "Naturalness | Tone | Terminology | Hook | Value",
      "line":        <1-indexed line in the source>,
      "match":       "<exact phrase flagged, <=120 chars>",
      "message":     "<one-sentence why this is off>",
      "suggestions": ["<concrete rewrite 1>", "<concrete rewrite 2>"]
    }
  ],
  "notes": "<2-4 sentences: single most important thing to fix, why, what to try>"
}
```
Cap findings at {{maxFindings}}. All five dimension scores are REQUIRED.
""";

    public sealed class ValidationResult
    {
        public bool Ok { get; init; }
        public List<ValidationError> Errors { get; init; } = new();
        public Dictionary<string, int> Dimensions { get; init; } = new();
        public List<WritingFinding> Findings { get; init; } = new();
        public string? Notes { get; init; }
    }

    public sealed class ValidationError
    {
        public required string Field { get; init; }
        public required string Code { get; init; }
        public string? Message { get; init; }
    }

    /// Field-level validator. Accepts raw text from any LLM, tolerates fenced
    /// blocks + leading prose (same extraction logic as the bundle ingestor).
    public static ValidationResult Validate(string responseText, int sourceLineCount)
    {
        var errors = new List<ValidationError>();

        var json = ExtractJson(responseText);
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

            // dimensions ────────────────────────────────────────────────────────
            var dimensions = new Dictionary<string, int>();
            if (!root.TryGetProperty("dimensions", out var dimsEl) || dimsEl.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new() { Field = "dimensions", Code = "missing" });
            }
            else
            {
                foreach (var name in Dimensions)
                {
                    if (!dimsEl.TryGetProperty(name, out var v))
                    {
                        errors.Add(new() { Field = $"dimensions.{name}", Code = "missing" });
                        continue;
                    }
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var iv))
                    {
                        errors.Add(new() { Field = $"dimensions.{name}", Code = "not_integer" });
                        continue;
                    }
                    if (iv < 1 || iv > 5)
                    {
                        errors.Add(new() { Field = $"dimensions.{name}", Code = "out_of_range",
                            Message = $"got {iv}, expected 1..5" });
                        continue;
                    }
                    dimensions[name] = iv;
                }
            }

            // findings ──────────────────────────────────────────────────────────
            var findings = new List<WritingFinding>();
            if (!root.TryGetProperty("findings", out var findEl) || findEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new() { Field = "findings", Code = "missing_or_not_array" });
            }
            else
            {
                int i = 0;
                foreach (var f in findEl.EnumerateArray())
                {
                    var prefix = $"findings[{i}]";
                    var dim = TryStr(f, "dimension");
                    if (dim is null) errors.Add(new() { Field = $"{prefix}.dimension", Code = "missing" });
                    else if (Array.IndexOf(Dimensions, dim) < 0)
                        errors.Add(new() { Field = $"{prefix}.dimension", Code = "unknown_enum",
                            Message = $"got '{dim}'" });

                    int line = 0;
                    if (!f.TryGetProperty("line", out var lineEl) || !lineEl.TryGetInt32(out line))
                        errors.Add(new() { Field = $"{prefix}.line", Code = "missing_or_not_integer" });
                    else if (line < 1 || line > sourceLineCount)
                        errors.Add(new() { Field = $"{prefix}.line", Code = "out_of_range",
                            Message = $"got {line}, expected 1..{sourceLineCount}" });

                    var match = TryStr(f, "match") ?? "";
                    var message = TryStr(f, "message") ?? "";
                    var suggestions = new List<string>();
                    if (f.TryGetProperty("suggestions", out var s) && s.ValueKind == JsonValueKind.Array)
                        foreach (var sg in s.EnumerateArray())
                            if (sg.ValueKind == JsonValueKind.String && sg.GetString() is { Length: > 0 } txt)
                                suggestions.Add(txt);

                    findings.Add(new WritingFinding
                    {
                        RuleId = $"Critic.{dim ?? "Unknown"}",
                        Severity = WritingSeverity.Suggestion,
                        Line = line, Column = 1, Match = match, Message = message,
                        Suggestions = suggestions,
                    });
                    i++;
                }
            }

            // notes ─────────────────────────────────────────────────────────────
            string? notes = null;
            if (root.TryGetProperty("notes", out var notesEl))
            {
                if (notesEl.ValueKind != JsonValueKind.String)
                    errors.Add(new() { Field = "notes", Code = "not_string" });
                else
                    notes = notesEl.GetString();
            }
            else
            {
                errors.Add(new() { Field = "notes", Code = "missing" });
            }

            return new ValidationResult
            {
                Ok = errors.Count == 0,
                Errors = errors,
                Dimensions = dimensions,
                Findings = findings,
                Notes = notes,
            };
        }
    }

    private static string? TryStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fenced = System.Text.RegularExpressions.Regex.Match(text,
            @"```(?:json)?\s*\n(?<body>[\s\S]*?)\n```",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (fenced.Success)
        {
            var body = fenced.Groups["body"].Value.Trim();
            if (body.StartsWith('{')) return body;
        }
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first ? text[first..(last + 1)] : null;
    }
}
