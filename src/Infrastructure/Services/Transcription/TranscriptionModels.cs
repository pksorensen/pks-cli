using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// One word as an engine reported it. Azure fast transcription returns these for
/// most locales; some return none, and <see cref="Alignment.DiarisedWords"/> then
/// spreads the phrase duration over its words instead.
/// </summary>
public sealed class SpeechWord
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("offsetMilliseconds")] public long OffsetMilliseconds { get; set; }
    [JsonPropertyName("durationMilliseconds")] public long DurationMilliseconds { get; set; }
}

/// <summary>
/// One diarised phrase: a run of speech attributed to one speaker.
/// </summary>
public sealed class SpeechPhrase
{
    /// <summary>
    /// Azure sends an integer, the gpt-4o diariser sends a string like "speaker_1",
    /// and a phrase with no diarisation has none at all. All three are kept as the
    /// string they will be compared as — never invented when missing.
    /// </summary>
    [JsonPropertyName("speaker")]
    [JsonConverter(typeof(SpeakerLabelConverter))]
    public string? Speaker { get; set; }

    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("offsetMilliseconds")] public long OffsetMilliseconds { get; set; }
    [JsonPropertyName("durationMilliseconds")] public long DurationMilliseconds { get; set; }
    [JsonPropertyName("words")] public List<SpeechWord>? Words { get; set; }
}

public sealed class SpeechCombinedPhrase
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>
/// A fast-transcription response. The verbatim engine fills only
/// <see cref="CombinedPhrases"/>; the diarising engine fills both.
/// </summary>
public sealed class SpeechResult
{
    [JsonPropertyName("combinedPhrases")] public List<SpeechCombinedPhrase>? CombinedPhrases { get; set; }
    [JsonPropertyName("phrases")] public List<SpeechPhrase>? Phrases { get; set; }
}

/// <summary>
/// Reads a speaker label whether the engine wrote it as a number, a string or null.
/// </summary>
public sealed class SpeakerLabelConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var n)
                ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException($"Unexpected speaker label token {reader.TokenType}"),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>Anything carrying a speaker label, so two engines' output can be compared.</summary>
public interface ISpeakerLabelled
{
    string? Speaker { get; }
}

/// <summary>A word from the diarising engine: the label and the timing, not the text of record.</summary>
public sealed class DiarisedWord : ISpeakerLabelled
{
    public string Key { get; init; } = "";
    public string? Speaker { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
}

/// <summary>
/// A verbatim word carrying a diarised label. <see cref="Raw"/> is the record and is
/// never rewritten — the merge only ever decides <see cref="Speaker"/>.
/// </summary>
public sealed class LabelledWord : ISpeakerLabelled
{
    /// <summary>As the verbatim engine wrote it, punctuation and all.</summary>
    public string Raw { get; init; } = "";

    /// <summary>Comparison form: lowercase, letters and digits only.</summary>
    public string Key { get; init; } = "";

    public string? Speaker { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }

    /// <summary>True when this word matched a diarised word directly rather than inheriting a neighbour's label.</summary>
    public bool Anchored { get; set; }

    /// <summary>True when the sentence-edge pass moved this word's label. The word itself is untouched.</summary>
    public bool Snapped { get; set; }
}

/// <summary>Consecutive words by one speaker.</summary>
public sealed class Turn
{
    public string? Speaker { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// How much two diarisers agree, after fitting their arbitrary label names to each other.
/// </summary>
public readonly record struct LabelAgreementResult(
    double Agreement,
    int Compared,
    IReadOnlyDictionary<string, string> Mapping);
