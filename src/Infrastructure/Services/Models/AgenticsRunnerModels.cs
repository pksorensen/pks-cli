namespace PKS.Infrastructure.Services.Models;

public class AgenticsRunnerRegistration
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Token { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Project { get; set; } = "";
    public string Server { get; set; } = "";
    public string? GitUrl { get; set; }
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// Operator-configured capability/chat-model/label overrides for this registration, set by
    /// <c>agentics runner start</c>'s interactive first-run configuration (or <c>--configure</c>).
    /// Null means "never configured" -- every field defaults to auto/probe-decides behavior, and
    /// (thanks to AgenticsRunnerConfigurationService's WhenWritingNull serialization) a null
    /// profile is simply absent from the persisted JSON, so old <c>agentics-runners.json</c> files
    /// round-trip unchanged. See docs/remote-runner-targets-plan.md Phase 3.
    /// </summary>
    public RunnerProfile? Profile { get; set; }
}

/// <summary>
/// Operator overrides persisted alongside an <see cref="AgenticsRunnerRegistration"/>. Every field
/// is independently nullable: null always means "auto" (probe/factory decides at poll time), an
/// explicit (possibly empty) list means the operator has reviewed and narrowed it. Setting a field
/// here can only narrow what would otherwise be advertised/exposed -- it can never force-advertise
/// a capability or model this runner cannot actually serve, since the live probe/resolution is
/// re-checked every poll and intersected with these values.
/// </summary>
public sealed class RunnerProfile
{
    /// <summary>Capability strings to advertise (e.g. "alp_operator", "chat-llm:v1"). Null = auto
    /// (whatever the capability probe finds available). Explicit list = operator override,
    /// intersected against live probe results every poll -- never a way to force-enable something
    /// unavailable.</summary>
    public List<string>? Capabilities { get; set; }

    /// <summary>Chat model ids this runner is allowed to expose/serve for chat-llm:v1 Jobs. Null =
    /// expose everything AgentChatProviderFactory.ListAvailableModelsAsync() (CanResolveAsync-filtered)
    /// currently accepts. Explicit list = allowlist, enforced on both the chat.models.request listing
    /// and the chat.completion.request resolution path (not just a display preference).</summary>
    public List<string>? ChatModels { get; set; }

    /// <summary>Free-form job-targeting labels sent at registration (distinct from ALP capability
    /// strings -- matched by overlap against a job's own `labels`, see agentics-store.ts
    /// findQueuedJobs). Null = use the runner's computed default labels.</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Default chat-llm:v1 model id when a chat.completion.request doesn't specify one and
    /// no --chat-llm-model flag/env override is set.</summary>
    public string? DefaultChatModel { get; set; }

    /// <summary>Set when this project's work was handed off to run on a registered SSH target
    /// instead of locally (Phase 4). Unused by Phase 3.</summary>
    public string? SshTargetLabel { get; set; }

    /// <summary>UTC timestamp of the last time this profile was set via the interactive configure
    /// flow (first run or --configure).</summary>
    public DateTime? ConfiguredAt { get; set; }
}

public class AgenticsRunnerConfiguration
{
    public List<AgenticsRunnerRegistration> Registrations { get; set; } = new();
    public DateTime? LastModified { get; set; }
}
