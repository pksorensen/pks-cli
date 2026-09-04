using System.Text.Json;

namespace PKS.Infrastructure.Services.Transcription;

public sealed record TranscriptionPipelineOptions
{
    /// <summary>
    /// Re-run the merge over the engine responses already saved under <c>raw/engine/</c>
    /// instead of calling the engines again. The point of writing those files is that a
    /// mislabelled turn can be investigated, and a fix to the merge verified, without paying
    /// for the same audio twice. A part with no saved response is transcribed normally.
    /// </summary>
    public bool Replay { get; init; }

    /// <summary>The recording. Any container ffmpeg reads, or a WAV, which skips ffmpeg.</summary>
    public required string MediaPath { get; init; }

    /// <summary>The meeting folder to write. <c>raw/</c> is created inside it.</summary>
    public required string OutputDirectory { get; init; }

    public string Locale { get; init; } = "da-DK";
    public int MaxSpeakers { get; init; } = 6;
    public string MethodId { get; init; } = TranscriptionMethods.Default;

    /// <summary>Names and terms worth biasing the words engine towards — typically the roster.</summary>
    public IReadOnlyList<string> PhraseList { get; init; } = [];

    public int PartSeconds { get; init; } = AudioPreparation.DefaultPartSeconds;

    /// <summary>Skip diarisation entirely and produce a words-only transcript.</summary>
    public bool WordsOnly { get; init; }

    /// <summary>Provider key override for the words engine. Null uses the method's own.</summary>
    public string? WordsProvider { get; init; }

    /// <summary>Provider key override for the labels engine. Null uses the method's own.</summary>
    public string? LabelsProvider { get; init; }
}

/// <summary>What a run produced, before it is rendered into files.</summary>
public sealed record TranscriptResult
{
    /// <summary>
    /// What actually ran. Differs from <see cref="Method"/> exactly when a chosen engine did
    /// not answer, and that difference is worth keeping: someone who asked for Ordret Skarp
    /// and got Ordret should be able to find out.
    /// </summary>
    public required string Engine { get; init; }

    /// <summary>What was asked for.</summary>
    public required string Method { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }
    public required long DurationMs { get; init; }
    public required List<Turn> Turns { get; init; }
    public required double AnchoredRatio { get; init; }
    public required int SpeakerCount { get; init; }
    public required int SnappedWords { get; init; }
    public required int PartCount { get; init; }
    public required int ApiCalls { get; init; }
    public required string Locale { get; init; }
    public required string WordsProviderKey { get; init; }
    public string? LabelsProviderKey { get; init; }

    /// <summary>
    /// <see cref="DiarisationScopes"/> — how far a speaker label can be trusted. Null when
    /// there are no labels. Anything other than <c>recording</c> means a name given with
    /// <c>--speakers</c> applies to one part only, and the transcript says so.
    /// </summary>
    public string? LabelScope { get; init; }

    /// <summary>
    /// Every word with its label and timing, in order. Turns are built from these, but the
    /// subtitle writer needs them unmerged: a turn is a minute long and a subtitle cue is a
    /// few seconds, and only the words know where inside the turn those seconds fall.
    /// </summary>
    public required IReadOnlyList<LabelledWord> Words { get; init; }
}

/// <summary>
/// Transcription pipeline for a recording on disk.
///
/// Shape of one run, per part of at most fifteen minutes:
///
///   PCM range → WAV → ┬ diarisation → who spoke when, per-word timings
///                     └ verbatim    → what was said, no timings at all
///                            ↓
///                        Alignment  → verbatim words carrying diarised labels
///                            ↓
///                   SnapSentenceEdges → boundary noise pulled back to its sentence
///
/// The two calls are issued together because they are independent; the parts run one after
/// another because each holds a WAV in memory.
///
/// A FAILED DIARISATION MUST NOT COST US THE WORDS. A transcript with no speaker labels is
/// still the artefact of record; an empty one is not. So the diarising call is allowed to
/// fail and the run degrades to <c>words-only</c>, saying so in <see cref="TranscriptResult.Engine"/>.
///
/// Both raw responses are written to disk before anything is merged. That is not for the
/// product — it is so a future question about a mislabelled word can be answered from the
/// engine output instead of guessed at, and so the merge can be re-run offline against real
/// meetings without a second bill.
/// </summary>
/// <summary>
/// How far a speaker label can be trusted to mean the same person.
/// </summary>
public static class DiarisationScopes
{
    /// <summary>One diarisation call covered the whole recording. A label is one person.</summary>
    public const string Recording = "recording";

