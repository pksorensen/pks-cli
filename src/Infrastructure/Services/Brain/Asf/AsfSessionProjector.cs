using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Brain.Asf;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// ASF event stream → the brain's local firehose rows.
///
/// This is the join between the two halves of the design: every source produces
/// ASF, ASF is what gets exported to agentics.dk, and this projector is what keeps
/// the *local* index (`prompts.jsonl`, `tools.jsonl`, `files.jsonl`,
/// `errors.jsonl`, per-session metadata) derived from exactly the same events. A
/// second parser feeding the local rows would drift from the uploaded chunks, and
/// then `brain heartbeat` and the profile graphs would disagree about the same
/// day — which is the one failure this whole feature cannot afford.
///
/// Consequences of that choice, all deliberate:
///
///   * Rows carry **masked** text. The ASF sources mask before hashing, so a
///     secret that reaches the firehose was never in the transcript to begin with.
///   * `LineCount` counts ASF events, not source lines — the source may not have
///     lines at all (opencode is a database).
///   * Claude-internal identifiers (`uuid`, `promptId`, the parent assistant uuid)
///     are not part of ASF and are therefore no longer stored. Nothing in the
///     brain reads them; `InputPreview`, which `BrainExtractContextBuilder` does
///     read for plan bodies, is preserved.
public sealed class AsfSessionProjector
{
    /// Tools that mean "a subagent ran". `Agent` is Claude's, `Task` is the older
    /// name and still appears in archived transcripts.
    private static readonly HashSet<string> SubagentTools = new(StringComparer.Ordinal) { "Agent", "Task" };

