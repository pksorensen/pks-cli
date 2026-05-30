namespace PKS.Infrastructure.Services.Persona.Models;

/// <summary>
/// A reader archetype the blog (or any other surface) can score content
/// against. Mirrors the YAML frontmatter + markdown body found at
/// <c>personas/&lt;locale&gt;/&lt;slug&gt;/&lt;slug&gt;.md</c>.
/// </summary>
public sealed class Persona
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Segment { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string Lang { get; set; } = "";
    /// <summary>Full markdown body after the closing frontmatter delimiter.</summary>
    public string Body { get; set; } = "";
    /// <summary>Absolute path the persona was loaded from.</summary>
    public string SourcePath { get; set; } = "";
    /// <summary>Parsed body sections keyed by heading (e.g. "Beskrivelse").</summary>
    public Dictionary<string, string> Sections { get; set; } = new();
}
