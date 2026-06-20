using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class SessionParser : ISessionParser
{
    private static readonly HashSet<string> FileOpTools = new(StringComparer.Ordinal)
    {
        "Read", "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    public async Task<ParsedSession> ParseAsync(string filePath, string projectSlug, CancellationToken ct = default)
    {
        var info = new FileInfo(filePath);
        var sessionId = Path.GetFileNameWithoutExtension(filePath);

        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            ProjectSlug = projectSlug,
            SourcePath = filePath,
            SourceMtimeUtc = info.Exists ? info.LastWriteTimeUtc : default,
            SourceBytes = info.Exists ? info.Length : 0,
        };

        var parsed = new ParsedSession { Metadata = metadata };
        if (!info.Exists || info.Length == 0)
        {
            return parsed;
        }

        var cwdCounter = new Dictionary<string, int>(StringComparer.Ordinal);
        var branches = new SortedSet<string>(StringComparer.Ordinal);
        var modelOrder = new List<string>();
        var modelSet = new HashSet<string>(StringComparer.Ordinal);
        var tokenTotals = new Dictionary<string, ModelTokenTotals>(StringComparer.Ordinal);
        var pending = new Dictionary<string, PendingTool>(StringComparer.Ordinal);
        var compact = new CompactState();

        DateTime? minTs = null, maxTs = null;
        string? claudeSessionId = null;
        long lineCount = 0;
        int assistantTurns = 0;
        int thinkingBlocks = 0;
        int subagentInvocations = 0;
        int interruptions = 0;

        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? raw;
        while ((raw = await reader.ReadLineAsync(ct)) is not null)
        {
            lineCount++;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch (JsonException) { continue; }

            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!TryString(root, "type", out var type)) continue;

            var ts = TryDate(root, "timestamp");
            if (ts is { } t)
            {
                if (minTs is null || t < minTs) minTs = t;
                if (maxTs is null || t > maxTs) maxTs = t;
            }

            if (TryString(root, "sessionId", out var sid) && claudeSessionId is null)
                claudeSessionId = sid;
            if (TryString(root, "cwd", out var cwd))
                cwdCounter[cwd] = cwdCounter.GetValueOrDefault(cwd) + 1;
            if (TryString(root, "gitBranch", out var gb))
                branches.Add(gb);

            switch (type)
            {
                case "user":
                    HandleUser(root, ts, sessionId, projectSlug, parsed, pending, compact, ref interruptions);
                    break;
                case "assistant":
                    assistantTurns++;
                    HandleAssistant(root, ts, sessionId, projectSlug, parsed, pending,
                        modelOrder, modelSet, tokenTotals,
                        ref thinkingBlocks, ref subagentInvocations);
                    break;
            }
        }

        // Drain pending tool_uses that never received a tool_result.
        foreach (var p in pending.Values)
        {
            parsed.ToolCalls.Add(p.Row);
            if (p.FileOp is not null)
                parsed.FileOps.Add(p.FileOp);
        }

        metadata.LineCount = lineCount;
        metadata.ClaudeSessionId = claudeSessionId;
        metadata.FirstTimestampUtc = minTs;
        metadata.LastTimestampUtc = maxTs;
        metadata.Cwd = cwdCounter.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        metadata.GitBranches = branches.ToList();
        metadata.Models = modelOrder;
        metadata.TokensByModel = tokenTotals.Values.OrderBy(m => m.Model, StringComparer.Ordinal).ToList();
        metadata.PromptCount = parsed.Prompts.Count;
        metadata.AssistantTurnCount = assistantTurns;
        metadata.ToolCallCount = parsed.ToolCalls.Count;
        metadata.ToolErrorCount = parsed.Errors.Count(e => e.Kind == "error");
        metadata.ThinkingBlockCount = thinkingBlocks;
        metadata.FileOpCount = parsed.FileOps.Count;
        metadata.SubagentInvocationCount = subagentInvocations;
        metadata.PlanEventCount = parsed.PlanEvents.Count;
        metadata.InterruptionCount = interruptions;
        metadata.TopTools = TopN(parsed.ToolCalls.GroupBy(t => t.ToolName).ToDictionary(g => g.Key, g => (long)g.Count()), 50);
        metadata.TopFiles = TopN(parsed.FileOps.GroupBy(f => f.FilePath).ToDictionary(g => g.Key, g => (long)g.Count()), 50);
        metadata.TopErrors = TopN(parsed.Errors.GroupBy(e => $"{e.ToolName ?? "?"}::{e.InputDigest ?? "?"}").ToDictionary(g => g.Key, g => (long)g.Count()), 20);
        metadata.Skills = parsed.Prompts.Where(p => p.IsSlash && p.SlashCommand is { Length: > 0 })
            .Select(p => p.SlashCommand!).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();
        metadata.Subagents = parsed.ToolCalls.Where(t => t.IsSubagent && t.SubagentType is { Length: > 0 })
            .Select(t => t.SubagentType!).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();

        return parsed;
    }

    private static List<TopName> TopN(Dictionary<string, long> dict, int n) =>
        dict.OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => new TopName { Name = kv.Key, Count = kv.Value })
            .ToList();

    // ── user-message handler ──────────────────────────────────────────────────

    private static void HandleUser(
        JsonElement root, DateTime? ts, string sessionId, string projectSlug,
        ParsedSession parsed, Dictionary<string, PendingTool> pending,
        CompactState compact, ref int interruptions)
    {
        var isMeta = TryBool(root, "isMeta", out var meta) && meta;
        if (!TryGetMessage(root, out var msg)) return;
        if (!msg.TryGetProperty("content", out var content)) return;

        // Real-world divergence from sync-parser.ts:125 — Claude Code emits string-content
        // user messages for plan-resume and other harness-injected prompts. Those ARE
        // real prompts (no tool_results possible in a string).
        if (content.ValueKind == JsonValueKind.String)
        {
            if (isMeta) return;
            var s = content.GetString();
            if (string.IsNullOrEmpty(s)) return;
            if (s.StartsWith("[Request interrupted", StringComparison.Ordinal))
            {
                interruptions++;
                return;
            }
            EmitPrompt(root, ts, sessionId, projectSlug, parsed, s, compact);
            return;
        }

        if (content.ValueKind != JsonValueKind.Array) return;

        // 1) close any tool_results for prior tool_uses
        foreach (var block in content.EnumerateArray())
        {
            if (!TryString(block, "type", out var bt)) continue;
            if (bt != "tool_result") continue;
            if (!TryString(block, "tool_use_id", out var toolUseId)) continue;
            if (!pending.Remove(toolUseId, out var entry)) continue;

            var isError = TryBool(block, "is_error", out var err) && err;
            var snippet = ExtractToolResultSnippet(block);
            var resultLen = snippet?.Length;
            entry.Row.IsError = isError;
            entry.Row.ResultSize = resultLen;
            if (ts is { } tEnd && entry.StartedUtc is { } tStart)
                entry.Row.DurationMs = (long)Math.Max(0, (tEnd - tStart).TotalMilliseconds);

            parsed.ToolCalls.Add(entry.Row);
            if (entry.FileOp is not null)
            {
                entry.FileOp.Success = !isError;
                parsed.FileOps.Add(entry.FileOp);
            }
            if (isError)
            {
                parsed.Errors.Add(new ErrorRow
                {
                    SessionId = sessionId,
                    ProjectSlug = projectSlug,
                    TimestampUtc = ts ?? entry.StartedUtc ?? DateTime.MinValue,
                    Kind = "error",
                    ToolName = entry.Row.ToolName,
                    ToolUseId = toolUseId,
                    InputDigest = entry.Row.InputDigest,
                    Snippet = Truncate(snippet, 500),
                    DurationMs = entry.Row.DurationMs,
                });
            }
        }

        // 2) extract real user prompt text — sync-parser.ts:146-154
        if (isMeta) return;
        var promptText = ExtractTextFromContent(content);
        if (string.IsNullOrEmpty(promptText)) return;
        if (promptText.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            interruptions++;
            return;
        }
        EmitPrompt(root, ts, sessionId, projectSlug, parsed, promptText, compact);
    }

    private static void EmitPrompt(
        JsonElement root, DateTime? ts, string sessionId, string projectSlug,
        ParsedSession parsed, string text, CompactState compact)
    {
        var (isSlash, slashCmd, slashArgs) = ParseSlash(text);

        // Context-compaction tagging. Two entries are involved and they arrive in order:
        //   1. the manual trigger  → a user message whose text contains <command-name>/compact</command-name>
        //   2. the summary itself   → a user message with isCompactSummary=true ("This session is being continued…")
        // Auto-compactions produce only (2). We carry a one-shot "pending manual" flag from
        // (1) so the next summary is labelled "manual" with its <command-args> steering text.
        // Caveat: a manual /compact whose continuation lands in a NEW session file can't be
        // linked here and will read as "auto" — best-effort, documented in the analysis notes.
        var isCompactSummary = TryBool(root, "isCompactSummary", out var ics) && ics;
        string? compactTrigger = null, compactInstructions = null;
        if (isCompactSummary)
        {
            compactTrigger = compact.PendingManual ? "manual" : "auto";
            compactInstructions = compact.PendingManual ? compact.PendingArgs : null;
            compact.Clear();
        }
        else if (text.Contains("<command-name>/compact</command-name>", StringComparison.Ordinal))
        {
            compact.PendingManual = true;
            compact.PendingArgs = ExtractTag(text, "command-args");
        }
        else
        {
            // Any unrelated prompt between trigger and summary consumes the flag,
            // so a stray /compact can never mislabel a later auto-compaction.
            compact.Clear();
        }

        TryString(root, "promptId", out var promptId);
        TryString(root, "uuid", out var uuid);
        TryString(root, "cwd", out var cwd);
        TryString(root, "gitBranch", out var gb);
        parsed.Prompts.Add(new PromptRow
        {
            SessionId = sessionId,
            ProjectSlug = projectSlug,
            TimestampUtc = ts ?? DateTime.MinValue,
            PromptId = promptId,
            Uuid = uuid ?? string.Empty,
            Text = text,
            TextHash = ShortHash(text),
            Cwd = cwd,
            GitBranch = gb,
            Length = text.Length,
            IsSlash = isSlash,
            SlashCommand = slashCmd,
            SlashArgs = slashArgs,
            IsCompactSummary = isCompactSummary ? true : null,
            CompactTrigger = compactTrigger,
            CompactInstructions = compactInstructions,
        });
    }

    /// One-shot state linking a manual /compact trigger to the summary that follows it.
    private sealed class CompactState
    {
        public bool PendingManual;
        public string? PendingArgs;
        public void Clear() { PendingManual = false; PendingArgs = null; }
    }

    /// Extracts the inner text of the first &lt;tag&gt;…&lt;/tag&gt; pair; returns null if absent or empty.
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

    // ── assistant-message handler ─────────────────────────────────────────────

    private static void HandleAssistant(
        JsonElement root, DateTime? ts, string sessionId, string projectSlug,
        ParsedSession parsed, Dictionary<string, PendingTool> pending,
        List<string> modelOrder, HashSet<string> modelSet,
        Dictionary<string, ModelTokenTotals> tokenTotals,
        ref int thinkingBlocks, ref int subagentInvocations)
    {
        if (!TryGetMessage(root, out var msg)) return;
        TryString(root, "uuid", out var parentUuid);

        // Track model + token usage
        var model = TryString(msg, "model", out var m) ? m : null;
        if (!string.IsNullOrEmpty(model) && modelSet.Add(model)) modelOrder.Add(model);
        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(model))
        {
            if (!tokenTotals.TryGetValue(model, out var bucket))
            {
                bucket = new ModelTokenTotals { Model = model };
                tokenTotals[model] = bucket;
            }
            bucket.InputTokens += TryLong(usage, "input_tokens");
            bucket.OutputTokens += TryLong(usage, "output_tokens");
            bucket.CacheReadInputTokens += TryLong(usage, "cache_read_input_tokens");
            bucket.CacheCreationInputTokens += TryLong(usage, "cache_creation_input_tokens");
        }

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
        {
            if (!TryString(block, "type", out var bt)) continue;
            switch (bt)
            {
                case "thinking":
                    thinkingBlocks++;
                    break;
                case "tool_use":
                    HandleToolUse(block, ts, sessionId, projectSlug, parentUuid, parsed, pending, ref subagentInvocations);
                    break;
            }
        }
    }

    private static void HandleToolUse(
        JsonElement block, DateTime? ts, string sessionId, string projectSlug,
        string? parentUuid, ParsedSession parsed,
        Dictionary<string, PendingTool> pending, ref int subagentInvocations)
    {
        if (!TryString(block, "id", out var toolUseId)) return;
        if (!TryString(block, "name", out var toolName)) return;
        block.TryGetProperty("input", out var input);

        var (digest, preview) = DigestInput(input);
        var isMcp = toolName.StartsWith("mcp__", StringComparison.Ordinal);
        var isSubagent = toolName == "Agent";
        string? subagentType = isSubagent && input.ValueKind == JsonValueKind.Object &&
            TryString(input, "subagent_type", out var st) ? st : null;
        if (isSubagent) subagentInvocations++;

        var row = new ToolCallRow
        {
            SessionId = sessionId,
            ProjectSlug = projectSlug,
            TimestampUtc = ts ?? DateTime.MinValue,
            ToolName = toolName,
            ToolUseId = toolUseId,
            InputDigest = digest,
            InputPreview = preview,
            ParentAssistantUuid = parentUuid,
            IsMcp = isMcp,
            IsSubagent = isSubagent,
            SubagentType = subagentType,
        };

        // File-op detection (Read/Edit/Write/MultiEdit → file_path; NotebookEdit → notebook_path or file_path)
        FileOpRow? fileOp = null;
        if (FileOpTools.Contains(toolName))
        {
            var op = toolName.ToLowerInvariant();
            string? filePath = null;
            if (input.ValueKind == JsonValueKind.Object)
            {
                if (TryString(input, "file_path", out var fp)) filePath = fp;
                else if (TryString(input, "notebook_path", out var np)) filePath = np;
            }
            if (filePath is not null)
            {
                fileOp = new FileOpRow
                {
                    SessionId = sessionId,
                    ProjectSlug = projectSlug,
                    TimestampUtc = ts ?? DateTime.MinValue,
                    Op = op,
                    FilePath = filePath,
                    Success = true,
                };
            }
        }

        // ExitPlanMode — capture plan body when present. Hash on the trimmed body
        // so it matches the on-disk plan file hash computed by PlanFileIndexer.
        if (toolName == "ExitPlanMode" && input.ValueKind == JsonValueKind.Object &&
            TryString(input, "plan", out var plan))
        {
            parsed.PlanEvents.Add(new PlanEvent
            {
                SessionId = sessionId,
                ProjectSlug = projectSlug,
                ToolUseId = toolUseId,
                PlanBody = plan,
                PlanHash = ShortHash(plan.Trim()),
                TimestampUtc = ts ?? DateTime.MinValue,
            });
        }

        pending[toolUseId] = new PendingTool(row, fileOp, ts);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private record PendingTool(ToolCallRow Row, FileOpRow? FileOp, DateTime? StartedUtc);

    private static bool TryGetMessage(JsonElement root, out JsonElement msg)
    {
        if (root.TryGetProperty("message", out msg) && msg.ValueKind == JsonValueKind.Object) return true;
        msg = default;
        return false;
    }

    private static string ExtractTextFromContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        var sb = new StringBuilder();
        var first = true;
        foreach (var block in content.EnumerateArray())
        {
            if (!TryString(block, "type", out var bt) || bt != "text") continue;
            if (!TryString(block, "text", out var text)) continue;
            if (!first) sb.Append('\n');
            sb.Append(text);
            first = false;
        }
        return sb.ToString();
    }

    private static string? ExtractToolResultSnippet(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Array) return ExtractTextFromContent(content);
        return null;
    }

    private static (bool isSlash, string? cmd, string? args) ParseSlash(string text)
    {
        // Real slash commands look like /foo, /build-banner, /loop, /init —
        // a leading slash followed by a kebab-case-style identifier.
        // This explicitly rejects paths like /workspaces/agentic-live-www/...
        if (!text.StartsWith('/')) return (false, null, null);
        if (text.Length < 2 || !char.IsLetterOrDigit(text[1])) return (false, null, null);

        var endIdx = -1;
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ':')
                continue;
            endIdx = i;
            break;
        }

        if (endIdx == -1)
        {
            return (true, text[1..], null);
        }

        // The character that terminated the name decides slashness:
        // whitespace = a real command name with following args; anything else
        // (slash, dot, etc.) means this is NOT a slash command.
        var term = text[endIdx];
        if (term is not (' ' or '\t' or '\n')) return (false, null, null);

        var name = text.Substring(1, endIdx - 1);
        var args = text[(endIdx + 1)..].Trim();
        return (true, name, args.Length == 0 ? null : args);
    }

    private static (string digest, string preview) DigestInput(JsonElement input)
    {
        var json = input.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : input.GetRawText();
        var preview = Truncate(json, 200) ?? string.Empty;
        return (ShortHash(json), preview);
    }

    private static string ShortHash(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string? Truncate(string? s, int max)
    {
        if (s is null) return null;
        return s.Length <= max ? s : s[..max];
    }

    private static bool TryString(JsonElement el, string prop, out string value)
    {
        value = "";
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind != JsonValueKind.String) return false;
        var s = p.GetString();
        if (string.IsNullOrEmpty(s)) return false;
        value = s;
        return true;
    }

    private static bool TryBool(JsonElement el, string prop, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (p.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }

    private static long TryLong(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(prop, out var p)) return 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;
    }

    private static DateTime? TryDate(JsonElement el, string prop)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(p.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt) ? dt : null;
    }
}