    /// <summary>The recording was diarised in parts. A label means one person within its part only.</summary>
    public const string Part = "part";
}

public sealed class TranscriptionPipeline
{
    /// <summary>
    /// Azure fast transcription accepts two hours of audio in one request. Meetings are
    /// shorter than that; the limit exists so the fallback below has a trigger, not because
    /// anything routine reaches it.
    /// </summary>
    private const long MaxWholeFileDiarisationMs = 2L * 60 * 60 * 1000;

    private readonly TranscriptionProviderRegistry _providers;

    public TranscriptionPipeline(TranscriptionProviderRegistry providers)
    {
        _providers = providers;
    }

    public async Task<TranscriptResult> RunAsync(
        TranscriptionPipelineOptions options,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= _ => { };

        var rawDirectory = Path.Combine(options.OutputDirectory, "raw");
        var engineDirectory = Path.Combine(rawDirectory, "engine");
        Directory.CreateDirectory(engineDirectory);

        var wavPath = await ResolveWavAsync(options.MediaPath, rawDirectory, progress, cancellationToken);
        var pcm = AudioPreparation.ReadPcm(wavPath);
        if (pcm.Length < AudioPreparation.MinTranscribableBytes)
        {
            throw new InvalidOperationException(
                $"{options.MediaPath} holds {pcm.Length} bytes of audio — less than a second. Nothing to transcribe.");
        }

        var method = TranscriptionMethods.Resolve(options.MethodId);
        var wordsProvider = _providers.Require(options.WordsProvider ?? method.WordsProvider);
        var labelsKey = options.WordsOnly ? null : options.LabelsProvider ?? method.LabelsProvider;
        var labelsProvider = labelsKey is null ? null : _providers.Require(labelsKey);

        var parts = AudioPreparation.PlanParts(pcm.Length, options.PartSeconds);
        progress($"{parts.Count} part(s), {AudioPreparation.PcmDurationMs(pcm.Length) / 1000}s of audio");

        var engine = labelsProvider is null ? "words-only" : method.Id;
        var apiCalls = 0;

        // ── who, over the whole recording ───────────────────────────────────────
        //
        // MEASURED 2026-08-24, and the reason this call is not inside the part loop.
        // Diariser labels are scoped to the request that produced them: on the 35-minute
        // museliving status call, diarised per part, label 1 was Poul in part 1, Jakob in
        // part 2 and Jakob again in part 3. Per part the labelling was 91-97 % right; across
        // parts a --speakers mapping was correct for one part in three, and nothing in the
        // output said so. One call over the whole file is what makes a label mean one person.
        List<DiarisedWord> diarised = [];
        var labelScope = labelsProvider is null ? null : DiarisationScopes.Recording;
        if (labelsProvider is not null)
        {
            var who = await DiariseAsync(
                options, engineDirectory, labelsProvider, pcm, progress, cancellationToken);
            apiCalls += who.ApiCalls;
            if (who.Words.Count == 0)
            {
                engine = "words-only";
                labelsProvider = null;
                labelScope = null;
            }
            else
            {
                diarised = who.Words;
                labelScope = who.Scope;
            }
        }

        var verbatim = new List<string>();
        var partWordCounts = new List<int>();

        // ── what, part by part ──────────────────────────────────────────────────
        //
        // The words engine has no timings and no labels, so a part boundary costs it
        // nothing beyond at most one word cut in half. It is split only so that one failed
        // call does not cost the whole recording.
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wav = AudioPreparation.PcmToWav(pcm.AsSpan((int)part.Start, (int)part.Length));
            var request = new TranscribeRequest
            {
                Locale = options.Locale,
                Diarize = false,
                MaxSpeakers = options.MaxSpeakers,
                PhraseList = options.PhraseList,
            };

            var replayed = Replayed(options, engineDirectory, wordsProvider, $"part{part.Index}-verbatim");
            var words = replayed ?? await wordsProvider.TranscribeAsync(wav, request, cancellationToken);
            if (replayed is null) apiCalls++;

            WriteEngineOutput(engineDirectory, $"part{part.Index}-verbatim", words.RawJson);

            var partWords = Alignment.VerbatimWords(words.Result);
            verbatim.AddRange(partWords);
            partWordCounts.Add(partWords.Count);
            progress($"  part {part.Index + 1}/{parts.Count} — {partWords.Count} words");
        }

