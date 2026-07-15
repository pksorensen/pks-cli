namespace PKS.Infrastructure.Services.Persona;

/// <summary>
/// Path resolver for the <c>pks persona</c> command family.
///
/// Personas live at <c>personas/&lt;locale&gt;/&lt;slug&gt;/&lt;slug&gt;.md</c>
/// inside the working tree. Rubrics are shared across locales at
/// <c>personas/_rubrics/&lt;id&gt;.md</c>. Score sidecars land next to the
/// content being scored, under <c>_review/&lt;locale&gt;.PERSONA-SCORES.json</c>
/// — mirroring the writing layout so all editorial sidecars cluster in one
/// folder per post.
/// </summary>
public interface IPersonaPathResolver
{
    /// <summary>
    /// Resolves the absolute path to the <c>personas/</c> root. Walks up
    /// from <paramref name="cwd"/> looking for the directory; returns null if
    /// none is found.
    /// </summary>
    string? ResolvePersonasRoot(string cwd);

    /// <summary>Locale-scoped persona directory, e.g. <c>personas/da/</c>.</summary>
    string PersonasLocaleDir(string personasRoot, string locale);

    /// <summary>The single persona file: <c>personas/&lt;locale&gt;/&lt;slug&gt;/&lt;slug&gt;.md</c>.</summary>
    string PersonaFilePath(string personasRoot, string locale, string slug);

    /// <summary>Shared rubric directory, locale-independent.</summary>
    string RubricsDir(string personasRoot);

    /// <summary>Single rubric file by id, e.g. <c>relevance.md</c>.</summary>
    string RubricFilePath(string personasRoot, string rubricId);

    /// <summary>
    /// The review-sidecar folder next to a piece of content. Reuses the
    /// <c>_review/</c> convention so persona scores sit next to naturalness
    /// and writing-report sidecars for the same post.
    /// </summary>
    string ReviewDir(string contentFilePath);

    /// <summary>
    /// <c>_review/&lt;locale&gt;.PERSONA-SCORES.json</c> next to the content.
    /// Locale is encoded in the filename — the content's own filename
    /// (<c>da.md</c>, <c>en.md</c>) doesn't always carry it.
    ///
    /// When <paramref name="modelTag"/> is non-empty the sidecar is
    /// model-scoped: <c>_review/&lt;locale&gt;.PERSONA-SCORES.&lt;model-slug&gt;.json</c>.
    /// This lets scores from different scoring models (e.g. gpt-5.5 vs
    /// claude-opus-4-8) coexist instead of upserting over each other —
    /// the basis for a per-post model A/B.
    /// </summary>
    string ScoresSidecarPath(string contentFilePath, string locale, string? modelTag = null);

    /// <summary>
    /// <c>_review/&lt;locale&gt;.PERSONA-SESSION.json</c> next to the content —
    /// a running log of every LLM call `pks persona score`/`score-all` made
    /// against this content (tokens, estimated cost, duration), mirroring
    /// the session-file convention `claude`/`codex` write for their own
    /// runs. Same model-scoping rule as <see cref="ScoresSidecarPath"/>.
    /// </summary>
    string SessionSidecarPath(string contentFilePath, string locale, string? modelTag = null);
}
