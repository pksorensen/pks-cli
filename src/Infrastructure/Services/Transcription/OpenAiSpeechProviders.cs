using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// The OpenAI-shaped transcription API: <c>POST /v1/audio/transcriptions</c>, multipart with
/// a <c>file</c> field. Everything from syv.ai to a self-hosted Whisper speaks it, which is
/// why it is a provider of its own rather than a special case.
///
/// Configure with PKS_TRANSCRIBE_OPENAI_URL and PKS_TRANSCRIBE_OPENAI_KEY (and optionally
/// PKS_TRANSCRIBE_OPENAI_MODEL). Nothing is hardcoded to one vendor: the benchmark that
/// motivated this provider compared five engines, and pinning one of them into the CLI would
/// make the sixth a code change.
/// </summary>
public sealed class OpenAiCompatibleTranscriptionProvider : ITranscriptionProvider
{
    private readonly HttpClient _http;

    public OpenAiCompatibleTranscriptionProvider(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromMinutes(15);
    }

    public string ProviderKey => "openai-compatible";
    public string DisplayName => "OpenAI-compatible transcription endpoint";
    public bool ProvidesSpeakerLabels => false;
    public bool ProvidesWordTimings => false;

    private static string? Url => Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_OPENAI_URL");
    private static string? Key => Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_OPENAI_KEY");
    private static string Model => Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_OPENAI_MODEL") ?? "whisper-1";

    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Key));

    public async Task<TranscriptionResponse> TranscribeAsync(
        byte[] wav, TranscribeRequest request, CancellationToken cancellationToken = default)
    {
        var url = Url ?? throw new InvalidOperationException(
            "openai-compatible: set PKS_TRANSCRIBE_OPENAI_URL to the /v1/audio/transcriptions endpoint.");

        var started = Stopwatch.StartNew();
        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "audio.wav");
        content.Add(new StringContent(request.Model ?? Model), "model");
        content.Add(new StringContent(BareLocale(request.Locale)), "language");

        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(Key))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }

        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"openai-compatible failed: {(int)response.StatusCode} {(body.Length <= 400 ? body : body[..400])}");
        }

        return new TranscriptionResponse(ParseRaw(body), body, started.ElapsedMilliseconds);
    }

    public SpeechResult ParseRaw(string rawJson)
        => new()
        {
            CombinedPhrases =
                [new SpeechCombinedPhrase { Text = JsonSerializer.Deserialize<OpenAiTranscription>(rawJson)?.Text ?? "" }],
        };

    private static string BareLocale(string locale)
    {
        var dash = locale.IndexOf('-');

        return dash > 0 ? locale[..dash] : locale;
    }

    private sealed class OpenAiTranscription
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}

/// <summary>
/// A second opinion on *who spoke when*, from a diarising model on the Azure OpenAI data
/// plane (<c>gpt-4o-transcribe-diarize</c>). Measured against the fast-transcription pair on
/// ten minutes of a real two-person Danish meeting (2026-08-07): 97.2 % of speech labelled
/// the same way, and visibly better wherever two people talk over each other — which is
/// precisely where the alignment merge is weakest.
///
/// IT MAY ONLY EVER SUPPLY LABELS. The same measurement found it returns 2141 words where
/// MAI returns 2761 at nearly identical audio coverage: it tidies. Tidying is the one thing
/// the transcript promises not to do, so its text is discarded and only the turn boundaries
/// survive. <see cref="ProvidesSpeakerLabels"/> is true and the words are never the record.
///
/// Two traps, both paid for once already:
///  - the modern <c>/openai/v1/…</c> path answers DeploymentNotFound on a perfectly healthy
///    deployment. The classic per-deployment path below works.
///  - <c>chunking_strategy</c> is *mandatory* for diarisation models — omit it and the
///    request fails rather than defaulting.
///
/// Slow and token-metered: ~3.7× realtime against fast transcription's 55×, ≈30k tokens per
/// ten minutes. It is opt-in for a reason.
/// </summary>
public sealed class FoundryDiarizeShadowProvider : ITranscriptionProvider
{
    private const string DefaultDeployment = "gpt-4o-transcribe-diarize";
    private const string DefaultApiVersion = "2025-04-01-preview";

