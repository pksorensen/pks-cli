namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// The transcription methods, by their Agentics names.
///
/// WHY THIS EXISTS. A transcript is not produced by "a model". It is produced by a
/// *combination*: one engine for the words, another for who spoke when, and our own alignment
/// and punctuation passes on top. Naming the combination after one of its models would be
/// wrong the moment we swap it, and naming it after all of them ("MAI-1.5 + fast transcription
/// + gpt-4o-diarize") is not a thing you put on a button. So the product has its own names,
/// and this is the only place that maps a name to what actually runs.
///
/// THE NAMING SCHEME. <c>&lt;Familie&gt; &lt;Variant&gt;</c>, in Danish, describing the
/// *result* rather than the technology. "Ordret" is the family: every word as it was said,
/// nothing summarised, nothing tidied. A variant may be added when we can say what it does
/// *for the reader*; a variant may never be named after the model behind it, because that
/// changes, and a customer reading "gpt-4o" on their meeting learns nothing they can act on.
///
/// STABILITY. <c>Id</c> is written into every transcript manifest and stays there for as long
/// as the transcript does. It is deliberately ugly and deliberately frozen — <c>two-engine</c>
/// and <c>words-only</c> are on disk in recordings made by the meeting server before pks could
/// produce them at all, and the two must keep meaning the same thing. The display names are
/// strings and can change freely; the ids cannot.
/// </summary>
public sealed record TranscriptionMethod(
    string Id,
    string Name,
    string Tagline,
    bool Chooseable,
    bool NeedsSecondDiariser,
    string WordsProvider,
    string? LabelsProvider);

public static class TranscriptionMethods
{
    public const string Default = "two-engine";

    public static readonly IReadOnlyList<TranscriptionMethod> All =
    [
        new(
            Id: "two-engine",
            Name: "Ordret",
            Tagline: "Hurtig. Ordret tekst, talere fundet på lyden.",
            Chooseable: true,
            NeedsSecondDiariser: false,
            WordsProvider: "foundry-enhanced",
            LabelsProvider: "foundry-fast"),

        new(
            Id: "foundry-diarize",
            Name: "Ordret Skarp",
            Tagline: "Langsommere. Samme ord, skarpere skift mellem talere.",
            Chooseable: true,
            NeedsSecondDiariser: true,
            WordsProvider: "foundry-enhanced",
            LabelsProvider: "foundry-gpt4o-diarize"),

        new(
            // Not a choice but an outcome: this is what the transcript looks like when
            // diarisation failed. Every word is still there — a transcript without names is
            // still the record; an empty one is not.
            Id: "words-only",
            Name: "Kun ord",
            Tagline: "Ordret tekst uden talere.",
            Chooseable: false,
            NeedsSecondDiariser: false,
            WordsProvider: "foundry-enhanced",
            LabelsProvider: null),
    ];

    public static TranscriptionMethod? Find(string? id)
        => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The method a run should use, given what a caller asked for. Anything unknown or
    /// unchooseable falls back to the default rather than failing — a stored method id
    /// outlives the configuration that made it possible, and a transcription that refuses to
    /// start because a credential was removed six months ago is the wrong failure.
    /// </summary>
    public static TranscriptionMethod Resolve(string? id)
    {
        var method = Find(id);

        return method is { Chooseable: true } ? method : Find(Default)!;
    }

    public static string DisplayName(string id) => Find(id)?.Name ?? id;
}
