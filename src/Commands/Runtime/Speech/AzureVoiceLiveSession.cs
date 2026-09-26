using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace PKS.Commands.Runtime.Speech;

internal sealed class AzureVoiceLiveSession : IAsyncDisposable
{
    private static readonly TokenRequestContext CognitiveServicesTokenContext = new(["https://cognitiveservices.azure.com/.default"]);
    private static readonly TokenRequestContext FoundryTokenContext = new(["https://ai.azure.com/.default"]);
    private const int BytesPerSecond = 24000 * sizeof(short);
    private const int CommitIntervalBytes = BytesPerSecond * 3;
    private readonly SpeechRuntimeOptions _options;
    private readonly ClientWebSocket _socket = new();
    private int _uncommittedBytes;

    public AzureVoiceLiveSession(SpeechRuntimeOptions options) => _options = options;

    public WebSocketState State => _socket.State;

    public async Task ConnectAsync(SpeechSessionStart session, CancellationToken cancellationToken)
    {
        var credentialOptions = new DefaultAzureCredentialOptions();
        if (!string.IsNullOrWhiteSpace(_options.ManagedIdentityClientId))
            credentialOptions.ManagedIdentityClientId = _options.ManagedIdentityClientId;
        var credential = new DefaultAzureCredential(credentialOptions);
        var useOpenAiTranscription = string.Equals(
            _options.Provider,
            "azure-openai-realtime-transcription",
            StringComparison.Ordinal);
        var token = await credential.GetTokenAsync(
            useOpenAiTranscription ? FoundryTokenContext : CognitiveServicesTokenContext,
            cancellationToken);
        _socket.Options.SetRequestHeader("Authorization", $"Bearer {token.Token}");

        var uri = new UriBuilder(_options.FoundryEndpoint)
        {
            Scheme = Uri.UriSchemeWss,
            Path = useOpenAiTranscription ? "/openai/v1/realtime" : "/voice-live/realtime",
            Query = useOpenAiTranscription
                ? "intent=transcription"
                : $"api-version={Uri.EscapeDataString(_options.ApiVersion)}&model={Uri.EscapeDataString(_options.RealtimeModel)}",
        }.Uri;
        await _socket.ConnectAsync(uri, cancellationToken);

        if (useOpenAiTranscription)
        {
            await SendJsonAsync(new
            {
                type = "session.update",
                session = new
                {
                    type = "transcription",
                    audio = new
                    {
                        input = new
                        {
                            format = new { type = "audio/pcm", rate = session.SampleRate },
                            turn_detection = (object?)null,
                            transcription = new
                            {
                                model = _options.TranscriptionModel,
                                language = string.IsNullOrWhiteSpace(session.Language) ? null : session.Language,
                            },
                        },
                    },
                },
            }, cancellationToken);
        }
        else
        {
            await SendJsonAsync(new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    input_audio_transcription = new
                    {
                        model = _options.TranscriptionModel,
                        language = string.IsNullOrWhiteSpace(session.Language) ? null : session.Language,
                    },
                    turn_detection = new
                    {
                        type = "azure_semantic_vad",
                        threshold = 0.5,
                        prefix_padding_ms = 300,
                        silence_duration_ms = 600,
                        create_response = false,
                        interrupt_response = true,
                        auto_truncate = true,
                    },
                    input_audio_noise_reduction = new { type = "azure_deep_noise_suppression" },
                    input_audio_echo_cancellation = new { type = "server_echo_cancellation" },
                    input_audio_sampling_rate = session.SampleRate,
                },
            }, cancellationToken);
        }
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
    {
        await SendJsonAsync(new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16.Span),
        }, cancellationToken);

        if (!string.Equals(_options.Provider, "azure-openai-realtime-transcription", StringComparison.Ordinal))
            return;

        _uncommittedBytes += pcm16.Length;
        if (_uncommittedBytes < CommitIntervalBytes)
            return;

        await SendJsonAsync(new { type = "input_audio_buffer.commit" }, cancellationToken);
        _uncommittedBytes = 0;
    }

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            stream.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private async Task SendJsonAsync(object value, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None); }
            catch { }
        }
        _socket.Dispose();
    }
}
