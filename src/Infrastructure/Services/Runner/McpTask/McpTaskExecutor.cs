using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>
/// Runs one <c>mcp_task</c> job (customers/famai/docs/mcp-task-protocol.md): spawn the named stdio
/// MCP servers from the HOST allowlist, expose their allowed tools to Claude, loop the Messages API
/// until <c>end_turn</c> or <c>maxSteps</c>, and return text + names/timings only. Claiming, the
/// progress heartbeat and the final PATCH stay in AgenticsRunnerRunCommand.
/// </summary>
public sealed class McpTaskExecutor
{
    public const string DefaultModel = "claude-haiku-5-5";
    public const int DefaultMaxSteps = 12;
    public const int DefaultMaxTokens = 2000;
    public const int MaxToolResultChars = 40_000;
    public const int MaxStepsCeiling = 50;
    public const int MaxTokensCeiling = 16_000;

    public const string DefaultSystemPrompt =
        "Du er en familieassistent. Svar kort og konkret på dansk. " +
        "Brug kun data, du får fra værktøjerne – gæt eller opfind aldrig noget. " +
        "Hvis værktøjerne ikke giver noget relevant, så sig det tydeligt. " +
        "Nævn aldrig andre børns fulde navne, og medtag ikke helbredsoplysninger ud over det, forælderen har brug for.";

    private readonly IMcpServerLauncher _launcher;
    private readonly HttpClient _http;
    private readonly Func<string, string?> _getEnv;
    private readonly Action<string>? _localLog;

    /// <param name="localLog">Runner-console diagnostics only. Never forwarded to the server.</param>
    public McpTaskExecutor(IMcpServerLauncher launcher, HttpClient http, Func<string, string?>? getEnv = null, Action<string>? localLog = null)
    {
        _launcher = launcher;
        _http = http;
        _getEnv = getEnv ?? Environment.GetEnvironmentVariable;
        _localLog = localLog;
    }

    /// <summary>Validates the requested servers against the allowlist without spawning anything.</summary>
    public static McpTaskOutcome? Validate(McpTaskRequest req, McpTaskConfig? config)
    {
        if (config == null)
            return McpTaskOutcome.Fail("mcp_config_missing", "This runner has no MCP task allowlist (mcp-task-servers.json).");
        if (string.IsNullOrWhiteSpace(req.Prompt))
            return McpTaskOutcome.Fail("empty_prompt", "agentDefinition.prompt is empty.");
        if (req.McpServers.Count == 0)
            return McpTaskOutcome.Fail("no_mcp_servers", "agentDefinition.mcpServers is empty.");
        var unknown = req.McpServers.Where(s => !config.Servers.ContainsKey(s)).Distinct().ToList();
        if (unknown.Count > 0)
            return McpTaskOutcome.Fail("unknown_mcp_server", $"Unknown MCP server(s) on this runner: {string.Join(", ", unknown)}");
        return null;
    }