    public ParsedSession Project(DiscoveredAgentSession session, IReadOnlyList<AsfEvent> events)
    {
        var metadata = new SessionMetadata
        {
            SessionId = session.NativeSessionId,
            ProjectSlug = session.ProjectSlug,
            SourcePath = session.SourcePath,
            SourceMtimeUtc = session.LastModifiedUtc,
            SourceBytes = session.Bytes,
            LineCount = events.Count,
        };

        var parsed = new ParsedSession { Metadata = metadata };
        if (events.Count == 0) return parsed;

        var sessionId = metadata.SessionId;
        var slug = metadata.ProjectSlug;

        var branches = new SortedSet<string>(StringComparer.Ordinal);
        var modelOrder = new List<string>();
        var modelSet = new HashSet<string>(StringComparer.Ordinal);
        var tokenTotals = new Dictionary<string, ModelTokenTotals>(StringComparer.Ordinal);
        var calls = new Dictionary<string, ToolCallRow>(StringComparer.Ordinal);
        var compact = new CompactState();

        DateTime? minTs = null, maxTs = null;
        string? cwd = null;
        var assistantEvents = 0;
        var usageEvents = 0;
        var thinkingBlocks = 0;
        var subagentInvocations = 0;
        var interruptions = 0;

        foreach (var e in events)
        {
            var ts = e.Ts.UtcDateTime;
            if (minTs is null || ts < minTs) minTs = ts;
            if (maxTs is null || ts > maxTs) maxTs = ts;

            cwd ??= e.Cwd;
            if (e.GitBranch is { Length: > 0 } branch) branches.Add(branch);
            if (e.Model is { Length: > 0 } model && modelSet.Add(model)) modelOrder.Add(model);

            switch (e.Kind)
            {
                case AsfKind.Prompt:
                    ProjectPrompt(parsed, e, ts, sessionId, slug, cwd, compact, ref interruptions);
                    break;

                case AsfKind.Compaction:
                    ProjectCompaction(parsed, e, ts, sessionId, slug, cwd, compact);
                    break;

                case AsfKind.Assistant:
                    assistantEvents++;
                    break;

                case AsfKind.Thinking:
                    thinkingBlocks++;
                    break;

                case AsfKind.ToolCall:
                    ProjectToolCall(parsed, calls, e, ts, sessionId, slug, ref subagentInvocations);
                    break;

                case AsfKind.ToolResult:
                    ProjectToolResult(parsed, calls, e, ts, sessionId, slug);
                    break;

                case AsfKind.FileOp:
                    if (e.Path is { Length: > 0 })
                    {
                        parsed.FileOps.Add(new FileOpRow
                        {
                            SessionId = sessionId,
                            ProjectSlug = slug,
                            TimestampUtc = ts,
                            Op = e.Op ?? "read",
                            FilePath = e.Path,
                            Success = e.Ok != false,
                        });
                    }

                    break;

                case AsfKind.Error:
                    parsed.Errors.Add(new ErrorRow
                    {
                        SessionId = sessionId,
                        ProjectSlug = slug,
                        TimestampUtc = ts,
                        Kind = "error",
                        ToolName = e.Tool,
                        ToolUseId = e.CallId,
                        Snippet = Truncate(e.Message, 500),
                        DurationMs = e.DurationMs,
                    });
                    break;

                case AsfKind.Usage:
                    usageEvents++;
                    AccumulateUsage(tokenTotals, e);
                    break;
            }
        }

        // Calls whose result never arrived still count — the agent did invoke the
        // tool. This mirrors the drain the Claude-only parser did at EOF.
        foreach (var row in calls.Values) parsed.ToolCalls.Add(row);

        metadata.FirstTimestampUtc = minTs;
        metadata.LastTimestampUtc = maxTs;
        metadata.Cwd = cwd ?? session.ProjectRoot;
        metadata.GitBranches = branches.ToList();
        metadata.Models = modelOrder;
        metadata.TokensByModel = tokenTotals.Values.OrderBy(m => m.Model, StringComparer.Ordinal).ToList();
        metadata.PromptCount = parsed.Prompts.Count;

        // One `usage` event is one model response, which is the honest definition
        // of a turn. Sources that report no usage at all (an interrupted session)
        // fall back to counting assistant messages.
        metadata.AssistantTurnCount = usageEvents > 0 ? usageEvents : assistantEvents;
        metadata.ToolCallCount = parsed.ToolCalls.Count;
        metadata.ToolErrorCount = parsed.Errors.Count(x => x.Kind == "error");
        metadata.ThinkingBlockCount = thinkingBlocks;
        metadata.FileOpCount = parsed.FileOps.Count;
        metadata.SubagentInvocationCount = subagentInvocations;
        metadata.PlanEventCount = parsed.PlanEvents.Count;
        metadata.InterruptionCount = interruptions;
        metadata.TopTools = TopN(parsed.ToolCalls.GroupBy(t => t.ToolName).ToDictionary(g => g.Key, g => (long)g.Count()), 50);
        metadata.TopFiles = TopN(parsed.FileOps.GroupBy(f => f.FilePath).ToDictionary(g => g.Key, g => (long)g.Count()), 50);
        metadata.TopErrors = TopN(
            parsed.Errors.GroupBy(x => $"{x.ToolName ?? "?"}::{x.InputDigest ?? "?"}").ToDictionary(g => g.Key, g => (long)g.Count()),
            20);
        metadata.Skills = parsed.Prompts
            .Where(p => p.IsSlash && p.SlashCommand is { Length: > 0 })
            .Select(p => p.SlashCommand!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        metadata.Subagents = parsed.ToolCalls
            .Where(t => t.IsSubagent && t.SubagentType is { Length: > 0 })
            .Select(t => t.SubagentType!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        return parsed;
    }

    // ── prompts ───────────────────────────────────────────────────────────────

    private static void ProjectPrompt(
        ParsedSession parsed, AsfEvent e, DateTime ts, string sessionId, string slug,
        string? cwd, CompactState compact, ref int interruptions)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;

        // "[Request interrupted…" is Claude's marker for a turn the user killed.
        // It is a signal worth counting, but it is not something the user typed.
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            interruptions++;
            parsed.Errors.Add(new ErrorRow
            {
                SessionId = sessionId,
                ProjectSlug = slug,
                TimestampUtc = ts,
                Kind = "interruption",
                Snippet = Truncate(text, 200),
            });

            return;
        }

        // A manual /compact arrives as an ordinary prompt containing the command
        // tags; the summary follows as a `compaction` event. Carrying a one-shot
        // flag between them is what lets a compaction be labelled manual vs auto.
        if (text.Contains("<command-name>/compact</command-name>", StringComparison.Ordinal))
        {
            compact.PendingManual = true;
            compact.PendingArgs = ExtractTag(text, "command-args");
        }
        else
        {
            compact.Clear();
        }

        var (isSlash, slashCommand, slashArgs) = ParseSlash(text);
        parsed.Prompts.Add(new PromptRow
        {
            SessionId = sessionId,
            ProjectSlug = slug,
            TimestampUtc = ts,
            Uuid = e.Id ?? string.Empty,
            Text = text,
            TextHash = ShortHash(text),
            Cwd = e.Cwd ?? cwd,
            GitBranch = e.GitBranch,
            Length = text.Length,
            IsSlash = isSlash,
            SlashCommand = slashCommand,
            SlashArgs = slashArgs,
        });
    }

