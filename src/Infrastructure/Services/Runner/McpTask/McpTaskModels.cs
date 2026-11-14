using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>What the executor needs from the job's agentDefinition.</summary>
public sealed class McpTaskRequest
{
    public string Prompt { get; init; } = "";
    public string? SystemPrompt { get; init; }
    public List<string> McpServers { get; init; } = new();
    public string? Model { get; init; }
    public int? MaxSteps { get; init; }
    public int? MaxTokens { get; init; }
}

public sealed class McpTaskToolCall
{
    [JsonPropertyName("server")] public string Server { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("ms")] public long Ms { get; set; }
}

public sealed class McpTaskUsage
{
    [JsonPropertyName("inputTokens")] public long InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long OutputTokens { get; set; }
}

/// <summary>The <c>result</c> object on the final PATCH. Never carries tool arguments or results.</summary>
public sealed class McpTaskResult
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("steps")] public int Steps { get; set; }
    [JsonPropertyName("toolCalls")] public List<McpTaskToolCall> ToolCalls { get; set; } = new();
    [JsonPropertyName("usage")] public McpTaskUsage Usage { get; set; } = new();
}

/// <summary>Executor outcome. On failure <see cref="CompletionReason"/> is a short machine code and
/// <see cref="Logs"/> a human line built only from names/status codes -- never tool payloads.</summary>
public sealed class McpTaskOutcome
{
    public bool Success { get; init; }
    public string? CompletionReason { get; init; }
    public string? Logs { get; init; }
    public McpTaskResult? Result { get; init; }

    public static McpTaskOutcome Fail(string reason, string logs, McpTaskResult? partial = null) =>
        new() { Success = false, CompletionReason = reason, Logs = logs, Result = partial };
}

/// <summary>Typed body for the final <c>PATCH …/runs/{runId}/jobs/{jobId}</c>. Null members are omitted,
/// so a job type that never sets <see cref="Result"/> sends exactly what it sent before.</summary>
public sealed class RunnerJobCompletionPatch
{
    [JsonPropertyName("status")] public string Status { get; set; } = "completed";
    [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
    [JsonPropertyName("completionReason")] public string? CompletionReason { get; set; }
    [JsonPropertyName("logs")] public string? Logs { get; set; }
    [JsonPropertyName("result")] public McpTaskResult? Result { get; set; }

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static RunnerJobCompletionPatch From(McpTaskOutcome o) => new()
    {
        Status = "completed",
        Conclusion = o.Success ? "success" : "failure",
        CompletionReason = o.Success ? null : o.CompletionReason,
        Logs = o.Success ? null : o.Logs,
        // Only a successful run reports a result: the contract's failure shape has none.
        Result = o.Success ? o.Result : null,
    };
}
