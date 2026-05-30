namespace PKS.Infrastructure.Services.Persona.Models;

/// <summary>
/// One scoring dimension: relevance, resonance, quality, credibility,
/// actionability, novelty. Stored at <c>personas/_rubrics/&lt;id&gt;.md</c>.
/// The frontmatter declares the JSON output schema the LLM must emit; the
/// body explains what each score means.
/// </summary>
public sealed class Rubric
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Markdown body — score-meaning sections, evidence cues.</summary>
    public string Body { get; set; } = "";
    /// <summary>Absolute path the rubric was loaded from.</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>Optional ordered list of subscore keys named in the schema.</summary>
    public List<string> Subscores { get; set; } = new();
}