    private static void ProjectCompaction(
        ParsedSession parsed, AsfEvent e, DateTime ts, string sessionId, string slug,
        string? cwd, CompactState compact)
    {
        var text = e.Text;
        if (string.IsNullOrEmpty(text)) return;

        parsed.Prompts.Add(new PromptRow
        {
            SessionId = sessionId,
            ProjectSlug = slug,
            TimestampUtc = ts,
            Uuid = e.Id ?? string.Empty,
            Text = text,
            TextHash = ShortHash(text),
            Cwd = e.Cwd ?? cwd,
            GitBranch = e.GitBranch,
            Length = text.Length,
            IsSlash = false,
            IsCompactSummary = true,
            CompactTrigger = compact.PendingManual ? "manual" : "auto",
            CompactInstructions = compact.PendingManual ? compact.PendingArgs : null,
        });
        compact.Clear();
    }

    // ── tools ─────────────────────────────────────────────────────────────────

    private static void ProjectToolCall(
        ParsedSession parsed, Dictionary<string, ToolCallRow> calls, AsfEvent e, DateTime ts,
        string sessionId, string slug, ref int subagentInvocations)
    {
        var tool = e.Tool ?? "unknown";
        var callId = e.CallId is { Length: > 0 } id ? id : e.Id ?? $"seq-{e.Seq}";
        var isSubagent = SubagentTools.Contains(tool);
        if (isSubagent) subagentInvocations++;

        var args = e.Args;
        var argsJson = args is null ? string.Empty : CanonicalJson.Serialize(args);

        calls[callId] = new ToolCallRow
        {
            SessionId = sessionId,
            ProjectSlug = slug,
            TimestampUtc = ts,
            ToolName = tool,
            ToolUseId = callId,
            InputDigest = e.ArgsHash is { Length: >= 16 } h ? h[..16] : ShortHash(argsJson),
            InputPreview = Truncate(argsJson, 200),
            IsMcp = e.IsMcp == true,
            IsSubagent = isSubagent,
            SubagentType = isSubagent ? SourceReadHelpers.Str((args as JsonObject)?["subagent_type"]) : null,
        };

        // ExitPlanMode carries the plan body in its arguments. The hash is taken on
        // the trimmed body so it matches the on-disk plan file hash computed by
        // PlanFileIndexer.
        if (tool == "ExitPlanMode" &&
            SourceReadHelpers.Str((args as JsonObject)?["plan"]) is { Length: > 0 } plan)
        {
            parsed.PlanEvents.Add(new PlanEvent
            {
                SessionId = sessionId,
                ProjectSlug = slug,
                ToolUseId = callId,
                PlanBody = plan,
                PlanHash = ShortHash(plan.Trim()),
                TimestampUtc = ts,
            });
        }
    }

