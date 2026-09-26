using System.Text.Json;

namespace PKS.Commands.Runtime.Speech;

internal sealed record SpeechTranslation(string Type, object Payload);

internal static class AzureVoiceLiveEvents
{
    public static SpeechTranslation? Translate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        return type switch
        {
            "conversation.item.input_audio_transcription.delta" when TryText(root, "delta", out var partial)
                => new("transcript.partial", new { text = partial }),
            "conversation.item.input_audio_transcription.completed" when TryText(root, "transcript", out var final)
                => new("transcript.final", new { text = final }),
            "conversation.item.input_audio_transcription.failed" => new("provider.error", new
            {
                message = ErrorMessage(root, "Azure OpenAI transcription failed"),
            }),
            "error" => new("provider.error", new
            {
                message = ErrorMessage(root, "Azure speech provider error"),
            }),
            _ => null,
        };
    }

    private static bool TryText(JsonElement root, string property, out string text)
    {
        text = root.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
        return text.Length > 0;
    }

    private static string ErrorMessage(JsonElement root, string fallback)
    {
        if (!root.TryGetProperty("error", out var error)) return fallback;
        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? fallback;
        return error.TryGetProperty("message", out var message) ? message.GetString() ?? fallback : fallback;
    }
}
