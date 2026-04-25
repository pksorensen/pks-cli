namespace PKS.Infrastructure.Services.AgenticsProxy;

public sealed class AgenticsProxyOptions
{
    public string BootstrapToken { get; init; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, HostPolicy> AllowedHosts { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public string JobId { get; init; } = "";
}
