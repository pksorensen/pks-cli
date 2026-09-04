using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// Azure Speech fast transcription. One endpoint, two definitions:
///
///   diarisation  {locales:["da-DK"], diarization:{enabled:true}}     → who, when
///   verbatim     {locales:["da"],    enhancedMode:{model:"mai-1.5"}} → what
///
/// THEY DO NOT COMBINE. Measured 2026-08-07 on 90 s of Danish: asking for both in one
/// request returns 200 with <c>enhancedMode</c> silently winning — one phrase, no speaker
/// labels, whole file. That is why this is two providers and two calls, and why a future
/// "just add diarization to the enhanced request" is a bug, not a saving.
///
/// The locale strings differ between the two and that is not a typo: plain diarisation
/// rejects "da" with InvalidLocale and wants "da-DK", while the MAI enhanced path takes "da".
/// </summary>
public abstract class FoundrySpeechProviderBase : ITranscriptionProvider
{
    private const string ApiVersion = "2025-10-15";

    private readonly HttpClient _http;
    private readonly IFoundrySpeechCredentials _credentials;

    protected FoundrySpeechProviderBase(HttpClient http, IFoundrySpeechCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
        // Fast transcription runs at roughly 55× realtime, so a 15-minute part is a matter of
        // seconds — but the service queues, and a timeout that fires early turns a slow
        // response into a retry storm against a request that was going to succeed.
        _http.Timeout = TimeSpan.FromMinutes(15);
    }

    public abstract string ProviderKey { get; }
    public abstract string DisplayName { get; }
    public abstract bool ProvidesSpeakerLabels { get; }
    public abstract bool ProvidesWordTimings { get; }

    /// <summary>The <c>definition</c> form field — the whole difference between the two engines.</summary>
    protected abstract object BuildDefinition(TranscribeRequest request);

    public Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default)
        => _credentials.IsConfiguredAsync(cancellationToken);

    public SpeechResult ParseRaw(string rawJson)
        => JsonSerializer.Deserialize<SpeechResult>(rawJson)
            ?? throw new InvalidOperationException($"{ProviderKey}: response was not a transcription");

    public async Task<TranscriptionResponse> TranscribeAsync(
        byte[] wav, TranscribeRequest request, CancellationToken cancellationToken = default)
    {
        var host = await _credentials.ResolveHostAsync(cancellationToken);
        var url = $"https://{host}/speechtotext/transcriptions:transcribe?api-version={ApiVersion}";
        var definition = JsonSerializer.Serialize(BuildDefinition(request));

        var lastError = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var started = Stopwatch.StartNew();
            try
            {
                using var content = new MultipartFormDataContent();
                var audio = new ByteArrayContent(wav);
                audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audio, "audio", "audio.wav");
                content.Add(new StringContent(definition), "definition");

                using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                await _credentials.ApplyAsync(message, FoundryDataPlane.Speech, cancellationToken);

                using var response = await _http.SendAsync(message, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return new TranscriptionResponse(ParseRaw(body), body, started.ElapsedMilliseconds);
                }

                lastError = $"{(int)response.StatusCode} {Truncate(body)}";

                // 4xx other than 429 will not get better by being asked again.
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken);
        }

        throw new InvalidOperationException($"{ProviderKey} failed: {lastError}");
    }

    protected static string Truncate(string body) => body.Length <= 400 ? body : body[..400];
}

/// <summary>
/// Who spoke, when — plus per-word timings, which is what makes the merge in
/// <see cref="Alignment"/> possible without cutting the audio up.
/// </summary>
public sealed class FoundryFastTranscriptionProvider : FoundrySpeechProviderBase
{
    public FoundryFastTranscriptionProvider(HttpClient http, IFoundrySpeechCredentials credentials)
        : base(http, credentials) { }

    public override string ProviderKey => "foundry-fast";
    public override string DisplayName => "Azure fast transcription (diarising)";
    public override bool ProvidesSpeakerLabels => true;
    public override bool ProvidesWordTimings => true;

    protected override object BuildDefinition(TranscribeRequest request) => new
    {
        locales = new[] { request.Locale },
        diarization = new
        {
            enabled = request.Diarize,
            // The service takes 2..12. A caller asking for one speaker means "do not
            // diarise", which is what the enhanced provider is for.
            maxSpeakers = Math.Clamp(request.MaxSpeakers, 2, 12),
        },
    };
}

/// <summary>
/// What was actually said. <c>transcribeStyle: verbatim</c> is the point of the whole
/// exercise: the default output is readability-optimised, and the requirement is that
/// nothing improves the record.
///
/// MAI models live only on the US East Foundry resource — they do not exist on the EU one,
/// which fails as a confusing model-not-found rather than a region error. Point
/// PKS_FOUNDRY_ENDPOINT at the right resource before concluding the model name is wrong.
///
/// Pinned, not the bare <c>mai-transcribe</c> alias: <c>mai-transcribe-1</c> is deprecated
/// 20 Aug 2026 and an alias's target is an assumption.
/// </summary>
public sealed class FoundryEnhancedTranscriptionProvider : FoundrySpeechProviderBase
{
    public const string DefaultModel = "mai-transcribe-1.5";

    public FoundryEnhancedTranscriptionProvider(HttpClient http, IFoundrySpeechCredentials credentials)
        : base(http, credentials) { }

    public override string ProviderKey => "foundry-enhanced";
    public override string DisplayName => "Azure enhanced mode (MAI transcribe)";
    public override bool ProvidesSpeakerLabels => false;
    public override bool ProvidesWordTimings => false;

    protected override object BuildDefinition(TranscribeRequest request)
    {
        var enhanced = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["model"] = request.Model ?? Environment.GetEnvironmentVariable("PKS_VERBATIM_MODEL") ?? DefaultModel,
            ["transcribeStyle"] = "verbatim",
        };
        if (request.PhraseList.Count > 0) enhanced["phraseList"] = request.PhraseList.Take(100).ToArray();

        return new
        {
            // The enhanced path wants the bare language, not the region-qualified tag the
            // diarising path insists on.
            locales = new[] { BareLocale(request.Locale) },
            enhancedMode = enhanced,
        };
    }

    private static string BareLocale(string locale)
    {
        var dash = locale.IndexOf('-');

        return dash > 0 ? locale[..dash] : locale;
    }
}
