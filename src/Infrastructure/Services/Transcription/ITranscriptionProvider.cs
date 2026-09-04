namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// What to ask an engine for. One request object for every provider, because the point of
/// the abstraction is that a benchmark can send the same question to all of them.
/// </summary>
public sealed record TranscribeRequest
{
    /// <summary>
    /// BCP-47 tag. Note that the two Azure paths disagree about the form and this is not a
    /// typo anywhere downstream: plain diarisation rejects "da" with InvalidLocale and wants
    /// "da-DK", while the MAI enhanced path takes "da". Each provider narrows this itself.
    /// </summary>
    public string Locale { get; init; } = "da-DK";

    /// <summary>Ask for speaker labels. Providers that cannot diarise ignore it.</summary>
    public bool Diarize { get; init; }

    /// <summary>Upper bound on speakers. Clamped by the provider to what the engine accepts.</summary>
    public int MaxSpeakers { get; init; } = 6;

    /// <summary>Engine-specific model or deployment id. Null means the provider's own default.</summary>
    public string? Model { get; init; }

    /// <summary>Names and terms worth biasing towards. Truncated by the provider to its own cap.</summary>
    public IReadOnlyList<string> PhraseList { get; init; } = [];
}

/// <summary>
/// One engine's answer, plus the raw body it came in. The raw body is written to disk before
/// anything is merged — that is what makes a mislabelled word answerable later instead of
/// guessed at, and what lets the merge be re-run offline against real meetings.
/// </summary>
public sealed record TranscriptionResponse(SpeechResult Result, string RawJson, long ElapsedMs);

/// <summary>
/// A speech-to-text engine pks can send audio to.
///
/// Same shape as the house's other provider abstractions (<c>IVmProvider</c>,
/// <c>IFileShareProvider</c>): an interface, a key, an auth check, and a registry that
/// resolves one by key.
/// </summary>
public interface ITranscriptionProvider
{
    /// <summary>Stable id used on the command line and written into the transcript manifest.</summary>
    string ProviderKey { get; }

    /// <summary>What to call it in output meant for a person.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this engine answers *who spoke when*. The two-engine method needs exactly one
    /// provider that does and one that does not.
    /// </summary>
    bool ProvidesSpeakerLabels { get; }

    /// <summary>
    /// Whether this engine returns per-word timings. Without them the merge falls back to
    /// spreading a phrase's duration over its words, which is right enough for alignment.
    /// </summary>
    bool ProvidesWordTimings { get; }

    Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribe one WAV. <paramref name="wav"/> is a complete 16 kHz mono s16le file,
    /// already cut to a size the engine accepts — chunking is the pipeline's job, not the
    /// provider's.
    /// </summary>
    Task<TranscriptionResponse> TranscribeAsync(
        byte[] wav, TranscribeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parse a response body this provider previously returned, without calling anything.
    ///
    /// This is what makes the saved <c>raw/engine/</c> files worth saving: the merge can be
    /// re-run over yesterday's engine output and must produce yesterday's transcript. Each
    /// provider parses its own wire format, so a provider that reshapes what it receives
    /// reshapes it identically on replay.
    /// </summary>
    SpeechResult ParseRaw(string rawJson);
}

/// <summary>
/// Resolves transcription providers by key. Mirrors <c>VmProviderRegistry</c> and
/// <c>FileShareProviderRegistry</c>.
/// </summary>
public class TranscriptionProviderRegistry
{
    private readonly IEnumerable<ITranscriptionProvider> _providers;

    public TranscriptionProviderRegistry(IEnumerable<ITranscriptionProvider> providers)
    {
        _providers = providers;
    }

    public IEnumerable<ITranscriptionProvider> GetAllProviders() => _providers;

    public ITranscriptionProvider? Get(string key)
        => _providers.FirstOrDefault(p => string.Equals(p.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a key, or throw with the list of keys that would have worked. A typo in a
    /// provider name should not read like a network failure.
    /// </summary>
    public ITranscriptionProvider Require(string key)
        => Get(key) ?? throw new ArgumentException(
            $"Unknown transcription provider '{key}'. Known: {string.Join(", ", _providers.Select(p => p.ProviderKey))}");
}
