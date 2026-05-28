using System.Text.Json;

namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Provider-neutral function/tool declaration sent to the model.
/// </summary>
/// <param name="Name">Tool name, e.g. "read", "write".</param>
/// <param name="Description">One-line description shown to the model.</param>
/// <param name="InputSchema">JSON Schema (object) describing the tool's argument shape.</param>
public sealed record ChatToolDefinition(string Name, string Description, JsonElement InputSchema);
