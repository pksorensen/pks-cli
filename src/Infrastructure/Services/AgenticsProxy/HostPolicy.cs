namespace PKS.Infrastructure.Services.AgenticsProxy;

public sealed class HostPolicy
{
    // Glob patterns checked before AllowedPaths. A matching path → 403.
    public List<string> DeniedPaths { get; init; } = new();
    // Empty = allow all paths. Non-empty = path must match at least one pattern → else 403.
    public List<string> AllowedPaths { get; init; } = new();
    public string TokenScope { get; init; } = "https://cognitiveservices.azure.com/.default";
}
