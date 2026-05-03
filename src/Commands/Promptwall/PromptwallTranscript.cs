using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PKS.Commands.Promptwall;

/// <summary>
/// Transcript-parsing helpers for the <c>pks promptwall</c> command.
/// Pure, side-effect-free operations on Claude Code session JSONL strings,
/// kept separate from the command shell so they can be unit-tested without
/// touching the file system or Spectre.Console.
/// </summary>
public static class PromptwallTranscript
{
    // Hard caps from design doc §5.3.
    public const int IndividualCapChars = 1200;
    public const int IndividualHighWaterChars = 400;

    private static readonly Regex SystemReminderTrailing =
        new(@"\s*<system-reminder>[\s\S]*?</system-reminder>\s*$", RegexOptions.Compiled);

    private static readonly Regex SystemReminderAnywhere =
        new(@"<system-reminder>[\s\S]*?</system-reminder>", RegexOptions.Compiled);

    private static readonly Regex CommandWrappers =
        new(@"^(?:<command-(?:name|message|args)>[\s\S]*?</command-(?:name|message|args)>)+",
            RegexOptions.Compiled);

    private static readonly Regex MultipleBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    public sealed record PromptCandidate(
        string File,
        string Uuid,
        DateTime Timestamp,
        string Text);

    public sealed record ImagePromptSpec(
        string Label,        // "PROMPT" or "REPLY"
        string SourceText,   // the cleaned prompt or reply text
        string ImagePrompt); // the full instruction sent to the image model

    /// <summary>
    /// Parse a JSONL string and return the user prompts in reverse-chronological
    /// order, after applying the §2.2 filters and §2.3 cleaning rules.
    /// </summary>
    public static List<PromptCandidate> ParsePrompts(string jsonl, string file)
    {
        var result = new List<PromptCandidate>();

        foreach (var line in jsonl.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (!TryReadPromptLine(root, out var uuid, out var ts, out var raw))
                continue;

            var cleaned = CleanPromptText(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) continue;

            result.Add(new PromptCandidate(file, uuid, ts, cleaned));
        }

        result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return result;
    }

    /// <summary>
    /// Walk the JSONL forward from the user line whose <paramref name="promptUuid"/>
    /// matches, accumulate the assistant's text-block replies, and stop at either
    /// the next real user prompt or an assistant message with stop_reason=end_turn.
    /// Returns null if there are no text blocks before the boundary.
    /// </summary>
    public static string? ExtractReply(string jsonl, string promptUuid)
    {
        var lines = jsonl.Split('\n');
        bool found = false;
        var collected = new List<string>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (!root.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            if (!found)
            {
                if (type == "user" &&
                    root.TryGetProperty("uuid", out var uuidProp) &&
                    uuidProp.GetString() == promptUuid)
                {
                    found = true;
                }
                continue;
            }

            // Past the picked prompt — look for the boundary.
            if (type == "user" && IsRealUserPromptLine(root))
                break;

            if (type != "assistant") continue;

            var text = ExtractAssistantText(root);
            if (!string.IsNullOrEmpty(text))
                collected.Add(text);

            // Stop at end_turn — that's the conclusion of this turn.
            if (TryGetStopReason(root) == "end_turn")
                break;
        }

        if (collected.Count == 0) return null;
        return string.Join("\n\n", collected);
    }