    public async Task<McpTaskOutcome> RunAsync(McpTaskRequest req, McpTaskConfig? config, Action<string>? onProgress, CancellationToken ct)
    {
        var invalid = Validate(req, config);
        if (invalid != null) return invalid;

        var apiKey = _getEnv("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return McpTaskOutcome.Fail("anthropic_auth", "ANTHROPIC_API_KEY is not set on the runner.");
        var model = new AnthropicMessagesClient(_http, apiKey, _getEnv("ANTHROPIC_BASE_URL"));

        var modelId = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model!;
        var maxSteps = Math.Clamp(req.MaxSteps ?? DefaultMaxSteps, 1, MaxStepsCeiling);
        var maxTokens = Math.Clamp(req.MaxTokens ?? DefaultMaxTokens, 1, MaxTokensCeiling);
        var system = string.IsNullOrWhiteSpace(req.SystemPrompt) ? DefaultSystemPrompt : req.SystemPrompt!;

        var result = new McpTaskResult { Model = modelId };
        var sessions = new Dictionary<string, IMcpServerSession>(StringComparer.Ordinal);
        try
        {
            // 1. Spawn + tools/list + allow/deny.
            var map = new McpToolNameMap();
            var tools = new JsonArray();
            foreach (var serverName in req.McpServers.Distinct())
            {
                var serverCfg = config!.Servers[serverName];
                onProgress?.Invoke($"starting {serverName}");
                IMcpServerSession session;
                try
                {
                    session = await _launcher.StartAsync(serverName, serverCfg, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _localLog?.Invoke($"MCP server '{serverName}' failed to start: {ex.Message}");
                    return McpTaskOutcome.Fail("mcp_server_start_failed", $"MCP server '{serverName}' failed to start ({ex.GetType().Name}).");
                }
                sessions[serverName] = session;

                IReadOnlyList<McpServerTool> listed;
                try
                {
                    listed = await session.ListToolsAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _localLog?.Invoke($"MCP server '{serverName}' tools/list failed: {ex.Message}");
                    return McpTaskOutcome.Fail("mcp_server_start_failed", $"MCP server '{serverName}' tools/list failed ({ex.GetType().Name}).");
                }

                foreach (var t in listed.Where(t => serverCfg.IsToolAllowed(t.Name)))
                    tools.Add(BuildToolDefinition(map.Add(serverName, t.Name), t));
            }
            _localLog?.Invoke($"MCP task: {tools.Count} tool(s) exposed from {sessions.Count} server(s)");

            // 2. Tool loop.
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = req.Prompt } };
            for (var step = 1; step <= maxSteps; step++)
            {
                onProgress?.Invoke($"step {step}/{maxSteps}");
                var body = new JsonObject
                {
                    ["model"] = modelId,
                    ["max_tokens"] = maxTokens,
                    ["system"] = system,
                    ["messages"] = messages.DeepClone(),
                };
                if (tools.Count > 0)
                {
                    body["tools"] = tools.DeepClone();
                    // Last allowed step: make the model answer instead of asking for more tools.
                    if (step == maxSteps) body["tool_choice"] = new JsonObject { ["type"] = "none" };
                }

                JsonObject resp;
                try
                {
                    resp = await model.CreateMessageAsync(body, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (AnthropicApiException ex)
                {
                    return McpTaskOutcome.Fail(ex.IsAuth ? "anthropic_auth" : "model_error", ex.Message, result);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return McpTaskOutcome.Fail("model_unreachable", $"Anthropic API unreachable ({ex.GetType().Name}).", result);
                }

                result.Steps = step;
                if (resp["usage"] is JsonObject usage)
                {
                    result.Usage.InputTokens += ReadLong(usage, "input_tokens") + ReadLong(usage, "cache_read_input_tokens") + ReadLong(usage, "cache_creation_input_tokens");
                    result.Usage.OutputTokens += ReadLong(usage, "output_tokens");
                }

                var content = resp["content"] as JsonArray ?? new JsonArray();
                var stopReason = resp["stop_reason"]?.GetValue<string>();
                // Pass the assistant content back unchanged (thinking blocks included).
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
                var text = ExtractText(content);

                if (stopReason == "tool_use")
                {
                    var toolResults = new JsonArray();
                    foreach (var block in content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_use"))
                        toolResults.Add(await RunToolAsync(block, map, sessions, result, onProgress, ct));
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                    continue;
                }
                if (stopReason == "pause_turn") continue;

                if (stopReason == "refusal")
                    return McpTaskOutcome.Fail("model_refusal", "The model declined the task.", result);

                result.Text = text;
                if (string.IsNullOrWhiteSpace(text))
                    return McpTaskOutcome.Fail(stopReason == "max_tokens" ? "max_tokens" : "empty_response",
                        $"The model returned no text (stop_reason={stopReason}).", result);
                return new McpTaskOutcome { Success = true, Result = result };
            }

            return McpTaskOutcome.Fail("max_steps_exceeded", $"No final answer within {maxSteps} steps.", result);
        }
        finally
        {
            await DisposeAllAsync(sessions.Values);
        }
    }

    private async Task<JsonObject> RunToolAsync(JsonObject block, McpToolNameMap map,
        Dictionary<string, IMcpServerSession> sessions, McpTaskResult result, Action<string>? onProgress, CancellationToken ct)
    {
        var id = block["id"]?.GetValue<string>() ?? "";
        var exposed = block["name"]?.GetValue<string>() ?? "";
        if (!map.TryResolve(exposed, out var server, out var tool) || !sessions.TryGetValue(server, out var session))
            return ToolResult(id, $"Unknown tool '{exposed}'.", isError: true);

        onProgress?.Invoke($"tool {server}/{tool}");
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (block["input"] is JsonObject input)
            foreach (var (k, v) in input)
                args[k] = v == null ? null : JsonSerializer.Deserialize<JsonElement>(v.ToJsonString());

        var sw = Stopwatch.StartNew();
        string text;
        bool isError;
        try
        {
            (text, isError) = await session.CallToolAsync(tool, args, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _localLog?.Invoke($"MCP tool {server}/{tool} threw {ex.GetType().Name}");
            (text, isError) = ($"Tool call failed ({ex.GetType().Name}).", true);
        }
        sw.Stop();
        result.ToolCalls.Add(new McpTaskToolCall { Server = server, Tool = tool, Ok = !isError, Ms = sw.ElapsedMilliseconds });
        return ToolResult(id, Truncate(text), isError);
    }

    internal static string Truncate(string text) =>
        text.Length <= MaxToolResultChars ? text : text[..MaxToolResultChars] + "\n[truncated]";

    internal static JsonObject BuildToolDefinition(string exposedName, McpServerTool tool)
    {
        JsonNode? schema = null;
        if (tool.InputSchema.ValueKind == JsonValueKind.Object)
            schema = JsonNode.Parse(tool.InputSchema.GetRawText());
        if (schema is not JsonObject so || so["type"]?.GetValue<string>() != "object")
            schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

        var def = new JsonObject { ["name"] = exposedName, ["input_schema"] = schema };
        if (!string.IsNullOrWhiteSpace(tool.Description)) def["description"] = tool.Description;
        return def;
    }

    private static JsonObject ToolResult(string id, string text, bool isError)
    {
        var r = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id,
            ["content"] = string.IsNullOrEmpty(text) ? "(empty)" : text,
        };
        if (isError) r["is_error"] = true;
        return r;
    }

    private static string ExtractText(JsonArray content)
    {
        var sb = new StringBuilder();
        foreach (var b in content.OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "text"))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(b["text"]?.GetValue<string>());
        }
        return sb.ToString().Trim();
    }

    private static long ReadLong(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;

    /// <summary>Kills every spawned server, bounded, independent of the (possibly cancelled) job token.</summary>
    private static async Task DisposeAllAsync(IEnumerable<IMcpServerSession> sessions)
    {
        var tasks = sessions.Select(async s =>
        {
            try { await s.DisposeAsync(); } catch { /* best-effort */ }
        }).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(10)));
    }
}
