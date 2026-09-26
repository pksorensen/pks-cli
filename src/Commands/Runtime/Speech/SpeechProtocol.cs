using System.Text.Json;

namespace PKS.Commands.Runtime.Speech;

internal sealed record SpeechSessionStart(
    string Capability,
    int SampleRate,
    int Channels,
    string Encoding,
    string? Language);

internal static class SpeechProtocol
{
    public const string Capability = "speech.realtime.transcribe";

    public static SpeechSessionStart ParseSessionStart(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        if (!string.Equals(type, "session.start", StringComparison.Ordinal))
            throw new InvalidOperationException("The first event must be session.start");

        var capability = root.TryGetProperty("capability", out var capabilityValue)
            ? capabilityValue.GetString() ?? Capability
            : Capability;
        if (!string.Equals(capability, Capability, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported capability '{capability}'");

        var audio = root.TryGetProperty("audio", out var audioValue) ? audioValue : default;
        var sampleRate = audio.ValueKind == JsonValueKind.Object && audio.TryGetProperty("sampleRate", out var sampleRateValue)
            ? sampleRateValue.GetInt32()
            : 24000;
        var channels = audio.ValueKind == JsonValueKind.Object && audio.TryGetProperty("channels", out var channelsValue)
            ? channelsValue.GetInt32()
            : 1;
        var encoding = audio.ValueKind == JsonValueKind.Object && audio.TryGetProperty("encoding", out var encodingValue)
            ? encodingValue.GetString() ?? "pcm16"
            : "pcm16";
        var language = root.TryGetProperty("language", out var languageValue) ? languageValue.GetString() : null;

        if (sampleRate != 24000 || channels != 1 || !string.Equals(encoding, "pcm16", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only 24 kHz mono PCM16 audio is supported");

        return new SpeechSessionStart(capability, sampleRate, channels, encoding, language);
    }

    public static string ServerEvent(string type, long sequence, object? payload = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["sequence"] = sequence,
            ["at"] = DateTimeOffset.UtcNow,
        };
        if (payload is not null)
        {
            foreach (var property in payload.GetType().GetProperties())
                body[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = property.GetValue(payload);
        }
        return JsonSerializer.Serialize(body);
    }
}
