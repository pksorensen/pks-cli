using System.Text.Json;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Parses a writer-profile bundle from JSON text (raw or wrapped in a
/// ```json fenced block inside a Markdown document) and writes it into the
/// store. Designed for the cowork → user → ingest hand-off:
///   1. The cowork (filesystem-less) Claude session produces the bundle.
///   2. The user saves the JSON (or the whole markdown reply) to a file.
///   3. `pks writing profile ingest &lt;path&gt;` runs this ingestor.
public static class WritingBundleIngestor
{
    /// Extract a `WritingProfileBundle` from arbitrary input text. Accepts:
    ///   - raw JSON ({…})
    ///   - markdown containing a ```json fenced block (we grab the first)
    /// Returns null + a reason string when nothing usable is found.
    public static (WritingProfileBundle? Bundle, string? Error) Parse(string text)
    {
        var json = ExtractJson(text);
        if (json is null)
            return (null, "No JSON object found. Expected raw JSON or a markdown reply containing a ```json … ``` block.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            return (ReadBundle(doc.RootElement), null);
        }
        catch (JsonException jx)
        {
            return (null, "JSON parse error: " + jx.Message);
        }
    }

    /// Hand-rolled reader that tolerates schema variants cowork might invent:
    ///   - `version: 1` or `schema: "pks-writing-v1"` (or both, or neither)
    ///   - anglicism entry: `english`|`term` + `danishAlternatives[]`|`suggestion` (string, split on '/' or ',')
    ///   - reference sample: `content`|`text` (id required)
    ///   - lesson: `dimension`+`lesson` (`sourcePath` optional)
    /// Unknown fields are ignored. Always returns a bundle — empty arrays
    /// when sections are missing.
    internal static WritingProfileBundle ReadBundle(JsonElement root)
    {
        var bundle = new WritingProfileBundle { Version = 1 };

        if (root.TryGetProperty("generatedBy", out var gb) && gb.ValueKind == JsonValueKind.String)
            bundle.GeneratedBy = gb.GetString();
        if (root.TryGetProperty("generatedAt", out var ga) && ga.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(ga.GetString(), out var ts))
            bundle.GeneratedAt = ts;
        if (root.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String)
            bundle.Profile = p.GetString();

        if (root.TryGetProperty("anglicisms", out var angs) && angs.ValueKind == JsonValueKind.Array)
        {
            bundle.Anglicisms = new List<AnglicismEntry>();
            foreach (var a in angs.EnumerateArray())
            {
                var english = TryString(a, "english") ?? TryString(a, "term");
                if (string.IsNullOrWhiteSpace(english)) continue;

                var alts = new List<string>();
                if (a.TryGetProperty("danishAlternatives", out var altsEl) && altsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in altsEl.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                            alts.Add(s.Trim());
                }
                else if (TryString(a, "suggestion") is { Length: > 0 } single)
                {
                    // Split on '/' or ',' — cowork often writes "udrulning / sætte i drift".
                    foreach (var part in single.Split(new[] { '/', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        alts.Add(part);
                }

                bundle.Anglicisms.Add(new AnglicismEntry
                {
                    English = english.Trim(),
                    DanishAlternatives = alts,
                    Note = TryString(a, "note"),
                });
            }
        }

        if (root.TryGetProperty("allowlist", out var allow) && allow.ValueKind == JsonValueKind.Array)
        {
            bundle.Allowlist = new List<string>();
            foreach (var v in allow.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                    bundle.Allowlist.Add(s.Trim());
        }

        if (root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Object)
        {
            bundle.References = new Dictionary<string, List<ReferenceSample>>();
            foreach (var channel in refs.EnumerateObject())
            {
                if (channel.Value.ValueKind != JsonValueKind.Array) continue;
                var list = new List<ReferenceSample>();
                foreach (var s in channel.Value.EnumerateArray())
                {
                    var id = TryString(s, "id");
                    var content = TryString(s, "content") ?? TryString(s, "text");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(content)) continue;
                    list.Add(new ReferenceSample { Id = id.Trim(), Content = content });
                }
                if (list.Count > 0) bundle.References[channel.Name] = list;
            }
        }

        if (root.TryGetProperty("lessons", out var lessons) && lessons.ValueKind == JsonValueKind.Array)
        {
            bundle.Lessons = new List<BundleLesson>();
            foreach (var l in lessons.EnumerateArray())
            {
                var dim = TryString(l, "dimension");
                var msg = TryString(l, "lesson");
                if (string.IsNullOrWhiteSpace(dim) || string.IsNullOrWhiteSpace(msg)) continue;
                bundle.Lessons.Add(new BundleLesson
                {
                    Dimension = dim.Trim(),
                    Lesson = msg.Trim(),
                    SourcePath = TryString(l, "sourcePath"),
                });
            }
        }

        return bundle;
    }

    private static string? TryString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// Writes a bundle into the global writing root via the store, honoring
    /// `force` (overwrite existing files) and `mergeOnly` (additive lists only).
    public static async Task<BundleIngestResult> ApplyAsync(
        WritingProfileBundle bundle,
        IWritingPathResolver paths,
        IWritingProfileStore store,
        bool force,
        CancellationToken ct = default)
    {
        await store.EnsureGlobalLayoutAsync(ct);
        var result = new BundleIngestResult();

        // profile.md — overwrite only with --force, never silently clobber.
        if (!string.IsNullOrWhiteSpace(bundle.Profile))
        {
            var path = paths.GlobalProfilePath;
            var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
            var isUntouchedTemplate = existing is not null && existing.Contains(DefaultSeeds.ProfileTemplateMarker);

            if (existing is null || force || isUntouchedTemplate)
            {
                await File.WriteAllTextAsync(path, bundle.Profile, ct);
                result.ProfileWritten = true;
            }
            else
            {
                result.ProfileSkipped = true;
            }
        }

        // anglicisms — additive; per-entry add via store so the user's edits are preserved.
        if (bundle.Anglicisms is { Count: > 0 } angs)
        {
            foreach (var a in angs)
            {
                if (string.IsNullOrWhiteSpace(a.English)) continue;
                await store.AddAnglicismAsync(a, ct);
                result.AnglicismsAdded++;
            }
        }

        // allowlist — additive set.
        if (bundle.Allowlist is { Count: > 0 } allow)
        {
            foreach (var t in allow)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                await store.AddAllowedTermAsync(t.Trim(), ct);
                result.AllowlistAdded++;
            }
        }

        // references — one file per sample under reference/<channel>/<id>.md.
        if (bundle.References is { Count: > 0 } refs)
        {
            foreach (var (channel, samples) in refs)
            {
                if (string.IsNullOrWhiteSpace(channel)) continue;
                var dir = paths.GlobalReferenceChannelDir(channel);
                Directory.CreateDirectory(dir);

                foreach (var s in samples)
                {
                    if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Content)) continue;
                    var safeId = SafeFileName(s.Id);
                    var dest = Path.Combine(dir, safeId + ".md");

                    if (File.Exists(dest) && !force)
                    {
                        Increment(result.ReferencesSkipped, channel);
                        continue;
                    }
                    await File.WriteAllTextAsync(dest, s.Content, ct);
                    Increment(result.ReferencesAdded, channel);
                }
            }
        }

        // lessons — always append.
        if (bundle.Lessons is { Count: > 0 } lessons)
        {
            foreach (var l in lessons)
            {
                if (string.IsNullOrWhiteSpace(l.Lesson) || string.IsNullOrWhiteSpace(l.Dimension)) continue;
                await store.AppendLessonAsync(l.Dimension, l.Lesson, l.SourcePath ?? "bundle", ct);
                result.LessonsAppended++;
            }
        }

        return result;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    internal static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 1. ```json … ``` fenced block (prefer the first one).
        var fenceRx = new Regex(@"```json\s*\n(?<body>[\s\S]*?)\n```",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var m = fenceRx.Match(text);
        if (m.Success) return m.Groups["body"].Value.Trim();

        // 2. ``` … ``` un-tagged fence whose body starts with '{'.
        var anyFenceRx = new Regex(@"```\s*\n(?<body>[\s\S]*?)\n```",
            RegexOptions.Multiline);
        foreach (Match am in anyFenceRx.Matches(text))
        {
            var body = am.Groups["body"].Value.Trim();
            if (body.StartsWith('{')) return body;
        }

        // 3. Raw JSON — first '{' to last '}'.
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first >= 0 && last > first) return text[first..(last + 1)];

        return null;
    }

    private static string SafeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        var clean = sb.ToString().Trim('.', ' ', '/');
        return string.IsNullOrEmpty(clean) ? "sample" : clean;
    }

    private static void Increment(Dictionary<string, int> dict, string key) =>
        dict[key] = dict.GetValueOrDefault(key) + 1;
}
