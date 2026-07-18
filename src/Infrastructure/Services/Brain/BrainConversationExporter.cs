using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Produces a compact, deterministic conversation view of a Claude Code JSONL
/// session. Human text and visible assistant text stay inline; expensive or
/// synthetic blocks become line + byte references back to the immutable source.
/// </summary>
public sealed class BrainConversationExporter : IBrainConversationExporter
{
    public async Task<BrainConversationExportResult> ExportAsync(
        BrainConversationExportOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.SourcePath))
            throw new ArgumentException("SourcePath required", nameof(options));
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("OutputPath required", nameof(options));
        if (options.MaxVisibleCharsPerBlock < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxVisibleCharsPerBlock must be positive.");

        var sourcePath = Path.GetFullPath(options.SourcePath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Session JSONL not found: {sourcePath}", sourcePath);

        var info = new FileInfo(sourcePath);
        var sessionId = Path.GetFileNameWithoutExtension(sourcePath);
        var sha256 = await ComputeSha256Async(sourcePath, ct);
        var body = new StringBuilder();
        var humanMessages = 0;
        var assistantTextBlocks = 0;
        var omittedBlocks = 0;
        long lineCount = 0;
        string? visibleRole = null;

        await foreach (var rawLine in ReadUtf8LinesAsync(sourcePath, ct))
        {
            lineCount = rawLine.LineNumber;
            if (string.IsNullOrWhiteSpace(rawLine.Text)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(rawLine.Text); }
            catch (JsonException) { continue; }
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "type", out var type))
                continue;

            var timestamp = TryString(root, "timestamp", out var ts) ? ts : null;
            if (type == "user")
            {
                if (IsSyntheticUser(root, out var syntheticKind))
                {
                    AppendOmitted(body, syntheticKind, null, rawLine);
                    omittedBlocks++;
                    continue;
                }

                if (!TryContent(root, out var content)) continue;
                if (content.ValueKind == JsonValueKind.String)
                {
                    var text = CleanHumanText(content.GetString());
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    AppendRole(body, ref visibleRole, "User", timestamp);
                    AppendVisible(body, text, options.MaxVisibleCharsPerBlock, rawLine, ref omittedBlocks);
                    humanMessages++;
                    continue;
                }

                if (content.ValueKind != JsonValueKind.Array) continue;
                var foundHumanText = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (!TryString(block, "type", out var blockType)) continue;
                    if (blockType == "text" && TryString(block, "text", out var textValue))
                    {
                        var text = CleanHumanText(textValue);
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        AppendRole(body, ref visibleRole, "User", timestamp);
                        AppendVisible(body, text, options.MaxVisibleCharsPerBlock, rawLine, ref omittedBlocks);
                        foundHumanText = true;
                    }
                    else if (blockType == "tool_result")
                    {
                        TryString(block, "tool_use_id", out var toolUseId);
                        AppendOmitted(body, "tool result", toolUseId, rawLine);
                        omittedBlocks++;
                    }
                }
                if (foundHumanText) humanMessages++;
                continue;
            }

            if (type != "assistant" || !TryContent(root, out var assistantContent)) continue;
            var isIntermediateAssistant = TryMessageString(root, "stop_reason", out var stopReason) &&
                                          !stopReason.Equals("end_turn", StringComparison.Ordinal);
            if (assistantContent.ValueKind == JsonValueKind.String)
            {
                var text = assistantContent.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (isIntermediateAssistant && !options.IncludeIntermediateAssistantText)
                {
                    AppendOmitted(body, "intermediate assistant text", null, rawLine);
                    omittedBlocks++;
                    continue;
                }
                AppendRole(body, ref visibleRole, "Assistant", timestamp);
                AppendVisible(body, text, options.MaxVisibleCharsPerBlock, rawLine, ref omittedBlocks);
                assistantTextBlocks++;
                continue;
            }
            if (assistantContent.ValueKind != JsonValueKind.Array) continue;

            foreach (var block in assistantContent.EnumerateArray())
            {
                if (!TryString(block, "type", out var blockType)) continue;
                if (blockType == "text" && TryString(block, "text", out var textValue))
                {
                    if (string.IsNullOrWhiteSpace(textValue)) continue;
                    if (isIntermediateAssistant && !options.IncludeIntermediateAssistantText)
                    {
                        AppendOmitted(body, "intermediate assistant text", null, rawLine);
                        omittedBlocks++;
                        continue;
                    }
                    AppendRole(body, ref visibleRole, "Assistant", timestamp);
                    AppendVisible(body, textValue, options.MaxVisibleCharsPerBlock, rawLine, ref omittedBlocks);
                    assistantTextBlocks++;
                }
                else if (blockType == "tool_use")
                {
                    TryString(block, "name", out var toolName);
                    AppendOmitted(body, "tool call", toolName, rawLine);
                    omittedBlocks++;
                }
                else if (blockType is "thinking" or "redacted_thinking")
                {
                    AppendOmitted(body, "thinking block", null, rawLine);
                    omittedBlocks++;
                }
            }
        }

        var markdown = new StringBuilder();
        markdown.AppendLine("---");
        markdown.AppendLine("format: pks-brain-conversation-v1");
        markdown.AppendLine($"session_id: {JsonSerializer.Serialize(sessionId)}");
        markdown.AppendLine($"source_path: {JsonSerializer.Serialize(sourcePath)}");
        markdown.AppendLine($"source_sha256: {sha256}");
        markdown.AppendLine($"source_bytes: {info.Length}");
        markdown.AppendLine($"source_lines: {lineCount}");
        markdown.AppendLine("byte_ranges: start-inclusive-end-exclusive");
        markdown.AppendLine("---");
        markdown.AppendLine();
        markdown.AppendLine("# Conversation");
        markdown.AppendLine();
        markdown.Append(body);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, markdown.ToString(), new UTF8Encoding(false), ct);

        return new BrainConversationExportResult
        {
            SessionId = sessionId,
            SourcePath = sourcePath,
            OutputPath = outputPath,
            SourceSha256 = sha256,
            SourceBytes = info.Length,
            SourceLines = lineCount,
            HumanMessages = humanMessages,
            AssistantTextBlocks = assistantTextBlocks,
            OmittedBlocks = omittedBlocks,
        };
    }

    private static bool IsSyntheticUser(JsonElement root, out string kind)
    {
        kind = "synthetic event";
        if (TryBool(root, "isMeta") || TryBool(root, "isSidechain")) return true;
        if (TryBool(root, "isCompactSummary"))
        {
            kind = "compact summary";
            return true;
        }
        if (root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object &&
            TryString(origin, "kind", out var originKind) && !string.IsNullOrWhiteSpace(originKind))
        {
            if (!originKind.Equals("human", StringComparison.OrdinalIgnoreCase))
            {
                kind = "synthetic event";
                return true;
            }
        }
        if (!TryContent(root, out var content) || content.ValueKind != JsonValueKind.String) return false;
        var text = content.GetString() ?? "";
        return text.StartsWith("<local-command-caveat>", StringComparison.Ordinal) ||
               text.StartsWith("<command-name>", StringComparison.Ordinal) ||
               text.StartsWith("<task-notification>", StringComparison.Ordinal) ||
               text.StartsWith("Another Claude session sent a message:", StringComparison.Ordinal) ||
               text.Contains("<local-command-stdout>", StringComparison.Ordinal);
    }

    private static string CleanHumanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal)) return "";
        var reminder = text.IndexOf("\n<system-reminder>", StringComparison.Ordinal);
        if (reminder >= 0) text = text[..reminder].TrimEnd();
        if (text.StartsWith("<system-reminder>", StringComparison.Ordinal)) return "";
        return text;
    }

    private static void AppendRole(StringBuilder output, ref string? visibleRole, string role, string? timestamp)
    {
        if (visibleRole == role) return;
        if (output.Length > 0 && output[^1] != '\n') output.AppendLine();
        output.Append("## ").Append(role);
        if (!string.IsNullOrWhiteSpace(timestamp)) output.Append(" · ").Append(timestamp);
        output.AppendLine().AppendLine();
        visibleRole = role;
    }

    private static void AppendVisible(
        StringBuilder output,
        string text,
        int maxChars,
        RawLine raw,
        ref int omittedBlocks)
    {
        var visible = text.Length <= maxChars ? text : text[..maxChars];
        output.AppendLine(visible.TrimEnd());
        output.Append("<!-- ").Append(Reference(raw)).AppendLine(" -->").AppendLine();
        if (text.Length <= maxChars) return;
        output.Append("> Omitted ").Append(text.Length - maxChars)
            .Append(" trailing characters — ").Append(Reference(raw)).AppendLine().AppendLine();
        omittedBlocks++;
    }

    private static void AppendOmitted(StringBuilder output, string kind, string? detail, RawLine raw)
    {
        output.Append("> Omitted ").Append(kind);
        if (!string.IsNullOrWhiteSpace(detail)) output.Append(" `").Append(detail).Append('`');
        output.Append(" — ").Append(Reference(raw)).AppendLine().AppendLine();
    }

    private static string Reference(RawLine raw) =>
        $"raw L{raw.LineNumber}, bytes {raw.ByteStart}-{raw.ByteEnd}";

    private static bool TryContent(JsonElement root, out JsonElement content)
    {
        content = default;
        return root.TryGetProperty("message", out var message) &&
               message.ValueKind == JsonValueKind.Object &&
               message.TryGetProperty("content", out content);
    }

    private static bool TryMessageString(JsonElement root, string property, out string value)
    {
        value = "";
        return root.TryGetProperty("message", out var message) &&
               message.ValueKind == JsonValueKind.Object &&
               TryString(message, property, out value);
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        return element.TryGetProperty(property, out var item) &&
               item.ValueKind == JsonValueKind.String &&
               (value = item.GetString() ?? "") is not null;
    }

    private static bool TryBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.True;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }

    private static async IAsyncEnumerable<RawLine> ReadUtf8LinesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        long absolute = 0;
        long lineStart = 0;
        long lineNumber = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                absolute++;
                if (b != (byte)'\n')
                {
                    line.WriteByte(b);
                    continue;
                }
                lineNumber++;
                yield return Decode(line, lineNumber, lineStart, absolute);
                line.SetLength(0);
                lineStart = absolute;
            }
        }
        if (line.Length > 0)
        {
            lineNumber++;
            yield return Decode(line, lineNumber, lineStart, absolute);
        }
    }

    private static RawLine Decode(MemoryStream line, long number, long start, long end)
    {
        var bytes = line.ToArray();
        var length = bytes.Length > 0 && bytes[^1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
        var text = Encoding.UTF8.GetString(bytes, 0, length);
        if (number == 1) text = text.TrimStart('\uFEFF');
        return new RawLine(text, number, start, end);
    }

    private sealed record RawLine(string Text, long LineNumber, long ByteStart, long ByteEnd);
}
