namespace PKS.Infrastructure.Services.Agents;

/// <summary>The identity a session claims when it registers as an agent.</summary>
public sealed record AgentIdentity(string Name, string Role);

/// <summary>What a successful registration yields, enough to wire the session up.</summary>
public sealed record AgentRegistration(
    string Provider,
    string Host,
    string InboxId,
    string McpUrl,
    string Token);

/// <summary>
/// A backend an agent (a coding session) can register itself against. `pks agent
/// register` is the generic verb; each provider implements how an agent enrolls
/// and is wired. The first provider is <c>share</c> (Agent Share / share.agentics.dk);
/// others (other inboxes, other surfaces) plug in additively via this interface.
/// </summary>
public interface IAgentProvider
{
    /// <summary>Stable provider key, e.g. "share". Used to select on the CLI.</summary>
    string Name { get; }

    /// <summary>Human-facing one-liner for the picker.</summary>
    string Description { get; }

    /// <summary>True when this provider has the credentials it needs (e.g. `pks share init` was run).</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>Enroll this session as an agent and return how to reach its inbox.</summary>
    Task<AgentRegistration> RegisterAsync(AgentIdentity identity, CancellationToken ct = default);
}
