namespace PKS.Infrastructure.Services.Writing.Models;

/// A loan-translation pitfall — a Danish word that is literally translated
/// from English but reads wrong in Danish tech context. Example:
///   `barn → barne-Claude, underordnet, child | "barn" = menneskeunge på dansk, ikke en proces`
/// Distinct from [[AnglicismEntry]] (english → danish): a calque is
/// real Danish whose literal meaning misfires.
public sealed class CalqueEntry
{
    /// The Danish term as written in the post (e.g. "barn", "sky").
    public string LiteralDanish { get; set; } = "";

    /// Better choices (could be the original English term, or a Danish phrasing
    /// that doesn't carry the wrong literal meaning).
    public List<string> Alternatives { get; set; } = new();

    /// Why the literal Danish reads wrong — surfaced to the critic and the user.
    public string? Why { get; set; }
}
