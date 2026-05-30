using System.Text.Json;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

/// <summary>
/// JSON-schema emit + validation for an LLM persona-score reply. Mirrors
/// <c>NaturalnessCandidatesSchema</c> — emit a dynamic schema for the prompt
/// bundle; validate the reply field-by-field with structured errors.
/// </summary>
public static class PersonaScoreSchema
{
    public sealed class ValidationError
    {
        public string Field { get; set; } = "";
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class ValidationResult
    {
        public bool Ok => Errors.Count == 0;
        public List<ValidationError> Errors { get; set; } = new();
        public PersonaScore? Parsed { get; set; }
    }

    public static object SchemaObject(Rubric rubric)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["score"] = new { type = "integer", minimum = 1, maximum = 5 },
            ["rationale"] = new { type = "string", minLength = 8, maxLength = 600 },
            ["evidence"] = new
            {
                type = "array",
                maxItems = 5,
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        quote = new { type = "string" },
                        note = new { type = "string" },
                    },
                    required = new[] { "quote", "note" },
                },
            },
        };
        var required = new List<string> { "score", "rationale", "evidence" };

        if (rubric.Subscores.Count > 0)
        {
            var sub = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var key in rubric.Subscores)
            {
                sub[key] = new { type = "integer", minimum = 1, maximum = 5 };
            }
            properties["subscores"] = new
            {
                type = "object",
                properties = sub,
                required = rubric.Subscores,
            };
            required.Add("subscores");
        }

        return new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false,
        };
    }

    public static ValidationResult Validate(string responseText, Rubric rubric, string personaId, string model)
    {
        var result = new ValidationResult();
        var jsonText = ExtractJson(responseText);
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            result.Errors.Add(new ValidationError { Field = "$", Code = "no-json", Message = "no JSON object found in reply" });
            return result;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException ex)
        {
            result.Errors.Add(new ValidationError { Field = "$", Code = "invalid-json", Message = ex.Message });
            return result;
        }

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            result.Errors.Add(new ValidationError { Field = "$", Code = "not-object", Message = "top-level JSON value must be an object" });
            return result;
        }

        var score = ReadInt(root, "score", 1, 5, result);
        var rationale = ReadString(root, "rationale", 4, 1500, result);
        var evidence = ReadEvidence(root, result);
        var subscores = rubric.Subscores.Count > 0 ? ReadSubscores(root, rubric.Subscores, result) : new Dictionary<string, int>();

        if (!result.Ok) return result;

        result.Parsed = new PersonaScore
        {
            PersonaId = personaId,
            Rubric = rubric.Id,
            Model = model,
            Score = score!.Value,
            Rationale = rationale!,
            Subscores = subscores,
            Evidence = evidence,
            ScoredAt = DateTime.UtcNow,
        };
        return result;
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();

        // Strip ```json … ``` fences if present.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text.Substring(firstNewline + 1);
            var endFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0) text = text.Substring(0, endFence);
            text = text.Trim();
        }

        if (text.StartsWith("{", StringComparison.Ordinal)) return text;

        // Fall back: first '{' to matching last '}'.
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open >= 0 && close > open) return text.Substring(open, close - open + 1);
        return "";
    }

    private static int? ReadInt(JsonElement root, string field, int min, int max, ValidationResult r)
    {
        if (!root.TryGetProperty(field, out var v))
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "missing", Message = $"required field '{field}' is missing" });
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "not-integer", Message = $"'{field}' must be an integer" });
            return null;
        }
        if (n < min || n > max)
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "out-of-range", Message = $"'{field}' must be between {min} and {max}; got {n}" });
            return null;
        }
        return n;
    }

    private static string? ReadString(JsonElement root, string field, int min, int max, ValidationResult r)
    {
        if (!root.TryGetProperty(field, out var v))
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "missing", Message = $"required field '{field}' is missing" });
            return null;
        }
        if (v.ValueKind != JsonValueKind.String)
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "not-string", Message = $"'{field}' must be a string" });
            return null;
        }
        var s = v.GetString() ?? "";
        if (s.Length < min)
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "too-short", Message = $"'{field}' is {s.Length} chars (min {min})" });
            return null;
        }
        if (s.Length > max)
        {
            r.Errors.Add(new ValidationError { Field = field, Code = "too-long", Message = $"'{field}' is {s.Length} chars (max {max})" });
            return null;
        }
        return s;
    }

    private static List<PersonaScoreEvidence> ReadEvidence(JsonElement root, ValidationResult r)
    {
        var list = new List<PersonaScoreEvidence>();
        if (!root.TryGetProperty("evidence", out var ev))
        {
            // Evidence is required by schema but be lenient: empty list, with a warning-style error.
            r.Errors.Add(new ValidationError { Field = "evidence", Code = "missing", Message = "'evidence' is required (may be an empty array)" });
            return list;
        }
        if (ev.ValueKind != JsonValueKind.Array)
        {
            r.Errors.Add(new ValidationError { Field = "evidence", Code = "not-array", Message = "'evidence' must be an array" });
            return list;
        }
        var i = 0;
        foreach (var item in ev.EnumerateArray())
        {
            // Lenient: accept either the canonical { quote, note } object OR
            // a plain string (a frequent LLM shortcut). Strings are coerced
            // to { quote: <string>, note: "" }.
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString() ?? "";
                if (s.Length > 0) list.Add(new PersonaScoreEvidence { Quote = s, Note = "" });
                i++;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                r.Errors.Add(new ValidationError { Field = $"evidence[{i}]", Code = "not-object", Message = "each evidence item must be an object or string" });
                i++;
                continue;
            }
            // Accept several common key variants: { quote, note } (canonical),
            // { quote, why }, { text, note }, { quote, rationale }, ...
            var quote = ReadEvidenceString(item, "quote", "text", "excerpt");
            var note = ReadEvidenceString(item, "note", "why", "rationale", "reason", "comment");
            if (quote.Length == 0)
                r.Errors.Add(new ValidationError { Field = $"evidence[{i}].quote", Code = "missing", Message = "evidence item missing 'quote' (or 'text')" });
            list.Add(new PersonaScoreEvidence { Quote = quote, Note = note });
            i++;
        }
        return list;
    }

    private static string ReadEvidenceString(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!;
            }
        }
        return "";
    }

    private static Dictionary<string, int> ReadSubscores(JsonElement root, List<string> keys, ValidationResult r)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!root.TryGetProperty("subscores", out var sub))
        {
            r.Errors.Add(new ValidationError { Field = "subscores", Code = "missing", Message = "'subscores' is required by this rubric" });
            return map;
        }
        if (sub.ValueKind != JsonValueKind.Object)
        {
            r.Errors.Add(new ValidationError { Field = "subscores", Code = "not-object", Message = "'subscores' must be an object" });
            return map;
        }
        foreach (var key in keys)
        {
            if (!sub.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n))
            {
                r.Errors.Add(new ValidationError { Field = $"subscores.{key}", Code = "missing-or-invalid", Message = $"subscore '{key}' must be an integer" });
                continue;
            }
            if (n < 1 || n > 5)
            {
                r.Errors.Add(new ValidationError { Field = $"subscores.{key}", Code = "out-of-range", Message = $"subscore '{key}' must be between 1 and 5; got {n}" });
                continue;
            }
            map[key] = n;
        }
        return map;
    }
}
