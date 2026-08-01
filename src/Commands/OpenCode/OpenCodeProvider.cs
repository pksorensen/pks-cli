namespace PKS.Commands.OpenCode;

public sealed record OpenCodeProvider(
    string Id,
    string DisplayName,
    string BaseUrl,
    string ApiKeyEnvironmentVariable,
    IReadOnlyCollection<string> Models)
{
    public bool Offers(string model) =>
        Models.Contains(model, StringComparer.OrdinalIgnoreCase);
}

public sealed class OpenCodeProviderException(string message) : Exception(message);