        // ── the merge, once, over everything ────────────────────────────────────
        //
        // Matching the whole verbatim run against the whole diarised run rather than part
        // against part: a sentence spanning a part boundary is one sentence again, and the
        // sequence matcher has more anchors to work with than either half had alone.
        var allWords = Alignment.LabelWords(verbatim, diarised);
        var snapped = 0;
        if (labelsProvider is not null)
        {
            snapped = Alignment.SnapSentenceEdges(allWords);
        }
        else
        {
            // No labels means no timings either. Anchor each part's words to the part it came
            // from so the combined timeline stays monotone and the transcript still says when.
            var index = 0;
            foreach (var part in parts)
            {
                var partEnd = part.OffsetMs + AudioPreparation.PcmDurationMs(part.Length);
                var count = partWordCounts[part.Index];
                for (var i = 0; i < count && index < allWords.Count; i++, index++)
                {
                    allWords[index].StartMs = part.OffsetMs;
                    allWords[index].EndMs = partEnd;
                }
            }
        }

        var turns = Alignment.TurnsFromWords(allWords);

        var merged = MergeAdjacent(turns);

        return new TranscriptResult
        {
            Engine = engine,
            Method = method.Id,
            GeneratedAt = DateTimeOffset.UtcNow,
            DurationMs = AudioPreparation.PcmDurationMs(pcm.Length),
            Turns = merged,
            AnchoredRatio = Alignment.AnchoredRatio(allWords),
            SpeakerCount = merged.Select(t => t.Speaker).Where(s => s is not null).Distinct().Count(),
            SnappedWords = snapped,
            PartCount = parts.Count,
            ApiCalls = apiCalls,
            Locale = options.Locale,
            WordsProviderKey = wordsProvider.ProviderKey,
            LabelsProviderKey = labelsProvider?.ProviderKey,
            LabelScope = labelScope,
            Words = allWords,
        };
    }

    /// <summary>
    /// The same audio through several words engines, so a claim that one is better than
    /// another is a measurement rather than an impression.
    ///
    /// No gold transcript is involved, and that is deliberate: WER against a reference is a
    /// separate job (it needs a labelled corpus, and the scorer is byte-parity-gated against
    /// an existing implementation). What this gives without any of that is word counts,
    /// wall-clock cost, and how far each engine's words agree with the first one's — which
    /// is enough to tell "these two heard the same meeting" from "one of these is guessing".
    /// </summary>
    public async Task<List<ComparisonRun>> CompareAsync(
        TranscriptionPipelineOptions options,
        IReadOnlyList<string> providerKeys,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= _ => { };

        var rawDirectory = Path.Combine(options.OutputDirectory, "raw");
        var engineDirectory = Path.Combine(rawDirectory, "engine");
        Directory.CreateDirectory(engineDirectory);

        var wavPath = await ResolveWavAsync(options.MediaPath, rawDirectory, progress, cancellationToken);
        var pcm = AudioPreparation.ReadPcm(wavPath);
        var parts = AudioPreparation.PlanParts(pcm.Length, options.PartSeconds);
        var runs = new List<ComparisonRun>();

        foreach (var key in providerKeys)
        {
            var provider = _providers.Require(key);
            var request = new TranscribeRequest
            {
                Locale = options.Locale,
                Diarize = false,
                MaxSpeakers = options.MaxSpeakers,
                PhraseList = options.PhraseList,
            };

            var words = new List<string>();
            var elapsedMs = 0L;
            string? failure = null;
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = $"compare-{Sanitise(key)}-part{part.Index}";
                try
                {
                    var replayed = Replayed(options, engineDirectory, provider, name);
                    var response = replayed ?? await provider.TranscribeAsync(
                        AudioPreparation.PcmToWav(pcm.AsSpan((int)part.Start, (int)part.Length)),
                        request, cancellationToken);

                    WriteEngineOutput(engineDirectory, name, response.RawJson);
                    words.AddRange(Alignment.VerbatimWords(response.Result));
                    elapsedMs += response.ElapsedMs;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One engine failing is a result about that engine, not a failed
                    // comparison. It goes in the table with its reason.
                    failure = ex.Message;

                    break;
                }
            }

            progress(failure is null
                ? $"  {key} — {words.Count} words in {elapsedMs / 1000.0:0.0}s"
                : $"  {key} — failed: {failure}");
            runs.Add(new ComparisonRun(key, provider.DisplayName, words, elapsedMs, failure));
        }

        return runs;
    }

    /// <summary>
    /// How far two engines agree, word for word, using the same matcher the merge uses:
    /// matched words over the longer of the two runs. 1.0 is identical text; the number falls
    /// both when an engine mishears and when it drops audio, which is what makes it useful.
    /// </summary>
    public static double Agreement(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        return (double)Alignment.MatchWords(a, b).Count / Math.Max(a.Count, b.Count);
    }

    private static string Sanitise(string key)
        => new(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-').ToArray());

    /// <summary>One engine's pass over the audio in a comparison.</summary>
    public sealed record ComparisonRun(
        string ProviderKey, string DisplayName, List<string> Words, long ElapsedMs, string? Failure);

    /// <summary>
    /// Who spoke, over the whole recording in one call.
    ///
    /// Fast transcription takes a two-hour file, which is longer than any meeting this is
    /// aimed at, so the normal path is a single request and a single set of labels. A
    /// recording past that limit cannot be diarised in one call, and there is no way to tell
    /// from the responses alone that part 2's "speaker 1" is part 1's "speaker 3" — so the
    /// fallback diarises per part and <em>says so</em> in <see cref="TranscriptResult.LabelScope"/>,
    /// which the writer turns into a warning in the transcript. Labels that silently change
    /// meaning halfway through are worse than labels that admit they do.
    /// </summary>
    private static async Task<DiarisationRun> DiariseAsync(
        TranscriptionPipelineOptions options,
        string engineDirectory,
        ITranscriptionProvider provider,
        byte[] pcm,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var request = new TranscribeRequest
        {
            Locale = options.Locale,
            Diarize = true,
            MaxSpeakers = options.MaxSpeakers,
            PhraseList = [],
        };

        if (AudioPreparation.PcmDurationMs(pcm.Length) <= MaxWholeFileDiarisationMs)
        {
            var replayed = Replayed(options, engineDirectory, provider, "diarization");
            var response = replayed ?? await SafeAsync(provider, AudioPreparation.PcmToWav(pcm), request,
                progress, cancellationToken);
            if (response is null) return new DiarisationRun([], null, 0);

            WriteEngineOutput(engineDirectory, "diarization", response.RawJson);

            return new DiarisationRun(
                Alignment.DiarisedWords(response.Result, 0), DiarisationScopes.Recording, replayed is null ? 1 : 0);
        }

        progress($"Recording is longer than {MaxWholeFileDiarisationMs / 3_600_000} h — "
            + "diarising per part. Speaker labels will not be comparable across parts.");

        var words = new List<DiarisedWord>();
        var calls = 0;
        foreach (var part in AudioPreparation.PlanParts(pcm.Length, options.PartSeconds))
        {
            var name = $"part{part.Index}-diarization";
            var replayed = Replayed(options, engineDirectory, provider, name);
            var response = replayed ?? await SafeAsync(
                provider, AudioPreparation.PcmToWav(pcm.AsSpan((int)part.Start, (int)part.Length)),
                request, progress, cancellationToken);
            if (replayed is null) calls++;
            if (response is null) continue;

            WriteEngineOutput(engineDirectory, name, response.RawJson);
            // Labels are prefixed with the part they came from. They still do not line up
            // across parts, but at least they stop pretending to.
            foreach (var word in Alignment.DiarisedWords(response.Result, part.OffsetMs))
            {
                words.Add(new DiarisedWord
                {
                    Key = word.Key,
                    Speaker = word.Speaker is null ? null : $"{part.Index + 1}.{word.Speaker}",
                    StartMs = word.StartMs,
                    EndMs = word.EndMs,
                });
            }
        }

        return new DiarisationRun(words, words.Count == 0 ? null : DiarisationScopes.Part, calls);
    }

    private readonly record struct DiarisationRun(List<DiarisedWord> Words, string? Scope, int ApiCalls);

    /// <summary>
    /// The saved response for one part, parsed by the provider that produced it, or null when
    /// replay is off or nothing was saved.
    /// </summary>
    private static TranscriptionResponse? Replayed(
        TranscriptionPipelineOptions options,
        string engineDirectory,
        ITranscriptionProvider? provider,
        string name)
    {
        if (!options.Replay || provider is null) return null;

        var path = Path.Combine(engineDirectory, $"{name}.json");
        if (!File.Exists(path)) return null;

        var raw = File.ReadAllText(path);

        return new TranscriptionResponse(provider.ParseRaw(raw), raw, 0);
    }

    /// <summary>
    /// Run the diarising engine, and turn a failure into "no labels" rather than into a failed
    /// transcription. The reason is written out, not swallowed.
    /// </summary>
    private static async Task<TranscriptionResponse?> SafeAsync(
        ITranscriptionProvider provider, byte[] wav, TranscribeRequest request,
        Action<string> progress, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.TranscribeAsync(wav, request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress($"  diarisation failed ({provider.ProviderKey}): {ex.Message}");

            return null;
        }
    }

    private static async Task<string> ResolveWavAsync(
        string mediaPath, string rawDirectory, Action<string> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(mediaPath)) throw new FileNotFoundException($"Recording not found: {mediaPath}", mediaPath);

        // A WAV that is already 16 kHz mono s16le needs no ffmpeg — which is what lets a
        // machine without a codec toolchain transcribe a recorder app's output.
        if (Path.GetExtension(mediaPath).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return mediaPath;

        var destination = Path.Combine(rawDirectory, "audio.wav");
        progress($"extracting audio with ffmpeg → {destination}");

        return await AudioPreparation.ExtractWavAsync(mediaPath, destination, cancellationToken);
    }

    private static void WriteEngineOutput(string engineDirectory, string name, string rawJson)
        => File.WriteAllText(Path.Combine(engineDirectory, name + ".json"), rawJson);

    /// <summary>
    /// Two parts in a row can end and start with the same speaker; the part boundary is an
    /// artefact of how the audio was cut, not of who was talking.
    /// </summary>
    internal static List<Turn> MergeAdjacent(IEnumerable<Turn> turns)
    {
        var output = new List<Turn>();
        foreach (var turn in turns)
        {
            var last = output.Count > 0 ? output[^1] : null;
            if (last is not null && string.Equals(last.Speaker, turn.Speaker, StringComparison.Ordinal))
            {
                last.Text += " " + turn.Text;
                last.EndMs = Math.Max(last.EndMs, turn.EndMs);
            }
            else
            {
                output.Add(new Turn { Speaker = turn.Speaker, StartMs = turn.StartMs, EndMs = turn.EndMs, Text = turn.Text });
            }
        }

        return output;
    }

    /// <summary>
    /// Diarisation only — who spoke when, no verbatim pass. This is the lower half of the
    /// pipeline on its own, and what you reach for when the speakers look wrong.
    ///
    /// One call over the whole recording, for the reason spelled out on <see cref="DiariseAsync"/>:
    /// labels only mean anything within the request that produced them. This command exists to
    /// answer "which label is whom", so a label that changes meaning at minute fifteen would
    /// make it worse than useless — it would put two people under one name in the talk-time
    /// table and no reader could tell.
    /// </summary>
    public async Task<SpeechResult> DiarizeAsync(
        TranscriptionPipelineOptions options,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress ??= _ => { };

        var rawDirectory = Path.Combine(options.OutputDirectory, "raw");
        Directory.CreateDirectory(rawDirectory);

        var wavPath = await ResolveWavAsync(options.MediaPath, rawDirectory, progress, cancellationToken);
        var pcm = AudioPreparation.ReadPcm(wavPath);
        var provider = _providers.Require(options.LabelsProvider ?? "foundry-fast");
        var request = new TranscribeRequest
        {
            Locale = options.Locale,
            Diarize = true,
            MaxSpeakers = options.MaxSpeakers,
        };

        if (AudioPreparation.PcmDurationMs(pcm.Length) <= MaxWholeFileDiarisationMs)
        {
            var response = await provider.TranscribeAsync(
                AudioPreparation.PcmToWav(pcm), request, cancellationToken);
            progress($"  {response.Result.Phrases?.Count ?? 0} phrases over the whole recording");

            return response.Result;
        }

        progress($"Recording is longer than {MaxWholeFileDiarisationMs / 3_600_000} h — "
            + "diarising per part. Labels are prefixed with their part and do not line up across parts.");

        var phrases = new List<SpeechPhrase>();
        var combined = new List<SpeechCombinedPhrase>();
        foreach (var part in AudioPreparation.PlanParts(pcm.Length, options.PartSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wav = AudioPreparation.PcmToWav(pcm.AsSpan((int)part.Start, (int)part.Length));
            var response = await provider.TranscribeAsync(wav, request, cancellationToken);

            foreach (var phrase in response.Result.Phrases ?? [])
            {
                // Shift into the whole-file timeline, the same way the merge does.
                phrase.OffsetMilliseconds += part.OffsetMs;
                foreach (var w in phrase.Words ?? []) w.OffsetMilliseconds += part.OffsetMs;
                if (phrase.Speaker is not null) phrase.Speaker = $"{part.Index + 1}.{phrase.Speaker}";
                phrases.Add(phrase);
            }

            combined.AddRange(response.Result.CombinedPhrases ?? []);
            progress($"  part {part.Index + 1}/{AudioPreparation.PlanParts(pcm.Length, options.PartSeconds).Count}"
                + $" — {response.Result.Phrases?.Count ?? 0} phrases");
        }

        return new SpeechResult { Phrases = phrases, CombinedPhrases = combined };
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
