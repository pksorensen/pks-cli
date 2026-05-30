namespace PKS.Infrastructure.Services.Persona.Models;

public sealed class PersonaScoreEvidence
{
    public string Quote { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>
/// One LLM judgement of a content item against (persona, rubric). Lives
/// inside <see cref="PersonaScoresFile.Scores"/>. Re-scoring upserts on
/// (PersonaId, Rubric); we keep only the latest, identified by ScoredAt.
/// </summary>
public sealed class PersonaScore
{
    public string PersonaId { get; set; } = "";
    public string Rubric { get; set; } = "";
    public string Model { get; set; } = "";
    public int Score { get; set; }
    public string Rationale { get; set; } = "";
    public Dictionary<string, int> Subscores { get; set; } = new();
    public List<PersonaScoreEvidence> Evidence { get; set; } = new();
    public DateTime ScoredAt { get; set; } = DateTime.UtcNow;
}

public sealed class PersonaScoresFile
{
    public string Post { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<PersonaScore> Scores { get; set; } = new();
}
