using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Claude Code → ASF. Reads ~/.claude/projects/&lt;slug&gt;/**/*.jsonl (recursive, so
/// subagent transcripts under &lt;sessionId&gt;/subagents/ come along), honoring the
/// ~/.config/claude/projects fallback via <see cref="IBrainPathResolver"/>.
///
/// Mapping table: docs/specs/asf/01-event-envelope.md §Claude Code.
///
/// Claude's own retention is `cleanupPeriodDays` in ~/.claude/settings.json; a
/// year is the recommended setting. Nothing here deletes anything.
public sealed class ClaudeAsfSource : IAgentSessionSource
{
    private readonly IBrainPathResolver _paths;

    public ClaudeAsfSource(IBrainPathResolver paths) => _paths = paths;

    public string Kind => AsfSource.Claude;

    public bool IsAvailable => Directory.Exists(_paths.ClaudeProjectsRoot);

    public string Location => _paths.ClaudeProjectsRoot;

    public IEnumerable<DiscoveredAgentSession> Discover(string? projectFilter = null)
    {
        var root = _paths.ClaudeProjectsRoot;
        if (!Directory.Exists(root)) yield break;

        foreach (var projectDir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var slug = Path.GetFileName(projectDir);
            if (string.IsNullOrEmpty(slug)) continue;
            if (projectFilter is { Length: > 0 } &&
                !slug.Contains(projectFilter, StringComparison.OrdinalIgnoreCase)) continue;

            // DecodeSlug is lossy whenever a path component contained '-', so it is
            // only the fallback; the transcript's own `cwd` is authoritative and is
            // what makes a Claude and a Codex session in the same repo share a
            // project handle.
            var decoded = _paths.DecodeSlug(slug);

            foreach (var file in Directory
                .EnumerateFiles(projectDir, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (IOException) { continue; }

                var cwd = SourceReadHelpers.Str(
                    SourceReadHelpers.PeekFirstObject(file, o => o["cwd"] is not null)?["cwd"]);

                yield return new DiscoveredAgentSession(
                    Kind,
                    Path.GetFileNameWithoutExtension(file),
                    slug,
                    _paths.Normalize(cwd) ?? decoded,
                    file,
                    info.LastWriteTimeUtc,
                    info.Length);
            }
        }
    }

    public async IAsyncEnumerable<AsfEvent> ReadAsync(
        DiscoveredAgentSession session,
        ISecretMasker masker,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        AsfEventBuilder? builder = null;
        var fallbackTs = new DateTimeOffset(session.LastModifiedUtc, TimeSpan.Zero);

        // tool_use id → the open call, so the paired tool_result can carry a real
        // durationMs and the file_op knows which tool and which path produced it.
        var pendingCalls = new Dictionary<string, PendingCall>(StringComparer.Ordinal);

        // One assistant message is written across several JSONL lines (one per
        // content block), and every one of them repeats the *same* `message.usage`.
        // Measured on a real transcript: 10,820 assistant lines, 6,298 distinct
        // message ids, zero ids whose usage payloads differ. Emitting one usage
        // event per line would overstate token totals by ~72%.
        var usageSeen = new HashSet<string>(StringComparer.Ordinal);
        var lastTs = fallbackTs;

        await foreach (var line in SourceReadHelpers.ReadJsonLinesAsync(session.SourcePath, ct))
        {
            if (line is not JsonObject obj) continue;

            var type = SourceReadHelpers.Str(obj["type"]);
            if (type is null || SkippedTypes.Contains(type)) continue;

            var ts = ParseTimestamp(obj["timestamp"], lastTs);
            lastTs = ts;

            if (builder is null)
            {
                var nativeId = SourceReadHelpers.Str(obj["sessionId"])
                    ?? SourceReadHelpers.Str(obj["session_id"])
                    ?? session.NativeSessionId;
                builder = new AsfEventBuilder(
                    masker, Kind, SourceReadHelpers.Str(obj["version"]), nativeId, session.ProjectRoot);

                var start = builder.Begin(AsfKind.SessionStart, ts);
                builder.SetContext(start, SourceReadHelpers.Str(obj["cwd"]), SourceReadHelpers.Str(obj["gitBranch"]));
                start.ProjectPath = SourceReadHelpers.Str(obj["cwd"]);
                start.Entrypoint = SourceReadHelpers.Str(obj["entrypoint"]);
                start.Synthetic = true;

                yield return builder.Seal(start);
            }

            var sidechain = SourceReadHelpers.Bool(obj["isSidechain"]) == true ? true : (bool?)null;
            var agentName = SourceReadHelpers.Str(obj["agentName"]);

            foreach (var e in TranslateLine(builder, obj, type, ts, pendingCalls, usageSeen))
            {
                e.Sidechain = sidechain;
                e.AgentName ??= agentName;
                yield return builder.Seal(e);
            }
        }

        if (builder is not null)
        {
            // Tool calls still open at EOF: an interrupted session, or a file
            // operation whose result Claude never wrote. The call already went out
            // as its own event; what is missing is the file_op, and dropping it
            // would silently undercount edits in exactly the sessions where the
            // agent was interrupted mid-edit.
            foreach (var call in pendingCalls.Values.OrderBy(c => c.Ts))
            {
                if (call.FilePath is null) continue;

                var e = builder.Begin(AsfKind.FileOp, call.Ts);
                e.Op = OpForTool(call.Tool);
                e.Ok = true;
                builder.SetPath(e, call.FilePath);

                yield return builder.Seal(e);
            }

            var end = builder.Begin(AsfKind.SessionEnd, lastTs);
            end.EndedAt = lastTs;
            end.Reason = "eof";
            end.Synthetic = true;

            yield return builder.Seal(end);
        }
    }

    /// UI/editor bookkeeping that carries no session content. Skipping these is
    /// what keeps a transcript's ASF projection roughly a third of its raw size.
    private static readonly HashSet<string> SkippedTypes = new(StringComparer.Ordinal)
    {
        "mode", "permission-mode", "ai-title", "last-prompt", "attachment",
        "file-history-snapshot", "file-history-delta", "agent-name", "summary",
    };

    /// A tool_use awaiting its tool_result. `FilePath` is read from the call's own
    /// input rather than from the result, because the result is the half that goes
    /// missing when a session is interrupted.
    private sealed record PendingCall(string Tool, DateTimeOffset Ts, string? FilePath);

    private static IEnumerable<AsfEvent> TranslateLine(
        AsfEventBuilder builder,
        JsonObject obj,
        string type,
        DateTimeOffset ts,
        Dictionary<string, PendingCall> pendingCalls,
        HashSet<string> usageSeen)
    {
        switch (type)
        {
            case "user":
                foreach (var e in TranslateUser(builder, obj, ts, pendingCalls)) yield return e;

                break;

            case "assistant":
                foreach (var e in TranslateAssistant(builder, obj, ts, pendingCalls, usageSeen)) yield return e;

                break;

            case "system":
                var systemEvent = TranslateSystem(builder, obj, ts);
                if (systemEvent is not null) yield return systemEvent;

                break;
        }
    }

    private static IEnumerable<AsfEvent> TranslateUser(
        AsfEventBuilder builder,
        JsonObject obj,
        DateTimeOffset ts,
        Dictionary<string, PendingCall> pendingCalls)
    {
        // isMeta lines are tool-injected context, not a human turn. Counting them
        // as prompts is the classic way to inflate a "prompts per day" chart.
        if (SourceReadHelpers.Bool(obj["isMeta"]) == true) yield break;

        var message = obj["message"] as JsonObject;
        var content = message?["content"];
        var isCompactSummary = SourceReadHelpers.Bool(obj["isCompactSummary"]) == true;

        if (content is JsonValue textValue && textValue.GetValueKind() == JsonValueKind.String)
        {
            var text = textValue.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return isCompactSummary
                    ? builder.SetSummary(builder.Begin(AsfKind.Compaction, ts), text)
                    : builder.SetText(builder.Begin(AsfKind.Prompt, ts), text);
            }

            yield break;
        }

        if (content is not JsonArray blocks) yield break;

        // One user message is one prompt even when Claude splits it across several
        // text blocks — joining here keeps "prompts per day" counting turns, not
        // block boundaries.
        var promptText = new StringBuilder();

        foreach (var block in blocks.OfType<JsonObject>())
        {
            var blockType = SourceReadHelpers.Str(block["type"]);

            if (blockType == "text")
            {
                var text = SourceReadHelpers.Str(block["text"]);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (promptText.Length > 0) promptText.Append('\n');
                promptText.Append(text);
            }
            else if (blockType == "tool_result")
            {
                var callId = SourceReadHelpers.Str(block["tool_use_id"]) ?? "";
                pendingCalls.TryGetValue(callId, out var call);

                var result = builder.Begin(AsfKind.ToolResult, ts);
                result.CallId = callId;
                result.Tool = call?.Tool;
                result.IsError = SourceReadHelpers.Bool(block["is_error"]) == true ? true : null;
                if (call is not null) result.DurationMs = (long)(ts - call.Ts).TotalMilliseconds;
                builder.SetOutput(result, ExtractText(block["content"]));

                // The sibling toolUseResult carries the structured half of the
                // same result — stdout/stderr and the patch. Merged, not emitted
                // separately, so one tool call stays one call and one result.
                var toolUseResult = obj["toolUseResult"];
                if (toolUseResult is JsonObject structured)
                {
                    if (result.Output is null or "")
                    {
                        var stdout = SourceReadHelpers.Str(structured["stdout"]);
                        var stderr = SourceReadHelpers.Str(structured["stderr"]);
                        var combined = string.Join("\n", new[] { stdout, stderr }.Where(s => !string.IsNullOrEmpty(s)));
                        if (combined.Length > 0) builder.SetOutput(result, combined);
                    }
                }

                yield return result;

                foreach (var fileOp in TranslateFileOp(
                    builder, obj["toolUseResult"], call, ts, result.IsError != true))
                {
                    yield return fileOp;
                }

                pendingCalls.Remove(callId);
            }
        }

        // The prompt comes last so a message that both closes tool calls and adds
        // user text keeps the results ahead of the turn they belong to.
        if (promptText.Length > 0)
        {
            var text = promptText.ToString();

            yield return isCompactSummary
                ? builder.SetSummary(builder.Begin(AsfKind.Compaction, ts), text)
                : builder.SetText(builder.Begin(AsfKind.Prompt, ts), text);
        }
    }

    private static IEnumerable<AsfEvent> TranslateAssistant(
        AsfEventBuilder builder,
        JsonObject obj,
        DateTimeOffset ts,
        Dictionary<string, PendingCall> pendingCalls,
        HashSet<string> usageSeen)
    {
        var message = obj["message"] as JsonObject;
        var model = SourceReadHelpers.Str(message?["model"]);
        var stopReason = SourceReadHelpers.Str(message?["stop_reason"]);

        if (message?["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                var blockType = SourceReadHelpers.Str(block["type"]);

                if (blockType == "text")
                {
                    var e = builder.Begin(AsfKind.Assistant, ts);
                    e.Model = model;
                    e.StopReason = stopReason;

                    yield return builder.SetText(e, SourceReadHelpers.Str(block["text"]) ?? "");
                }
                else if (blockType == "thinking")
                {
                    // `signature` is opaque vendor ciphertext — length and hash
                    // only, at every level.
                    var e = builder.Begin(AsfKind.Thinking, ts);
                    e.Model = model;

                    yield return builder.SetThinking(e, SourceReadHelpers.Str(block["thinking"]));
                }
                else if (blockType == "tool_use")
                {
                    var callId = SourceReadHelpers.Str(block["id"]) ?? "";
                    var tool = SourceReadHelpers.Str(block["name"]) ?? "unknown";
                    pendingCalls[callId] = new PendingCall(tool, ts, FileOpPath(tool, block["input"]));

                    var e = builder.Begin(AsfKind.ToolCall, ts);
                    e.CallId = callId;
                    e.Tool = tool;
                    e.Model = model;
                    e.IsMcp = tool.StartsWith("mcp__", StringComparison.Ordinal) ? true : null;
                    builder.SetArgs(e, block["input"]);

                    yield return e;
                }
            }
        }

        // See usageSeen above: only the first line of a multi-line assistant
        // message contributes its (identical) usage.
        var messageId = SourceReadHelpers.Str(message?["id"]);
        if (message?["usage"] is JsonObject usage &&
            (messageId is null || usageSeen.Add(messageId)))
        {
            var e = builder.Begin(AsfKind.Usage, ts);
            e.Model = model;
            e.StopReason = stopReason;
            e.InputTokens = SourceReadHelpers.Num(usage["input_tokens"]);
            e.OutputTokens = SourceReadHelpers.Num(usage["output_tokens"]);
            e.CacheReadTokens = SourceReadHelpers.Num(usage["cache_read_input_tokens"]);
            e.CacheWriteTokens = SourceReadHelpers.Num(usage["cache_creation_input_tokens"]);

            yield return e;
        }
    }

    private static AsfEvent? TranslateSystem(AsfEventBuilder builder, JsonObject obj, DateTimeOffset ts)
    {
        var level = SourceReadHelpers.Str(obj["level"]);
        var subtype = SourceReadHelpers.Str(obj["subtype"]);
        var isError = level == "error" ||
            (subtype is not null && subtype.Contains("error", StringComparison.OrdinalIgnoreCase));

        if (!isError) return null;

        var e = builder.Begin(AsfKind.Error, ts);
        e.DurationMs = SourceReadHelpers.Num(obj["durationMs"]);
        e.CallId = SourceReadHelpers.Str(obj["toolUseID"]);

        return builder.SetError(e, subtype ?? "system", SourceReadHelpers.Str(obj["content"]));
    }

    private static IEnumerable<AsfEvent> TranslateFileOp(
        AsfEventBuilder builder,
        JsonNode? toolUseResult,
        PendingCall? call,
        DateTimeOffset ts,
        bool ok)
    {
        var structured = toolUseResult as JsonObject;

        // When the call is known, its own input decides: `toolUseResult` is absent
        // on plenty of real results (interrupted tools, older transcripts), and
        // reading the path only from there loses the file op entirely. A result
        // whose call was never seen (truncated transcript head) falls back to the
        // structured half.
        var filePath = call is not null
            ? call.FilePath
            : SourceReadHelpers.Str(structured?["filePath"])
                ?? SourceReadHelpers.Str((structured?["file"] as JsonObject)?["filePath"]);
        if (string.IsNullOrEmpty(filePath)) yield break;

        var e = builder.Begin(AsfKind.FileOp, ts);
        e.Op = OpForTool(call?.Tool);
        e.Ok = ok;
        builder.SetPath(e, filePath);

        if (structured?["structuredPatch"] is JsonArray hunks)
        {
            var added = 0;
            var removed = 0;
            foreach (var hunk in hunks.OfType<JsonObject>())
            {
                if (hunk["lines"] is not JsonArray lines) continue;
                foreach (var lineNode in lines)
                {
                    var line = SourceReadHelpers.Str(lineNode);
                    if (line is null || line.Length == 0) continue;
                    if (line[0] == '+') added++;
                    else if (line[0] == '-') removed++;
                }
            }
            e.LinesAdded = added;
            e.LinesRemoved = removed;
        }

        yield return e;
    }

    /// The file a tool call touches, or null when the tool is not a file tool.
    /// Mirrors the tool set the brain has always counted as file operations.
    private static string? FileOpPath(string tool, JsonNode? input)
    {
        if (!FileOpTools.Contains(tool)) return null;
        if (input is not JsonObject obj) return null;

        return SourceReadHelpers.Str(obj["file_path"]) ?? SourceReadHelpers.Str(obj["notebook_path"]);
    }

    private static readonly HashSet<string> FileOpTools = new(StringComparer.Ordinal)
    {
        "Read", "NotebookRead", "Write", "Edit", "MultiEdit", "NotebookEdit",
    };

    private static string OpForTool(string? tool) => tool switch
    {
        "Read" or "NotebookRead" => "read",
        "Write" => "write",
        "Edit" => "edit",
        "MultiEdit" => "multi-edit",
        "NotebookEdit" => "notebook-edit",
        _ => "read",
    };

    /// tool_result content is a string, or an array of blocks each with `text`.
    private static string ExtractText(JsonNode? content)
    {
        switch (content)
        {
            case null:
                return "";

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return value.GetValue<string>();

            case JsonArray array:
            {
                var sb = new StringBuilder();
                foreach (var block in array.OfType<JsonObject>())
                {
                    var text = SourceReadHelpers.Str(block["text"]);
                    if (text is null) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }

                return sb.ToString();
            }

            default:
                return SourceReadHelpers.Str(content) ?? "";
        }
    }

    private static DateTimeOffset ParseTimestamp(JsonNode? node, DateTimeOffset fallback)
    {
        var raw = SourceReadHelpers.Str(node);
        if (raw is null) return fallback;

        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : fallback;
    }
}