    /// <summary>
    /// Build the image-generation prompt(s) for the picked text. Returns one
    /// spec when the prompt-only / combined-short case applies, otherwise two
    /// specs (prompt card + reply card) per design §5.3.
    /// </summary>
    public static List<ImagePromptSpec> BuildImagePrompts(
        string promptText,
        string? replyText,
        int combinedThreshold = 600)
    {
        var prompt = TruncateAtWordBoundary(promptText, IndividualCapChars);

        if (string.IsNullOrEmpty(replyText))
            return [new ImagePromptSpec("PROMPT", prompt, RenderTemplate("PROMPT", "cyanToViolet", prompt))];

        var reply = TruncateAtWordBoundary(replyText, IndividualCapChars);

        bool eitherTooLong =
            prompt.Length > IndividualHighWaterChars ||
            reply.Length > IndividualHighWaterChars;
        bool combinedTooLong = prompt.Length + reply.Length > combinedThreshold;

        if (eitherTooLong || combinedTooLong)
        {
            return [
                new ImagePromptSpec("PROMPT", prompt, RenderTemplate("PROMPT", "cyanToViolet", prompt)),
                new ImagePromptSpec("REPLY",  reply,  RenderTemplate("REPLY",  "violetToCyan", reply)),
            ];
        }

        // Single image with both prompt and reply rendered.
        var combined = $"PROMPT:\n{prompt}\n\nREPLY:\n{reply}";
        return [new ImagePromptSpec("PROMPT", combined, RenderTemplate("PROMPT", "cyanToViolet", combined))];
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool TryReadPromptLine(
        JsonElement root,
        out string uuid,
        out DateTime timestamp,
        out string text)
    {
        uuid = "";
        timestamp = default;
        text = "";

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "user")
            return false;

        if (GetBool(root, "isSidechain")) return false;
        if (GetBool(root, "isMeta")) return false;

        // Synthetic prompt: any non-null origin.kind means it was injected
        // (e.g. task-notification from a Monitor / sub-agent), not user-typed.
        if (root.TryGetProperty("origin", out var originProp) &&
            originProp.ValueKind == JsonValueKind.Object &&
            originProp.TryGetProperty("kind", out var kindProp) &&
            kindProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(kindProp.GetString()))
            return false;

        // Synthetic prompt: /compact continuation summary. Claude Code marks these
        // with isCompactSummary=true; the parent record is type=system,
        // subtype=compact_boundary.
        if (GetBool(root, "isCompactSummary")) return false;

        if (!root.TryGetProperty("message", out var msg)) return false;
        if (!msg.TryGetProperty("content", out var content)) return false;

        // content must be a JSON string — array form means tool_result blocks.
        if (content.ValueKind != JsonValueKind.String) return false;

        if (!root.TryGetProperty("uuid", out var uuidProp)) return false;
        if (!root.TryGetProperty("timestamp", out var tsProp)) return false;
        if (!DateTime.TryParse(tsProp.GetString(), out var ts)) return false;

        var raw = content.GetString() ?? "";
        if (string.IsNullOrEmpty(raw)) return false;

        // Slash-command echoes — these are not user-typed prompts.
        if (raw.StartsWith("<command-name>", StringComparison.Ordinal)) return false;
        if (raw.Contains("<local-command-stdout>", StringComparison.Ordinal)) return false;

        uuid = uuidProp.GetString() ?? "";
        timestamp = ts;
        text = raw;
        return true;
    }

    private static bool IsRealUserPromptLine(JsonElement root)
    {
        return TryReadPromptLine(root, out _, out _, out var raw)
               && !string.IsNullOrWhiteSpace(CleanPromptText(raw));
    }

    private static string CleanPromptText(string raw)
    {
        var t = raw;

        // Drop trailing harness-appended <system-reminder> blocks.
        t = SystemReminderTrailing.Replace(t, "");

        // If the whole thing was a system-reminder, drop it entirely.
        if (string.IsNullOrWhiteSpace(SystemReminderAnywhere.Replace(t, "").Trim()))
            return "";

        // Strip leading <command-message>/<command-args> envelopes.
        t = CommandWrappers.Replace(t, "");

        // Collapse 3+ blank lines.
        t = MultipleBlankLines.Replace(t, "\n\n");

        return t.Trim();
    }

    private static string? ExtractAssistantText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;

        // content can be a string or an array of typed blocks.
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var bt)) continue;
            if (bt.GetString() != "text") continue;
            if (!block.TryGetProperty("text", out var tx)) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(tx.GetString());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? TryGetStopReason(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("stop_reason", out var sr)) return null;
        return sr.GetString();
    }

    private static bool GetBool(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var p)
               && p.ValueKind == JsonValueKind.True;
    }

    private static string TruncateAtWordBoundary(string s, int cap)
    {
        if (s.Length <= cap) return s;
        var slice = s[..cap];
        var lastSpace = slice.LastIndexOfAny([' ', '\n', '\t']);
        if (lastSpace > cap / 2) slice = slice[..lastSpace];
        return slice.TrimEnd() + "…";
    }

    private static string RenderTemplate(string label, string gradient, string text)
    {
        // Per design doc §5.2 — a single fixed PKS visual identity.
        // Gradient direction is the only knob (cyanToViolet for prompt,
        // violetToCyan for reply) so a 2-card set reads as a pair.
        var (from, to) = gradient == "violetToCyan"
            ? ("#7C3AED", "#06B6D4")
            : ("#06B6D4", "#7C3AED");

        return $"""
            A clean, modern social-media card on a soft diagonal gradient background
            ({from} → {to}, low contrast).

            Centered, render the following text verbatim in a crisp monospace font
            (JetBrains Mono or Fira Code style), pure white #FFFFFF with a subtle 4px
            drop shadow at 30% opacity. Wrap lines naturally; preserve indentation
            and code-style punctuation. Generous 12% padding on all sides.

            Above the text, a small uppercase label "{label}" in 60% opacity white,
            letter-spaced. Below the text, a thin 1px white divider at 30% opacity.
            In the bottom-right corner, a small wordmark "pks promptwall" in 40%
            opacity white.

            The text to render is, exactly and without paraphrasing:

            «««
            {text}
            »»»

            1200x1200 px, 1:1 square, suitable for X/Twitter and LinkedIn. No other
            elements, no decorative shapes, no people, no logos other than the
            wordmark.
            """;
    }
}
