namespace PKS.Commands.Runtime.Speech;

internal sealed record SpeechRuntimeOptions(
    Uri FoundryEndpoint,
    string RealtimeModel,
    string TranscriptionModel,
    string ApiVersion,
    string Provider,
    string? ManagedIdentityClientId)
{
    public static SpeechRuntimeOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var provider = read("PKS_SPEECH_PROVIDER") ?? "azure-openai-realtime-transcription";
        if (!string.Equals(provider, "azure-openai-realtime-transcription", StringComparison.Ordinal)
            && !string.Equals(provider, "azure-voice-live", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported PKS_SPEECH_PROVIDER '{provider}'");

        var endpointValue = read("PKS_FOUNDRY_ENDPOINT");
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("PKS_FOUNDRY_ENDPOINT must be an absolute https URL");

        var realtimeModel = read("PKS_FOUNDRY_REALTIME_MODEL") ?? "gpt-realtime";
        var transcriptionModel = read("PKS_FOUNDRY_TRANSCRIPTION_MODEL") ?? "gpt-4o-transcribe-diarize";
        var apiVersion = read("PKS_FOUNDRY_VOICE_LIVE_API_VERSION") ?? "2025-10-01";
        if (string.IsNullOrWhiteSpace(transcriptionModel))
            throw new InvalidOperationException("A Foundry transcription deployment name is required");
        if (string.Equals(provider, "azure-voice-live", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(realtimeModel))
            throw new InvalidOperationException("A Foundry realtime model name is required for Azure Voice Live");

        return new SpeechRuntimeOptions(
            endpoint,
            realtimeModel,
            transcriptionModel,
            apiVersion,
            provider,
            read("AZURE_CLIENT_ID"));
    }
}
