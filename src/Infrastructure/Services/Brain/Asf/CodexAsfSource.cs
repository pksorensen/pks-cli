using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Codex → ASF. Reads ~/.codex/sessions/&lt;yyyy&gt;/&lt;mm&gt;/&lt;dd&gt;/rollout-*.jsonl and
/// ~/.codex/archived_sessions/ (where the TUI moves deleted threads — still on
/// disk, still ours to back up).
///
/// Mapping table: docs/specs/asf/01-event-envelope.md §Codex.
///
/// Codex has no retention setting at all: it never prunes rollouts, so this
/// source sees everything since install. Nothing here deletes anything either.
public sealed class CodexAsfSource : IAgentSessionSource
{
    private readonly IBrainPathResolver _paths;

    public CodexAsfSource(IBrainPathResolver paths) => _paths = paths;

    public string Kind => AsfSource.Codex;

    public bool IsAvailable =>
        Directory.Exists(_paths.CodexSessionsRoot) || Directory.Exists(_paths.CodexArchivedSessionsRoot);

    public string Location => _paths.CodexSessionsRoot;

    public IEnumerable<DiscoveredAgentSession> Discover(string? projectFilter = null)
    {
        foreach (var root in new[] { _paths.CodexSessionsRoot, _paths.CodexArchivedSessionsRoot })
        {
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory
                .EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                var meta = SourceReadHelpers.PeekFirstObject(
                    file,
                    o => SourceReadHelpers.Str(o["type"]) == "session_meta");
                var payload = meta?["payload"] as JsonObject;

                var cwd = _paths.Normalize(SourceReadHelpers.Str(payload?["cwd"])) ?? "";
                var slug = cwd.Length > 0 ? _paths.EncodeSlug(cwd) : "-unknown";

                if (projectFilter is { Length: > 0 } &&
                    !slug.Contains(projectFilter, StringComparison.OrdinalIgnoreCase)) continue;

                FileInfo info;
                try { info = new FileInfo(file); }
                catch (IOException) { continue; }

                // A rollout's own session_id is authoritative — the filename repeats
                // it, but a copied or renamed file would otherwise mint a new session.
                var sessionId = SourceReadHelpers.Str(payload?["session_id"])
                    ?? Path.GetFileNameWithoutExtension(file);

                yield return new DiscoveredAgentSession(
                    Kind, sessionId, slug, cwd, file, info.LastWriteTimeUtc, info.Length);
            }
        }
    }

    public long? WriteRawBackup(DiscoveredAgentSession session, Stream destination) =>
        SourceReadHelpers.CopyBackingFile(session, destination);

    public async IAsyncEnumerable<AsfEvent> ReadAsync(
        DiscoveredAgentSession session,
        ISecretMasker masker,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        AsfEventBuilder? builder = null;
        var lastTs = new DateTimeOffset(session.LastModifiedUtc, TimeSpan.Zero);
        var turnState = new CodexTurnState();

        await foreach (var line in SourceReadHelpers.ReadJsonLinesAsync(session.SourcePath, ct))
        {
            if (line is not JsonObject obj) continue;

            var type = SourceReadHelpers.Str(obj["type"]);
            if (type is null) continue;

            var payload = obj["payload"] as JsonObject;
            var ts = ParseTimestamp(obj["timestamp"], lastTs);
            lastTs = ts;

            if (type == "session_meta")
            {
                // A forked thread writes a second session_meta mid-file. The first
                // one owns the session; later ones only refresh the turn context.
                if (builder is not null) continue;

                builder = new AsfEventBuilder(
                    masker,
                    Kind,
                    SourceReadHelpers.Str(payload?["cli_version"]),
                    session.NativeSessionId,
                    session.ProjectRoot);

                var start = builder.Begin(AsfKind.SessionStart, ts);
                var cwd = SourceReadHelpers.Str(payload?["cwd"]);
                builder.SetContext(start, cwd, null);
                start.ProjectPath = cwd;
                start.Entrypoint = SourceReadHelpers.Str(payload?["originator"]);
                start.Model = SourceReadHelpers.Str(payload?["model_provider"]);

                yield return builder.Seal(start);

                continue;
            }

            if (builder is null) continue;

            foreach (var e in Translate(builder, type, payload, ts, turnState))
            {
                yield return builder.Seal(e);
            }
        }

        if (builder is not null)
        {
            // Any turn whose cumulative token_count never got flushed by a
            // task_complete still owes one usage event.
            foreach (var e in turnState.FlushPendingUsage(builder, lastTs))
            {
                yield return builder.Seal(e);
            }

            var end = builder.Begin(AsfKind.SessionEnd, lastTs);
            end.EndedAt = lastTs;
            end.Reason = "eof";
            end.Turns = turnState.TurnCount;
            end.Synthetic = true;

            yield return builder.Seal(end);
        }
    }

    private static IEnumerable<AsfEvent> Translate(
        AsfEventBuilder builder,
        string type,
        JsonObject? payload,
        DateTimeOffset ts,
        CodexTurnState state)
    {
        if (payload is null) yield break;

        switch (type)
        {
            case "turn_context":
                // No event of its own: the model/cwd/policy of a turn are carried
                // by the events inside it.
                state.EnterTurn(SourceReadHelpers.Str(payload["turn_id"]));
                state.Model = SourceReadHelpers.Str(payload["model"]) ?? state.Model;

                yield break;

            case "compacted":
                // Paired with event_msg/context_compacted; one compaction, not two.
                if (state.CompactionPending) yield break;
                state.CompactionPending = true;

                yield return builder.SetSummary(
                    Stamp(builder.Begin(AsfKind.Compaction, ts), state),
                    SourceReadHelpers.Str(payload["message"]));

                yield break;

            case "world_state":
                // A full re-dump of AGENTS.md every turn. Nothing happened.
                yield break;

            case "event_msg":
                foreach (var e in TranslateEventMsg(builder, payload, ts, state)) yield return e;

                yield break;

            case "response_item":
                foreach (var e in TranslateResponseItem(builder, payload, ts, state)) yield return e;

                yield break;
        }
    }

    private static IEnumerable<AsfEvent> TranslateEventMsg(
        AsfEventBuilder builder,
        JsonObject payload,
        DateTimeOffset ts,
        CodexTurnState state)
    {
        var kind = SourceReadHelpers.Str(payload["type"]);
        var turnId = SourceReadHelpers.Str(payload["turn_id"]);
        if (turnId is not null) state.EnterTurn(turnId);

        switch (kind)
        {
            case "task_started":
                state.EnterTurn(turnId);

                yield break;

            case "user_message":
            {
                var e = Stamp(builder.Begin(AsfKind.Prompt, ts), state);
                builder.SetText(e, SourceReadHelpers.Str(payload["message"]) ?? "");
                var attachments = CountImages(payload);
                if (attachments.Count > 0) builder.SetAttachments(e, attachments);

                yield return e;

                yield break;
            }

            case "agent_message":
            {
                var e = Stamp(builder.Begin(AsfKind.Assistant, ts), state);

                yield return builder.SetText(e, SourceReadHelpers.Str(payload["message"]) ?? "");

                yield break;
            }

            case "context_compacted":
                if (state.CompactionPending)
                {
                    state.CompactionPending = false;

                    yield break;
                }

                yield return Stamp(builder.Begin(AsfKind.Compaction, ts), state);

                yield break;

            case "token_count":
            {
                // Cumulative, emitted on nearly every stream tick — 3100 lines in
                // one real session. Only the last per turn survives, as a delta.
                // See docs/specs/asf/01-event-envelope.md §"The token_count rule".
                var total = payload["info"]?["total_token_usage"] as JsonObject;
                if (total is not null) state.RecordCumulativeUsage(total);

                yield break;
            }

            case "task_complete":
            {
                foreach (var usage in state.FlushPendingUsage(builder, ts)) yield return usage;

                var error = SourceReadHelpers.Str(payload["error"]);
                if (error is not null)
                {
                    var e = Stamp(builder.Begin(AsfKind.Error, ts), state);
                    e.DurationMs = SourceReadHelpers.Num(payload["duration_ms"]);

                    yield return builder.SetError(e, "task_failed", error);
                }

                yield break;
            }

            case "turn_aborted":
            {
                foreach (var usage in state.FlushPendingUsage(builder, ts)) yield return usage;

                var e = Stamp(builder.Begin(AsfKind.Error, ts), state);
                e.DurationMs = SourceReadHelpers.Num(payload["duration_ms"]);

                yield return builder.SetError(
                    e, "turn_aborted", SourceReadHelpers.Str(payload["reason"]));

                yield break;
            }

            case "patch_apply_end":
            {
                var ok = SourceReadHelpers.Bool(payload["success"]) ?? true;
                foreach (var e in TranslatePatch(builder, payload, ts, state, ok)) yield return e;

                yield break;
            }

            case "mcp_tool_call_end":
            {
                var invocation = payload["invocation"] as JsonObject;
                var server = SourceReadHelpers.Str(invocation?["server"]);
                var tool = SourceReadHelpers.Str(invocation?["tool"]);

                var call = Stamp(builder.Begin(AsfKind.ToolCall, ts), state);
                call.CallId = SourceReadHelpers.Str(payload["call_id"]);
                call.Tool = $"mcp__{server}__{tool}";
                call.IsMcp = true;
                builder.SetArgs(call, invocation?["arguments"]);

                yield return call;

                var result = Stamp(builder.Begin(AsfKind.ToolResult, ts), state);
                result.CallId = call.CallId;
                result.Tool = call.Tool;
                result.IsMcp = true;
                result.DurationMs = DurationMs(payload["duration"]);
                var ok = payload["result"]?["Ok"];
                result.IsError = ok is null ? true : null;
                builder.SetOutput(result, McpResultText(payload["result"]));

                yield return result;

                yield break;
            }

            case "thread_goal_updated":
            {
                var goal = payload["goal"] as JsonObject;
                var objective = SourceReadHelpers.Str(goal?["objective"]);
                if (objective is null) yield break;

                var e = Stamp(builder.Begin(AsfKind.Plan, ts), state);
                e.Reason = SourceReadHelpers.Str(goal?["status"]);

                yield return builder.SetPlan(e, new[] { objective }, 0);

                yield break;
            }
        }
    }

    private static IEnumerable<AsfEvent> TranslateResponseItem(
        AsfEventBuilder builder,
        JsonObject payload,
        DateTimeOffset ts,
        CodexTurnState state)
    {
        var kind = SourceReadHelpers.Str(payload["type"]);
        var turnId = SourceReadHelpers.Str(
            payload["internal_chat_message_metadata_passthrough"]?["turn_id"]);
        if (turnId is not null) state.EnterTurn(turnId);

        switch (kind)
        {
            case "reasoning":
            {
                // `encrypted_content` is opaque vendor ciphertext; `summary` is the
                // only readable part and it is usually empty. Length only.
                var e = Stamp(builder.Begin(AsfKind.Thinking, ts), state);

                yield return builder.SetThinking(e, ReasoningText(payload));
            }

                yield break;

            case "custom_tool_call":
            case "function_call":
            {
                var callId = SourceReadHelpers.Str(payload["call_id"]) ?? "";
                var tool = SourceReadHelpers.Str(payload["name"]) ?? "unknown";
                state.OpenCall(callId, tool, ts);

                var e = Stamp(builder.Begin(AsfKind.ToolCall, ts), state);
                e.CallId = callId;
                e.Tool = tool;

                // custom_tool_call carries `input` as a raw string (a JS snippet);
                // function_call carries `arguments` as a JSON *string*. Parsing the
                // latter means argsHash is stable against key reordering upstream.
                var input = payload["input"] ?? payload["arguments"];
                builder.SetArgs(e, NormalizeArgs(input));

                yield return e;
            }

                yield break;

            case "custom_tool_call_output":
            case "function_call_output":
            {
                var callId = SourceReadHelpers.Str(payload["call_id"]) ?? "";
                var call = state.CloseCall(callId);

                var e = Stamp(builder.Begin(AsfKind.ToolResult, ts), state);
                e.CallId = callId;
                e.Tool = call?.Tool;
                if (call is not null) e.DurationMs = (long)(ts - call.Value.Ts).TotalMilliseconds;
                builder.SetOutput(e, OutputText(payload["output"]));

                yield return e;
            }

                yield break;

            // `message` is skipped entirely: role `developer` is the system prompt
            // re-emitted every turn, and roles user/assistant duplicate the
            // event_msg pair above. Counting them doubles every prompt chart.
            case "message":
            default:
                yield break;
        }
    }

    private static IEnumerable<AsfEvent> TranslatePatch(
        AsfEventBuilder builder,
        JsonObject payload,
        DateTimeOffset ts,
        CodexTurnState state,
        bool ok)
    {
        if (payload["changes"] is not JsonObject changes) yield break;

        foreach (var (path, change) in changes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (change is not JsonObject c) continue;

            var op = SourceReadHelpers.Str(c["type"]) ?? "update";
            var e = Stamp(builder.Begin(AsfKind.FileOp, ts), state);
            e.Op = op == "update" ? "edit" : op;
            e.Ok = ok;
            e.CallId = SourceReadHelpers.Str(payload["call_id"]);
            builder.SetPath(e, path);

            switch (op)
            {
                case "update":
                {
                    var (added, removed) = SourceReadHelpers.CountDiffLines(
                        SourceReadHelpers.Str(c["unified_diff"]));
                    e.LinesAdded = added;
                    e.LinesRemoved = removed;

                    break;
                }

                case "add":
                    e.LinesAdded = LineCount(SourceReadHelpers.Str(c["content"]));
                    e.LinesRemoved = 0;

                    break;

                case "delete":
                    e.LinesAdded = 0;
                    e.LinesRemoved = LineCount(SourceReadHelpers.Str(c["content"]));

                    break;
            }

            yield return e;
        }
    }

    private static AsfEvent Stamp(AsfEvent e, CodexTurnState state)
    {
        e.TurnId = state.TurnId;
        e.Model ??= state.Model;

        return e;
    }

    private static int LineCount(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;

    /// `output` is an array of `{type:"input_text", text}` blocks, or a bare string.
    private static string OutputText(JsonNode? output)
    {
        if (output is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>();

        if (output is not JsonArray array) return SourceReadHelpers.Str(output) ?? "";

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

    private static string McpResultText(JsonNode? result)
    {
        var content = result?["Ok"]?["content"] ?? result?["Err"];

        return content is null ? "" : OutputText(content);
    }

    private static string? ReasoningText(JsonObject payload)
    {
        if (payload["summary"] is JsonArray summary && summary.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (var block in summary)
            {
                var text = block is JsonObject o ? SourceReadHelpers.Str(o["text"]) : SourceReadHelpers.Str(block);
                if (text is null) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }

            return sb.ToString();
        }

        return SourceReadHelpers.Str(payload["content"]);
    }

    /// function_call ships its arguments as a JSON string; parse it so the hash is
    /// taken over the same canonical form as every other source's args. When it is
    /// not JSON (custom_tool_call's `input` is a JS snippet), keep it as a string
    /// under a `raw` key so the masker still sees it.
    private static JsonNode? NormalizeArgs(JsonNode? input)
    {
        if (input is null) return null;
        if (input is JsonObject or JsonArray) return input.DeepClone();

        var text = SourceReadHelpers.Str(input);
        if (text is null) return null;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonNode.Parse(text);
                if (parsed is not null) return parsed;
            }
            catch (JsonException)
            {
                // Not JSON after all — fall through to the raw wrapper.
            }
        }

        return new JsonObject { ["raw"] = text };
    }

    /// Codex durations are `{secs, nanos}`.
    private static long? DurationMs(JsonNode? duration)
    {
        if (duration is not JsonObject d) return null;
        var secs = SourceReadHelpers.Num(d["secs"]);
        var nanos = SourceReadHelpers.Num(d["nanos"]);
        if (secs is null && nanos is null) return null;

        return (secs ?? 0) * 1000 + (nanos ?? 0) / 1_000_000;
    }

    private static List<string> CountImages(JsonObject payload)
    {
        var names = new List<string>();
        foreach (var key in new[] { "images", "local_images" })
        {
            if (payload[key] is not JsonArray array) continue;
            foreach (var item in array)
            {
                names.Add(SourceReadHelpers.Str(item is JsonObject o ? o["path"] ?? o["url"] : item) ?? key);
            }
        }

        return names;
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

    /// Per-session mutable parse state: the current turn, the open tool calls, and
    /// the cumulative token totals waiting to be turned into one delta.
    private sealed class CodexTurnState
    {
        private readonly Dictionary<string, (string Tool, DateTimeOffset Ts)> _openCalls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _turns = new(StringComparer.Ordinal);
        private JsonObject? _pendingUsage;
        private CumulativeUsage _emitted;

        public string? TurnId { get; private set; }

        public string? Model { get; set; }

        public bool CompactionPending { get; set; }

        public int TurnCount => _turns.Count;

        public void EnterTurn(string? turnId)
        {
            if (turnId is null || turnId == TurnId) return;
            TurnId = turnId;
            _turns.Add(turnId);
        }

        public void OpenCall(string callId, string tool, DateTimeOffset ts) =>
            _openCalls[callId] = (tool, ts);

        public (string Tool, DateTimeOffset Ts)? CloseCall(string callId)
        {
            if (!_openCalls.Remove(callId, out var call)) return null;

            return call;
        }

        public void RecordCumulativeUsage(JsonObject total) => _pendingUsage = total;

        /// Emits at most one `usage` event, holding the delta between the latest
        /// cumulative total and the last one already emitted. Sums over `usage`
        /// events therefore equal the session total instead of a triangular number.
        public IEnumerable<AsfEvent> FlushPendingUsage(AsfEventBuilder builder, DateTimeOffset ts)
        {
            if (_pendingUsage is null) yield break;

            var current = CumulativeUsage.From(_pendingUsage);
            _pendingUsage = null;

            var delta = current - _emitted;
            if (delta.IsEmpty) yield break;
            _emitted = current;

            var e = builder.Begin(AsfKind.Usage, ts);
            e.TurnId = TurnId;
            e.Model = Model;
            e.InputTokens = delta.Input;
            e.OutputTokens = delta.Output;
            e.CacheReadTokens = delta.CacheRead;
            e.CacheWriteTokens = delta.CacheWrite;
            e.ReasoningTokens = delta.Reasoning;

            yield return e;
        }
    }

    private readonly record struct CumulativeUsage(
        long Input, long Output, long CacheRead, long CacheWrite, long Reasoning)
    {
        public static CumulativeUsage From(JsonObject total) => new(
            SourceReadHelpers.Num(total["input_tokens"]) ?? 0,
            SourceReadHelpers.Num(total["output_tokens"]) ?? 0,
            SourceReadHelpers.Num(total["cached_input_tokens"]) ?? 0,
            SourceReadHelpers.Num(total["cache_write_input_tokens"]) ?? 0,
            SourceReadHelpers.Num(total["reasoning_output_tokens"]) ?? 0);

        public bool IsEmpty => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite == 0 && Reasoning == 0;

        /// Clamped at zero: a forked or resumed thread can restart its counters, and
        /// a negative delta would silently subtract from the day's totals.
        public static CumulativeUsage operator -(CumulativeUsage a, CumulativeUsage b) => new(
            Math.Max(0, a.Input - b.Input),
            Math.Max(0, a.Output - b.Output),
            Math.Max(0, a.CacheRead - b.CacheRead),
            Math.Max(0, a.CacheWrite - b.CacheWrite),
            Math.Max(0, a.Reasoning - b.Reasoning));
    }
}
