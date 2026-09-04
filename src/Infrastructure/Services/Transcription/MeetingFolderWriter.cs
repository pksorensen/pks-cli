using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// Writes the meeting folder. The format is documented in <c>docs/meeting-folder-format.md</c>;
/// this class is its only emitter, so a change here is a change to the format and belongs in
/// that document in the same commit.
///
/// <code>
/// &lt;date&gt;-&lt;slug&gt;/
/// ├─ README.md          summary + agreed actions — a STUB here; judgement is not CLI output
/// ├─ meeting.json       manifest: what was recorded, what transcribed it, who the speakers are
/// ├─ transcript.md      readable: speaker + timestamp
/// ├─ transcript.json    machine-readable turns
/// ├─ transcript.srt
/// └─ raw/
///    ├─ audio.wav       gitignored
///    ├─ engine/         raw engine responses per part — replayable without a second bill
///    └─ turns.jsonl
/// </code>
///
/// TWO RULES THAT HAVE ALREADY COST TIME.
///
/// The <c>raw/</c> nesting is not cosmetic: the workspace gitignore matches
/// <c>customers/**/meetings/**/raw/*.{mp4,mp3,wav,m4a}</c>, so media anywhere else in the
/// folder gets committed.
///
/// A speaker the diariser invented is named <c>(overlap)</c>, never given a person's name.
/// One real meeting produced a fourth "speaker" that was twenty-nine sub-second fragments of
/// crosstalk; naming it would have put words in someone's mouth.
/// </summary>
public sealed class MeetingFolderWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The label a diariser produced when it heard crosstalk rather than a person.</summary>
    public const string OverlapSpeaker = "(overlap)";

    public sealed record Options
    {
        public required string Directory { get; init; }
        public required string Title { get; init; }
        public required DateOnly Date { get; init; }

        /// <summary>Diariser label → the person's name. Labels with no entry keep "Taler N".</summary>
        public IReadOnlyDictionary<string, string> Speakers { get; init; } = new Dictionary<string, string>();

        /// <summary>The recording this came from, for the manifest. Null when transcribing a stray WAV.</summary>
        public string? SourcePath { get; init; }

        /// <summary>Overwrite an existing README.md. Off by default: the README carries judgement.</summary>
        public bool OverwriteReadme { get; init; }
    }

    public IReadOnlyList<string> Write(TranscriptResult transcript, Options options)
    {
        Directory.CreateDirectory(options.Directory);
        var written = new List<string>();

        var named = transcript.Turns
            .Select((t, i) => new NamedTurn(i, SpeakerName(t.Speaker, options.Speakers), t.StartMs, t.EndMs, t.Text))
            .ToList();

        Write(options.Directory, "transcript.md", RenderMarkdown(named, transcript, options), written);
        Write(options.Directory, "transcript.srt", RenderSrt(named, transcript, options.Speakers), written);
        Write(options.Directory, "transcript.json", JsonSerializer.Serialize(
            BuildTranscriptDocument(named, transcript, options), Json), written);
        Write(options.Directory, "meeting.json", JsonSerializer.Serialize(
            BuildManifest(transcript, options), Json), written);

        var rawDirectory = Path.Combine(options.Directory, "raw");
        Directory.CreateDirectory(rawDirectory);
        Write(rawDirectory, "turns.jsonl", string.Concat(named.Select(t =>
            JsonSerializer.Serialize(t, new JsonSerializerOptions(Json) { WriteIndented = false }) + "\n")), written);

        var readmePath = Path.Combine(options.Directory, "README.md");
        if (options.OverwriteReadme || !File.Exists(readmePath))
        {
            Write(options.Directory, "README.md", RenderReadmeStub(transcript, options), written);
        }

        return written;
    }

    private static void Write(string directory, string name, string content, List<string> written)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        written.Add(path);
    }

    private static string SpeakerName(string? label, IReadOnlyDictionary<string, string> speakers)
    {
        if (label is null) return "Ukendt";
        if (speakers.TryGetValue(label, out var name)) return name;

        return $"Taler {label}";
    }

    private sealed record NamedTurn(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("startMs")] long StartMs,
        [property: JsonPropertyName("endMs")] long EndMs,
        [property: JsonPropertyName("text")] string Text);

    // ── rendering ───────────────────────────────────────────────────────────────

    private static string Hms(long ms)
    {
        var total = ms / 1000.0;
        var h = (int)(total / 3600);
        var m = (int)(total % 3600 / 60);
        var s = (int)(total % 60);

        return $"{h}:{m:00}:{s:00}";
    }

    private static string SrtTime(long ms)
        => $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}";

    private static string RenderMarkdown(List<NamedTurn> turns, TranscriptResult transcript, Options options)
    {
        var participants = turns.Select(t => t.Speaker).Distinct().ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# {options.Title}").AppendLine();
        sb.Append("**Dato:** ").Append(options.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
          .Append(" · **Længde:** ").Append(Hms(transcript.DurationMs))
          .Append(" · **Deltagere:** ").AppendLine(string.Join(", ", participants)).AppendLine();

        sb.AppendLine($"> Ord: `{transcript.WordsProviderKey}`. "
            + (transcript.LabelsProviderKey is null
                ? "Ingen talergenkendelse — transskriptionen er ordret, men uden navne."
                : $"Talere: `{transcript.LabelsProviderKey}`, flettet ord for ord "
                  + $"({transcript.AnchoredRatio * 100:0} % forankret, {transcript.SnappedWords} ord trukket tilbage "
                  + "til deres sætning)."));
        sb.AppendLine($"> Metode `{transcript.Method}` ({TranscriptionMethods.DisplayName(transcript.Method)}), "
            + $"locale {transcript.Locale}. Ordret — inkl. øh og falske starter.").AppendLine();
        if (transcript.LabelScope == DiarisationScopes.Part)
        {
            sb.AppendLine("> **Talerne er kun sammenlignelige inden for hver del.** Optagelsen var for lang "
                + "til at diarisere i ét kald, så et navn gælder den del det står i — `2.1` er del 2's "
                + "taler 1 og er ikke nødvendigvis den samme person som `1.1`.").AppendLine();
        }
        sb.AppendLine("---").AppendLine();

        string? last = null;
        foreach (var turn in turns)
        {
            if (turn.Speaker != last)
            {
                sb.AppendLine($"**{turn.Speaker}** · `{Hms(turn.StartMs)}`  ");
                last = turn.Speaker;
            }
            else
            {
                sb.AppendLine($"*`{Hms(turn.StartMs)}`*  ");
            }
            sb.AppendLine(turn.Text).AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>A cue that runs longer than this is not a subtitle any more.</summary>
    private const long MaxCueMs = 6_000;

    /// <summary>Two lines of readable width.</summary>
    private const int MaxCueChars = 84;


    /// <summary>
    /// Subtitles are cut from the words, not from the turns. A turn in this format is however
    /// long one person kept talking — a minute is ordinary — and a subtitle that sits on
    /// screen for a minute holding a thousand characters is not a subtitle. Only the word
    /// timings know where inside the turn a cue should end.
    ///
    /// Without word timings (a <c>words-only</c> run, where diarisation failed) there is
    /// nothing to cut on, so the turns are emitted as-is. Long cues then, but honest ones:
    /// invented timings would put words on screen at times nobody said them.
    /// </summary>
    private static string RenderSrt(
        List<NamedTurn> turns, TranscriptResult transcript, IReadOnlyDictionary<string, string> speakers)
    {
        var cues = transcript.LabelsProviderKey is null || transcript.Words.Count == 0
            ? turns.Select(t => (t.Speaker, t.StartMs, t.EndMs, t.Text)).ToList()
            : CutCues(transcript.Words, speakers);

        var sb = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var (speaker, startMs, endMs, text) = cues[i];
            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{SrtTime(startMs)} --> {SrtTime(Math.Max(endMs, startMs + 800))}");
            sb.AppendLine($"{speaker}: {text}").AppendLine();
        }

        return sb.ToString();
    }

    private static List<(string Speaker, long StartMs, long EndMs, string Text)> CutCues(
        IReadOnlyList<LabelledWord> words, IReadOnlyDictionary<string, string> speakers)
    {
        var cues = new List<(string, long, long, string)>();
        var current = new List<LabelledWord>();

        void Flush()
        {
            if (current.Count == 0) return;
            cues.Add((
                SpeakerName(current[0].Speaker, speakers),
                current[0].StartMs,
                current[^1].EndMs,
                string.Join(" ", current.Select(w => w.Raw)).Trim()));
            current.Clear();
        }

        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (current.Count > 0 && current[0].Speaker != word.Speaker) Flush();
            current.Add(word);

            var chars = current.Sum(w => w.Raw.Length + 1);
            var span = current[^1].EndMs - current[0].StartMs;
            // Same sentence test the merge uses, so a cue never breaks after "f.eks." while
            // the alignment is treating that full stop as mid-sentence.
            var endsSentence = Alignment.EndsSentence(word, i + 1 < words.Count ? words[i + 1] : null);
            if (endsSentence || chars >= MaxCueChars || span >= MaxCueMs) Flush();
        }

        Flush();

        return cues;
    }

    private static object BuildTranscriptDocument(List<NamedTurn> turns, TranscriptResult transcript, Options options)
        => new
        {
            title = options.Title,
            date = options.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            durationSec = Math.Round(transcript.DurationMs / 1000.0, 1),
            method = transcript.Method,
            methodName = TranscriptionMethods.DisplayName(transcript.Method),
            engine = transcript.Engine,
            locale = transcript.Locale,
            providers = new { words = transcript.WordsProviderKey, labels = transcript.LabelsProviderKey },
            labelScope = transcript.LabelScope,
            anchoredRatio = Math.Round(transcript.AnchoredRatio, 4),
            snappedWords = transcript.SnappedWords,
            speakerCount = transcript.SpeakerCount,
            speakers = options.Speakers,
            turns = turns.Select(t => new
            {
                index = t.Index,
                speaker = t.Speaker,
                start = Math.Round(t.StartMs / 1000.0, 2),
                end = Math.Round(t.EndMs / 1000.0, 2),
                text = t.Text,
            }),
        };

    private static object BuildManifest(TranscriptResult transcript, Options options)
    {
        object? source = null;
        if (options.SourcePath is not null && File.Exists(options.SourcePath))
        {
            var info = new FileInfo(options.SourcePath);
            source = new
            {
                name = info.Name,
                bytes = info.Length,
                // The transcript is derived from exactly this file. When a recording is
                // re-uploaded or re-encoded, the hash is what says whether the transcript
                // still describes it.
                sha256 = Sha256(options.SourcePath),
            };
        }

        return new
        {
            formatVersion = 1,
            title = options.Title,
            date = options.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            generatedAt = transcript.GeneratedAt.ToString("O"),
            durationMs = transcript.DurationMs,
            method = transcript.Method,
            engine = transcript.Engine,
            locale = transcript.Locale,
            providers = new { words = transcript.WordsProviderKey, labels = transcript.LabelsProviderKey },
            parts = transcript.PartCount,
            apiCalls = transcript.ApiCalls,
            labelScope = transcript.LabelScope,
            anchoredRatio = Math.Round(transcript.AnchoredRatio, 4),
            snappedWords = transcript.SnappedWords,
            speakers = options.Speakers,
            source,
            producedBy = "pks transcribe",
        };
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();

        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// A stub, deliberately. The actions table is the reason the folder exists and it is
    /// judgement, not extraction — an agent or a person fills it in from the transcript. What
    /// the CLI can honestly write is the frame and the facts of the run.
    /// </summary>
    private static string RenderReadmeStub(TranscriptResult transcript, Options options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {options.Title}").AppendLine();
        sb.Append("**Dato:** ").Append(options.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
          .Append(" · **Længde:** ").Append(Hms(transcript.DurationMs))
          .Append(" · **Transskription:** [transcript.md](transcript.md)").AppendLine().AppendLine();

        sb.AppendLine("## Aftalte actions").AppendLine();
        sb.AppendLine("| # | Action | Ejer | Hvornår | Kilde |");
        sb.AppendLine("|---|---|---|---|---|");
        sb.AppendLine("| 1 | _udfyld fra transskriptionen_ | | | `0:00:00` |").AppendLine();
        sb.AppendLine("## Beslutninger").AppendLine();
        sb.AppendLine("| Beslutning | Begrundelse | Kilde |");
        sb.AppendLine("|---|---|---|").AppendLine();
        sb.AppendLine("## Løse ender").AppendLine();

        sb.AppendLine("---").AppendLine();
        sb.AppendLine("<!-- Skrevet af `pks transcribe`. Alt over denne linje er til at udfylde;");
        sb.AppendLine("     alt under den er kørslens egne tal. -->").AppendLine();
        sb.AppendLine($"Metode `{transcript.Method}` ({TranscriptionMethods.DisplayName(transcript.Method)}) · "
            + $"motor `{transcript.Engine}` · ord `{transcript.WordsProviderKey}` · "
            + $"talere `{transcript.LabelsProviderKey ?? "ingen"}` · "
            + $"{transcript.PartCount} del(e), {transcript.ApiCalls} API-kald · "
            + $"{transcript.AnchoredRatio * 100:0} % forankret · {transcript.SnappedWords} ord trukket tilbage.");

        if (transcript.Engine == "words-only")
        {
            sb.AppendLine().AppendLine("> **Ingen talere.** Talergenkendelsen svarede ikke, så ordene står uden navne.");
            sb.AppendLine("> Transskriptionen er stadig referatet — den er bare anonym.");
        }

        return sb.ToString();
    }
}
