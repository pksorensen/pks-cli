namespace PKS.Infrastructure.Services.Writing.Models;

public sealed class NaturalnessAlternative
{
    /// "A" | "B" | "C" — exactly one of these three, validated.
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public string Rationale { get; set; } = "";
    /// In [0, 1]. How close the rewrite is to the author's profile voice.
    public double Authorlikeness { get; set; }
}
