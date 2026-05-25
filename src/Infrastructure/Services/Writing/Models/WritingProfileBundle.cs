namespace PKS.Infrastructure.Services.Writing.Models;

/// Single-file portable bundle a filesystem-less Claude session can produce
/// (e.g. claude.ai / mobile) and `pks writing profile ingest` can explode
/// onto disk. Everything is optional — partial bundles are normal.
///
/// JSON schema (camelCase via WritingProfileStore.JsonOptions):
/// {
///   "version": 1,
///   "generatedBy": "claude-cowork",       (optional, free-form)
///   "generatedAt": "2026-05-25T...",      (optional, ISO-8601)
///   "profile":     "# Writer Profile\n…",  (full markdown body or null)
///   "anglicisms": [
///     { "english": "deploye", "danishAlternatives": ["udrulle"], "note": null }
///   ],
///   "allowlist":  ["AppHost", "vibecast"],
///   "references": {
///     "blog":     [ { "id": "post-01", "content": "…full markdown…" } ],
///     "linkedin": [ … ]
///   },
///   "lessons": [
///     { "dimension": "Tone", "lesson": "…", "sourcePath": "/p/x.md" }
///   ]
/// }
public sealed class WritingProfileBundle
{
    public int Version { get; set; } = 1;
    public string? GeneratedBy { get; set; }
    public DateTime? GeneratedAt { get; set; }

    public string? Profile { get; set; }
    public List<AnglicismEntry>? Anglicisms { get; set; }
    public List<string>? Allowlist { get; set; }
    public Dictionary<string, List<ReferenceSample>>? References { get; set; }
    public List<BundleLesson>? Lessons { get; set; }
}

public sealed class BundleLesson
{
    public string Dimension { get; set; } = "";
    public string Lesson { get; set; } = "";
    public string? SourcePath { get; set; }
}

public sealed class BundleIngestResult
{
    public bool ProfileWritten { get; set; }
    public bool ProfileSkipped { get; set; }      // existed; --force not set
    public int AnglicismsAdded { get; set; }
    public int AllowlistAdded { get; set; }
    public Dictionary<string, int> ReferencesAdded { get; set; } = new();
    public Dictionary<string, int> ReferencesSkipped { get; set; } = new();
    public int LessonsAppended { get; set; }
}