    private static void ProjectToolResult(
        ParsedSession parsed, Dictionary<string, ToolCallRow> calls, AsfEvent e, DateTime ts,
        string sessionId, string slug)
    {
        var callId = e.CallId ?? "";
        if (!calls.Remove(callId, out var row))
        {
            // A result with no call — a transcript whose head was rotated away.
            // Counting the result as a call would be worse than dropping it: the
            // tool is known, but its arguments never existed in this file.
            return;
        }

        var isError = e.IsError == true;
        row.IsError = isError;
        row.ResultSize = e.OutputBytes ?? e.Output?.Length;
        row.DurationMs = e.DurationMs;
        parsed.ToolCalls.Add(row);

        if (isError)
        {
            parsed.Errors.Add(new ErrorRow
            {
                SessionId = sessionId,
                ProjectSlug = slug,
                TimestampUtc = ts,
                Kind = "error",
                ToolName = row.ToolName,
                ToolUseId = callId,
                InputDigest = row.InputDigest,
                Snippet = Truncate(e.Output ?? e.Message, 500),
                DurationMs = e.DurationMs,
            });
        }
    }

    private static void AccumulateUsage(Dictionary<string, ModelTokenTotals> totals, AsfEvent e)
    {
        var model = e.Model;
        if (string.IsNullOrEmpty(model)) return;

        if (!totals.TryGetValue(model, out var bucket))
        {
            bucket = new ModelTokenTotals { Model = model };
            totals[model] = bucket;
        }

        bucket.InputTokens += e.InputTokens ?? 0;
        bucket.OutputTokens += e.OutputTokens ?? 0;
        bucket.CacheReadInputTokens += e.CacheReadTokens ?? 0;
        bucket.CacheCreationInputTokens += e.CacheWriteTokens ?? 0;
    }

    // ── helpers (ported verbatim from the Claude-only parser) ──────────────────

    /// One-shot state linking a manual /compact trigger to the summary that follows.
    private sealed class CompactState
    {
        public bool PendingManual;
        public string? PendingArgs;

        public void Clear()
        {
            PendingManual = false;
            PendingArgs = null;
        }
    }

    private static List<TopName> TopN(Dictionary<string, long> dict, int n) =>
        dict.OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => new TopName { Name = kv.Key, Count = kv.Value })
            .ToList();

    /// Inner text of the first &lt;tag&gt;…&lt;/tag&gt; pair; null if absent or empty.
    private static string? ExtractTag(string text, string tag)
    {
        var open = "<" + tag + ">";
        var close = "</" + tag + ">";
        var i = text.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return null;
        i += open.Length;
        var j = text.IndexOf(close, i, StringComparison.Ordinal);
        if (j < 0) return null;
        var inner = text[i..j].Trim();

        return inner.Length == 0 ? null : inner;
    }

    private static (bool IsSlash, string? Command, string? Args) ParseSlash(string text)
    {
        // Real slash commands look like /foo, /build-banner, /loop, /init — a
        // leading slash followed by a kebab-case-style identifier. This explicitly
        // rejects paths like /workspaces/agentic-live-www/...
        if (!text.StartsWith('/')) return (false, null, null);
        if (text.Length < 2 || !char.IsLetterOrDigit(text[1])) return (false, null, null);

        var endIdx = -1;
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':') continue;
            endIdx = i;
            break;
        }

        if (endIdx == -1) return (true, text[1..], null);

        // The character that terminated the name decides slashness: whitespace = a
        // real command name with following args; anything else (slash, dot) means
        // this is not a slash command.
        var term = text[endIdx];
        if (term is not (' ' or '\t' or '\n')) return (false, null, null);

        var name = text.Substring(1, endIdx - 1);
        var args = text[(endIdx + 1)..].Trim();

        return (true, name, args.Length == 0 ? null : args);
    }

    private static string ShortHash(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 8).ToLowerInvariant();
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
