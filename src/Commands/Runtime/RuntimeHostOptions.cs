using PKS.Commands.Runtime.Speech;

namespace PKS.Commands.Runtime;

internal sealed record RuntimeHostOptions(
    IReadOnlyList<string> Capabilities,
    string ServiceToken)
{
    public static RuntimeHostOptions FromEnvironment(
        Func<string, string?>? read = null,
        IReadOnlyList<string>? forcedCapabilities = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var capabilities = forcedCapabilities ?? ParseCapabilities(read("PKS_RUNTIME_CAPABILITIES"));
        var unsupported = capabilities.FirstOrDefault(capability =>
            !string.Equals(capability, SpeechProtocol.Capability, StringComparison.Ordinal));
        if (unsupported is not null)
            throw new InvalidOperationException($"Unsupported runtime capability '{unsupported}'");

        var token = read("PKS_RUNTIME_TOKEN")
            ?? read("PKS_SPEECH_RUNTIME_TOKEN")
            ?? "";

        return new RuntimeHostOptions(capabilities, token);
    }

    private static IReadOnlyList<string> ParseCapabilities(string? value)
    {
        var requested = string.IsNullOrWhiteSpace(value)
            ? [SpeechProtocol.Capability]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return requested.Distinct(StringComparer.Ordinal).ToArray();
    }
}