    private readonly HttpClient _http;
    private readonly IFoundrySpeechCredentials _credentials;

    public FoundryDiarizeShadowProvider(HttpClient http, IFoundrySpeechCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public string ProviderKey => "foundry-gpt4o-diarize";
    public string DisplayName => "Azure OpenAI diarising transcription (labels only)";
    public bool ProvidesSpeakerLabels => true;
    public bool ProvidesWordTimings => false;

    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default)
        => _credentials.IsConfiguredAsync(cancellationToken);

    public async Task<TranscriptionResponse> TranscribeAsync(
        byte[] wav, TranscribeRequest request, CancellationToken cancellationToken = default)
    {
        var host = Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_DIARIZE_HOST")
            ?? await _credentials.ResolveHostAsync(cancellationToken);
        var deployment = request.Model
            ?? Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_DIARIZE_DEPLOYMENT")
            ?? DefaultDeployment;
        var apiVersion = Environment.GetEnvironmentVariable("PKS_TRANSCRIBE_DIARIZE_API_VERSION") ?? DefaultApiVersion;
        var url = $"https://{host}/openai/deployments/{deployment}/audio/transcriptions?api-version={apiVersion}";

        var started = Stopwatch.StartNew();
        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "audio.wav");
        content.Add(new StringContent(deployment), "model");
        content.Add(new StringContent("diarized_json"), "response_format");
        content.Add(new StringContent("auto"), "chunking_strategy");
        content.Add(new StringContent(BareLocale(request.Locale)), "language");

        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        await _credentials.ApplyAsync(message, FoundryDataPlane.OpenAi, cancellationToken);

        using var response = await _http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"foundry-gpt4o-diarize failed: {(int)response.StatusCode} {(body.Length <= 400 ? body : body[..400])}");
        }

        return new TranscriptionResponse(ParseRaw(body), body, started.ElapsedMilliseconds);
    }

    public SpeechResult ParseRaw(string rawJson)
        => PhrasesFromDiarized(JsonSerializer.Deserialize<DiarizedJson>(rawJson));

    /// <summary>
    /// <c>diarized_json</c> is turn-level: segments with a speaker, a start and an end in
    /// seconds, and no per-word timings. Reshaping it as fast transcription's phrases lets
    /// the rest of the pipeline treat the two engines identically —
    /// <see cref="Alignment.DiarisedWords"/> already spreads a phrase's duration over its
    /// words when the words are missing, and matching is on text anyway.
    /// </summary>
    internal static SpeechResult PhrasesFromDiarized(DiarizedJson? result)
    {
        var phrases = (result?.Segments ?? [])
            .Select(s => new SpeechPhrase
            {
                Speaker = s.Speaker,
                Text = (s.Text ?? "").Trim(),
                OffsetMilliseconds = (long)Math.Round(s.Start * 1000),
                DurationMilliseconds = Math.Max(0, (long)Math.Round((s.End - s.Start) * 1000)),
            })
            .Where(p => p.Text.Length > 0)
            .ToList();

        return new SpeechResult { Phrases = phrases };
    }

    private static string BareLocale(string locale)
    {
        var dash = locale.IndexOf('-');

        return dash > 0 ? locale[..dash] : locale;
    }

    internal sealed class DiarizedJson
    {
        [JsonPropertyName("segments")] public List<DiarizedSegment>? Segments { get; set; }
    }

    internal sealed class DiarizedSegment
    {
        [JsonPropertyName("speaker")] public string? Speaker { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("start")] public double Start { get; set; }
        [JsonPropertyName("end")] public double End { get; set; }
    }
}
