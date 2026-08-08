using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// One ASF (Agent Session Format) event — the uniform shape produced from Claude
/// Code, Codex and opencode alike. Spec: docs/specs/asf/01-event-envelope.md in
/// the agentic-live-www workspace.
///
/// The model is deliberately one flat POCO rather than a kind-per-type hierarchy:
/// serialization must omit absent fields (never emit null — see
/// docs/specs/asf/02-levels.md), and a flat shape makes the level projection a
/// property-clearing pass instead of a type conversion.
///
/// Every property is null-by-default and serialized with
/// JsonIgnoreCondition.WhenWritingNull, so "redacted" and "not applicable to this
/// kind" collapse to the same wire form: the key is simply absent.
public sealed class AsfEvent
{
    // ---- envelope ---------------------------------------------------------

    [JsonPropertyName("v")] public int V { get; set; } = 1;

    /// "e_" + sha256 hex, computed over the FULL event before redaction.
    /// Level-independent by construction — see AsfEventId.
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// 0-based ordinal within the session, gap-free, assigned from source order.
    [JsonPropertyName("seq")] public int Seq { get; set; }

    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; set; }

    /// claude | codex | opencode
    [JsonPropertyName("src")] public string Src { get; set; } = "";

    /// The agent tool's own version string, verbatim.
    [JsonPropertyName("srcVersion")] public string? SrcVersion { get; set; }

    /// "s_" + 32 hex of sha256("<src>:<native session id>").
    [JsonPropertyName("session")] public string Session { get; set; } = "";

    /// "p_" + 32 hex of sha256(absolute project root).
    [JsonPropertyName("project")] public string Project { get; set; } = "";

    /// See AsfKind.
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// The level THIS copy was emitted at. Excluded from the id hash.
    [JsonPropertyName("level")] public string Level { get; set; } = AsfLevel.Full;

    // ---- common optionals -------------------------------------------------

    /// Groups the events of one request/response cycle.
    [JsonPropertyName("turnId")] public string? TurnId { get; set; }

    /// The id of the logically preceding event.
    [JsonPropertyName("parentId")] public string? ParentId { get; set; }

    /// True for subagent transcripts.
    [JsonPropertyName("sidechain")] public bool? Sidechain { get; set; }

    /// True when the parser manufactured this event rather than reading it.
    [JsonPropertyName("synthetic")] public bool? Synthetic { get; set; }

    [JsonPropertyName("agentName")] public string? AgentName { get; set; }

    // ---- session_start / session_end --------------------------------------

    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("cwdHash")] public string? CwdHash { get; set; }
    [JsonPropertyName("gitBranch")] public string? GitBranch { get; set; }
    [JsonPropertyName("gitBranchHash")] public string? GitBranchHash { get; set; }
    [JsonPropertyName("projectPath")] public string? ProjectPath { get; set; }
    [JsonPropertyName("entrypoint")] public string? Entrypoint { get; set; }
    [JsonPropertyName("agent")] public string? Agent { get; set; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("turns")] public int? Turns { get; set; }

    // ---- text-bearing kinds (prompt / assistant / thinking / compaction) ---

    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("textLen")] public int? TextLen { get; set; }
    [JsonPropertyName("textHash")] public string? TextHash { get; set; }
    [JsonPropertyName("summaryLen")] public int? SummaryLen { get; set; }
    [JsonPropertyName("summaryHash")] public string? SummaryHash { get; set; }
    [JsonPropertyName("attachments")] public List<string>? Attachments { get; set; }
    [JsonPropertyName("attachmentCount")] public int? AttachmentCount { get; set; }

    // ---- tool_call / tool_result ------------------------------------------

    [JsonPropertyName("callId")] public string? CallId { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("args")] public JsonNode? Args { get; set; }
    [JsonPropertyName("argsHash")] public string? ArgsHash { get; set; }
    [JsonPropertyName("argsBytes")] public long? ArgsBytes { get; set; }
    [JsonPropertyName("output")] public string? Output { get; set; }
    [JsonPropertyName("outputHash")] public string? OutputHash { get; set; }
    [JsonPropertyName("outputBytes")] public long? OutputBytes { get; set; }
    [JsonPropertyName("isError")] public bool? IsError { get; set; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }

    /// The source kept only a head/tail of this output.
    [JsonPropertyName("truncated")] public bool? Truncated { get; set; }

    /// opencode spilled this output to tool-output/tool_* — see docs/specs/asf/05-blob-backup.md.
    [JsonPropertyName("spilled")] public bool? Spilled { get; set; }

    [JsonPropertyName("isMcp")] public bool? IsMcp { get; set; }

    // ---- file_op ----------------------------------------------------------

    /// read | write | edit | multi-edit | notebook-edit | add | update | delete
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("pathHash")] public string? PathHash { get; set; }
    [JsonPropertyName("ext")] public string? Ext { get; set; }
    [JsonPropertyName("depth")] public int? Depth { get; set; }
    [JsonPropertyName("linesAdded")] public int? LinesAdded { get; set; }
    [JsonPropertyName("linesRemoved")] public int? LinesRemoved { get; set; }
    [JsonPropertyName("ok")] public bool? Ok { get; set; }

    // ---- error ------------------------------------------------------------

    [JsonPropertyName("errorClass")] public string? ErrorClass { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("fatal")] public bool? Fatal { get; set; }

    // ---- plan -------------------------------------------------------------

    [JsonPropertyName("items")] public List<string>? Items { get; set; }
    [JsonPropertyName("itemCount")] public int? ItemCount { get; set; }
    [JsonPropertyName("doneCount")] public int? DoneCount { get; set; }

    // ---- usage ------------------------------------------------------------

    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("stopReason")] public string? StopReason { get; set; }
    [JsonPropertyName("inputTokens")] public long? InputTokens { get; set; }
    [JsonPropertyName("outputTokens")] public long? OutputTokens { get; set; }
    [JsonPropertyName("cacheReadTokens")] public long? CacheReadTokens { get; set; }
    [JsonPropertyName("cacheWriteTokens")] public long? CacheWriteTokens { get; set; }
    [JsonPropertyName("reasoningTokens")] public long? ReasoningTokens { get; set; }
    [JsonPropertyName("costUsd")] public double? CostUsd { get; set; }

    // ---- provenance -------------------------------------------------------

    /// Where this copy of the session was read from, when that was somewhere
    /// other than the tool's own directory on this machine —
    /// `docker:&lt;volume name&gt;` for a session rescued out of a container volume.
    /// Absent means the ordinary host path.
    ///
    /// **Excluded from the id hash** (see AsfEventId), and that exclusion is the
    /// whole point: the same session recovered from two volumes, or present both
    /// on the host and in a volume, must collapse to one event rather than
    /// double-counting. Origin says where a copy came from; it is not part of
    /// what the event *is*.
    [JsonPropertyName("origin")] public string? Origin { get; set; }

    // ---- server-side annotation -------------------------------------------

    /// Set by the receiving server when its masker caught something the client
    /// missed. Never set by the client; never part of the id.
    [JsonPropertyName("serverMasked")] public bool? ServerMasked { get; set; }

    public AsfEvent Clone() => (AsfEvent)MemberwiseClone();
}

/// The `kind` catalogue. String constants rather than an enum so that an unknown
/// kind from a newer producer round-trips instead of throwing — the spec requires
/// a newer client never to make an older reader drop data.
public static class AsfKind
{
    public const string SessionStart = "session_start";
    public const string Prompt = "prompt";
    public const string Assistant = "assistant";
    public const string Thinking = "thinking";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
    public const string FileOp = "file_op";
    public const string Error = "error";
    public const string Plan = "plan";
    public const string Usage = "usage";
    public const string Compaction = "compaction";
    public const string SessionEnd = "session_end";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        SessionStart, Prompt, Assistant, Thinking, ToolCall, ToolResult,
        FileOp, Error, Plan, Usage, Compaction, SessionEnd,
    };
}

/// Source identifiers. Also the chunk-filename component.
public static class AsfSource
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string OpenCode = "opencode";
}
