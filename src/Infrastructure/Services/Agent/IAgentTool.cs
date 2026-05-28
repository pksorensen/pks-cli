using System.Text.Json;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// A tool the agent can call (read, write, edit, bash, grep, find, ls, ...).
/// One implementation per tool. All tools are constructed with a sandbox root (`cwd`)
/// and must reject paths that escape it.
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// Provider-neutral tool declaration. The model sees Name + Description + InputSchema.
    /// </summary>
    ChatToolDefinition Definition { get; }

    /// <summary>
    /// Execute the tool. `arguments` is the JSON object the model emitted (already parsed).
    /// Returns the text content that will be returned to the model in a ToolResultBlock.
    /// Throwing is allowed — AgentLoop will wrap the exception as a failed ToolResultBlock.
    /// </summary>
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Output of a single tool invocation.
/// </summary>
/// <param name="Output">Text returned to the model.</param>
/// <param name="IsError">If true, the model sees this as a failed tool call.</param>
public sealed record ToolResult(string Output, bool IsError = false)
{
    public static ToolResult Success(string output) => new(output, IsError: false);
    public static ToolResult Error(string message) => new(message, IsError: true);
}
