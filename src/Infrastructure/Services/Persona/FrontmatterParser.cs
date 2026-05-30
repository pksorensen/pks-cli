namespace PKS.Infrastructure.Services.Persona;

/// <summary>
/// Tiny YAML-frontmatter parser sized to the persona/rubric file shapes —
/// flat string/number/bool scalars and inline string arrays
/// (<c>foo: [a, b, c]</c>). We intentionally avoid taking a YAML dependency
/// for a few flat keys; the writing services do the same.
/// </summary>
internal static class FrontmatterParser
{
    public sealed record Parsed(Dictionary<string, object?> Fields, string Body);

    public static Parsed Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("---", StringComparison.Ordinal))
            return new Parsed(new Dictionary<string, object?>(StringComparer.Ordinal), raw);

        var normalised = raw.Replace("\r\n", "\n");
        var lines = normalised.Split('\n');
        var closeIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---") { closeIdx = i; break; }
        }
        if (closeIdx <= 0)
            return new Parsed(new Dictionary<string, object?>(StringComparer.Ordinal), raw);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 1; i < closeIdx; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#")) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            fields[key] = ParseScalar(value);
        }

        var bodyStart = closeIdx + 1;
        // Skip a single immediate blank line after the closing ---
        if (bodyStart < lines.Length && string.IsNullOrEmpty(lines[bodyStart])) bodyStart++;
        var body = string.Join('\n', lines.Skip(bodyStart));
        return new Parsed(fields, body);
    }

    public static string? GetString(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        return v.ToString();
    }

    public static List<string>? GetStringList(Dictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v is null) return null;
        if (v is List<string> already) return already;
        return null;
    }

    /// <summary>
    /// Parses headings + sections out of markdown body. Returns a map keyed
    /// by heading text (without the leading "##") containing the lines
    /// between this heading and the next.
    /// </summary>
    public static Dictionary<string, string> ParseSections(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var current = "";
        var buffer = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current.Length > 0)
                    result[current] = string.Join('\n', buffer).TrimEnd();
                current = raw.Substring(3).Trim();
                buffer.Clear();
            }
            else if (current.Length > 0)
            {
                buffer.Add(raw);
            }
        }
        if (current.Length > 0)
            result[current] = string.Join('\n', buffer).TrimEnd();
        return result;
    }

    /// <summary>
    /// Counts top-level bullets ("- foo" or "* foo") in a section body.
    /// </summary>
    public static int CountBullets(string sectionBody)
    {
        var count = 0;
        foreach (var line in sectionBody.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    private static object? ParseScalar(string raw)
    {
        if (raw.Length == 0) return "";
        // Strip simple wrapping quotes
        if ((raw.StartsWith("\"") && raw.EndsWith("\"")) ||
            (raw.StartsWith("'") && raw.EndsWith("'")))
        {
            return raw.Substring(1, raw.Length - 2);
        }
        // Inline string array: [a, b, c]
        if (raw.StartsWith("[") && raw.EndsWith("]"))
        {
            var inner = raw.Substring(1, raw.Length - 2);
            var parts = inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim('"', '\'').Trim())
                .ToList();
            return parts;
        }
        return raw;
    }
}
