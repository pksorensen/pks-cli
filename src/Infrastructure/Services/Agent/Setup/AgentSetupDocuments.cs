using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Agent.Setup;

/// <summary>
/// The wire shapes of the Agent Setup Protocol (ASP) v1.
///
/// <para>
/// ASP generalises Context&amp; Home's one-run Codex installer: the <b>platform publishes data</b>
/// and the <b>client owns the file edits</b>. A platform implements four JSON documents and
/// inherits every tool a client supports; a client implements one tool's file format and inherits
/// every platform.
/// </para>
///
/// <para>
/// Spec: <c>docs/specs/agent-setup/</c> in the agentic-live-www workspace.
/// </para>
///
/// <para>
/// <b>Everything optional is optional on purpose.</b> A platform may publish no model provider
/// (agentics.dk has no Responses endpoint, so it configures the context around the model instead)
/// and no credential (its MCP servers authenticate the reader themselves over OAuth). A client
/// that treats either as mandatory fails against the reference implementation, so absent fields
/// are an ordinary case here and never a warning.
/// </para>
/// </summary>
public sealed class AspServiceDocument
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("platform")] public AspPlatform? Platform { get; set; }

    /// <summary>Tool bindings this platform publishes profiles for.</summary>
    [JsonPropertyName("tools")] public List<string> Tools { get; set; } = [];

    [JsonPropertyName("profiles")] public string? Profiles { get; set; }

    /// <summary>The RFC 9728 protected-resource-metadata URL. Where sign-in starts.</summary>
    [JsonPropertyName("authorization")] public string? Authorization { get; set; }

    /// <summary>
    /// The loopback redirects this platform's authorization server accepts.
    /// Absent means the house default, <c>http://127.0.0.1:{51789,51790,51791}/callback</c>.
    /// </summary>
    [JsonPropertyName("loopback_redirect_uris")] public List<string>? LoopbackRedirectUris { get; set; }

    /// <summary>Optional, defaults to true. False says every profile here omits <c>provider</c>.</summary>
    [JsonPropertyName("publishes_provider")] public bool? PublishesProvider { get; set; }
}

public sealed class AspPlatform
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("documentation")] public string? Documentation { get; set; }
}

/// <summary>
/// RFC 9728 Protected Resource Metadata, plus the two house extensions.
///
/// <para>
/// <b><c>ClientId</c> is the field that makes the protocol general.</b> RFC 9728 has no field for
/// it and RFC 7591 dynamic registration is implemented by none of the providers we reach, so it is
/// a house extension — but it is the one that lets one binary serve two issuers. Keycloak takes a
/// CIMD URL and resolves it by fetching it, so no registration exists anywhere; Entra does not
/// implement CIMD and hands out a pre-registered GUID. A client holding its own constant works
/// against exactly one of them.
/// </para>
/// </summary>
public sealed class AspResourceMetadata
{
    [JsonPropertyName("resource")] public string? Resource { get; set; }
    [JsonPropertyName("authorization_servers")] public List<string> AuthorizationServers { get; set; } = [];
    [JsonPropertyName("scopes_supported")] public List<string> ScopesSupported { get; set; } = [];

    /// <summary>House extension — see the type remarks. A CIMD URL or a pre-registered id.</summary>
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }

    /// <summary>House extension: the RFC 8707 resource indicator to ask for, when it differs.</summary>
    [JsonPropertyName("audience")] public string? Audience { get; set; }
}

/// <summary>The authenticated profile list: what this reader is entitled to configure.</summary>
public sealed class AspProfileList
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source")] public AspSource? Source { get; set; }
    [JsonPropertyName("subject")] public AspSubject? Subject { get; set; }

    /// <summary>A hint for the picker's preselection, never an instruction to switch.</summary>
    [JsonPropertyName("default")] public string? Default { get; set; }

    [JsonPropertyName("profiles")] public List<AspProfile> Profiles { get; set; } = [];
}

public sealed class AspSource
{
    /// <summary>The platform's short slug. Every namespace we write is built on it.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
}

public sealed class AspSubject
{
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class AspProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }

    /// <summary>Groups the picker — an owner, a tenant, a customer.</summary>
    [JsonPropertyName("group")] public string? Group { get; set; }

    /// <summary>Absent when the platform configures context rather than the model.</summary>
    [JsonPropertyName("provider")] public AspProvider? Provider { get; set; }

    [JsonPropertyName("model")] public AspModel? Model { get; set; }
    [JsonPropertyName("models")] public List<AspModelEntry>? Models { get; set; }
    [JsonPropertyName("mcpServers")] public List<AspMcpServer>? McpServers { get; set; }
    [JsonPropertyName("marketplaces")] public List<AspMarketplace>? Marketplaces { get; set; }

    /// <summary>Where to POST for this profile's credential. Absent when there is nothing to mint.</summary>
    [JsonPropertyName("credential")] public string? Credential { get; set; }
}

public sealed class AspProvider
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }

    /// <summary>Codex needs <c>responses</c>; a chat-completions endpoint fails with no diagnostic.</summary>
    [JsonPropertyName("wireApi")] public string? WireApi { get; set; }

    [JsonPropertyName("auth")] public AspProviderAuth? Auth { get; set; }
}

/// <summary>
/// How the credential reaches the provider.
///
/// <para>
/// <b><c>header</c> is the house choice, and it is not a preference.</b> Azure API Management —
/// which is what Context&amp; Home's gateway is — matches a subscription key in a named header and
/// cannot match one presented as <c>Authorization: Bearer &lt;key&gt;</c>. Codex's <c>env_key</c>
/// emits the bearer form, so a Home profile configured that way 401s on every call. Our own
/// gateway accepts either, so <c>header</c> is the setting that works for both.
/// </para>
/// </summary>
public sealed class AspProviderAuth
{
    /// <summary><c>header</c> or <c>bearer</c>.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }

    [JsonPropertyName("header")] public string? Header { get; set; }

    /// <summary>The base name of the environment variable. Never a value.</summary>
    [JsonPropertyName("env")] public string? Env { get; set; }
}

public sealed class AspModel
{
    [JsonPropertyName("default")] public string? Default { get; set; }
    [JsonPropertyName("allow")] public List<string>? Allow { get; set; }
    [JsonPropertyName("reasoningEffort")] public string? ReasoningEffort { get; set; }
}

public sealed class AspModelEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("maxTokens")] public int? MaxTokens { get; set; }
}

public sealed class AspMcpServer
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("command")] public string? Command { get; set; }
    [JsonPropertyName("args")] public List<string>? Args { get; set; }

    /// <summary>Only for a server that answers <c>appInfo</c> in <c>ui/initialize</c>.</summary>
    [JsonPropertyName("mcpApps")] public bool? McpApps { get; set; }
}

public sealed class AspMarketplace
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
}

/// <summary>The credential response. The value is used once, put in an env var, and never written.</summary>
public sealed class AspCredential
{
    [JsonPropertyName("env")] public string? Env { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}
