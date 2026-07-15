namespace PKS.Infrastructure.Services.Persona.Models;

/// <summary>
/// One LLM call made while scoring — a (persona, rubric) cell, a screen
/// pass, or the auth preflight probe. Appended to
/// <see cref="PersonaSessionFile.Calls"/> as calls happen so a killed or
/// interrupted batch still leaves an accurate partial cost record.
/// </summary>
public sealed class PersonaSessionCall
{
    public string PersonaId { get; set; } = "";
    public string Rubric { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double CostUsd { get; set; }
    public double DurationMs { get; set; }
    public bool Ok { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Running token/cost log for every `pks persona score`/`score-all` call
/// made against one piece of content — the same idea as the session file
/// `claude`/`codex` write for their own runs, scoped to one post so the
/// price of a review pass is visible without an account-level billing
/// query. Lives at <c>_review/&lt;locale&gt;.PERSONA-SESSION.json</c>.
/// </summary>
public sealed class PersonaSessionFile
{
    public string Post { get; set; } = "";
    public string Locale { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public int TotalCalls { get; set; }
    public List<PersonaSessionCall> Calls { get; set; } = new();
}
