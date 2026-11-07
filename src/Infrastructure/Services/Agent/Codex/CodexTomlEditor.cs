using System.Text;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// Edits <c>~/.codex/config.toml</c> in place, by name, without parsing it.
///
/// <para>
/// <b>Why not rebuild the file.</b> That file is co-owned. Codex Desktop writes
/// <c>[desktop]</c> into it, <c>codex plugin</c> writes <c>[marketplaces.*]</c> and
/// <c>[plugins.*]</c>, <c>codex mcp add</c> writes <c>[mcp_servers.*]</c>, and the reader writes
/// whatever they like. A prototype that read the file, built a new one from a model and wrote it
/// back deleted <c>[desktop]</c> and every <c>[plugins.*]</c> table; Codex Desktop then silently
/// refused to launch while <c>codex doctor</c> and <c>codex app-server</c> both reported success,
/// and it took a reinstall to find. So: three edits, nothing else touched, byte for byte.
/// </para>
///
/// <para>
/// <b>Why by name and not by marker.</b> <see cref="CodexCliConfig.UpsertManagedBlock"/> delimits
/// its region with comments, and <see cref="CodexCliConfig"/> had to grow
/// <c>FindDisplacedToml</c> to cope: Codex rewrites the TOML structurally and moves the closing
/// marker <i>below</i> tables it appends later, so foreign tables end up inside the managed region
/// and have to be rescued out. A table name cannot be displaced — it is still
/// <c>[mcp_servers.agentics-project]</c> wherever in the file it lands. Ownership therefore lives
/// in a ledger keyed by name (<see cref="CodexProfileLedger"/>) and never in the file's layout.
/// </para>
///
/// <para>
/// Measured against codex-cli 0.163.0. See <c>docs/specs/agent-setup/03-codex-binding.md</c> in
/// the agentic-live-www workspace for the protocol this implements.
/// </para>
/// </summary>
public static class CodexTomlEditor
{
    /// <summary>
    /// Replaces a bare top-level key's value, keeping it where it is, or inserts it at the very
    /// top of the file when absent.
    ///
    /// <para>
    /// <b>The top is not a style choice.</b> In TOML a bare key written after a table header
    /// belongs to that table, so appending <c>model = "x"</c> to the end of a file that contains
    /// any table silently creates <c>[that_table].model</c> instead of a top-level key. Codex then
    /// reads no model and reports nothing.
    /// </para>
    /// </summary>
    public static string SetTopLevelKey(string? content, string key, string tomlValue)
    {
        var text = content ?? string.Empty;
        var line = $"{key} = {tomlValue}";

        var found = FindTopLevelKeyLine(text, key);
        if (found.Start >= 0)
            return text[..found.Start] + line + text[found.ContentEnd..];

        // Insert at the top, after any leading comment header so a file that opens with an
        // explanatory comment keeps reading as one.
        var insertAt = SkipLeadingComments(text);
        var prefix = text[..insertAt];
        var rest = text[insertAt..];
        var separator = rest.Length > 0 && !rest.StartsWith('\n') ? "\n" : string.Empty;

        return prefix + line + "\n" + separator + rest;
    }

    /// <summary>Reads a bare top-level key's raw TOML value, or null when it is not set.</summary>
    public static string? GetTopLevelKey(string? content, string key)
    {
        var text = content ?? string.Empty;
        var found = FindTopLevelKeyLine(text, key);
        if (found.Start < 0) return null;

        var line = text[found.Start..found.ContentEnd];
        var eq = line.IndexOf('=');

        return eq < 0 ? null : line[(eq + 1)..].Trim();
    }

    /// <summary>Removes a bare top-level key entirely, if present.</summary>
    public static string RemoveTopLevelKey(string? content, string key)
    {
        var text = content ?? string.Empty;
        var found = FindTopLevelKeyLine(text, key);

        return found.Start < 0 ? text : text[..found.Start] + text[found.LineEnd..];
    }

    /// <summary>
    /// Removes a whole table by name — its header and every line until the next table header or
    /// end of file. Sub-tables (<c>[name.child]</c>) go with it.
    /// </summary>
    public static string RemoveTable(string? content, string tableName)
    {
        var text = content ?? string.Empty;
        if (text.Length == 0) return text;

        var result = new StringBuilder();
        var dropping = false;

        foreach (var (line, raw) in Lines(text))
        {
            var trimmed = line.Trim();
            if (IsTableHeader(trimmed))
                dropping = TableHeaderMatches(trimmed, tableName);

            if (!dropping) result.Append(raw);
        }

        return result.ToString();
    }

    /// <summary>
    /// Appends a table block at the end of the file, separated by one blank line.
    /// Trailing content is normalised to exactly one newline so repeated applies do not
    /// accumulate blank lines — which is what makes a second apply a byte-for-byte no-op.
    /// </summary>
    public static string AppendTable(string? content, string block)
    {
        var body = block.Trim('\r', '\n');
        if (body.Length == 0) return content ?? string.Empty;

        var text = (content ?? string.Empty).TrimEnd('\r', '\n', ' ', '\t');

        return text.Length == 0 ? body + "\n" : text + "\n\n" + body + "\n";
    }

    /// <summary>True when a table of this name is present.</summary>
    public static bool HasTable(string? content, string tableName)
    {
        foreach (var (line, _) in Lines(content ?? string.Empty))
        {
            var trimmed = line.Trim();
            if (IsTableHeader(trimmed) && TableHeaderMatches(trimmed, tableName)) return true;
        }

        return false;
    }

    /// <summary>
    /// Removes the legacy v1 profile selector — a top-level <c>profile = "…"</c> key and any
    /// <c>[profiles.*]</c> table.
    ///
    /// <para>
    /// <b>This is a repair, not tidying.</b> On codex-cli 0.163.0 either one is a hard error:
    /// <c>legacy `profile = "x"` config is no longer supported</c>. Worse, its presence also makes
    /// <c>-p</c> itself fail, naming the legacy key — so one stale selector costs the reader every
    /// profile on the machine, ours and theirs. A client that writes one has bricked the config; a
    /// client that finds one and leaves it has left the reader with nothing working.
    /// </para>
    /// </summary>
    public static string RemoveLegacyProfileSelector(string? content, out bool removedAnything)
    {
        var text = content ?? string.Empty;
        var before = text;

        text = RemoveTopLevelKey(text, "profile");

        // Remove [profiles] and every [profiles.<name>] table.
        var result = new StringBuilder();
        var dropping = false;
        foreach (var (line, raw) in Lines(text))
        {
            var trimmed = line.Trim();
            if (IsTableHeader(trimmed))
            {
                var name = trimmed[1..^1].Trim();
                dropping = name.Equals("profiles", StringComparison.Ordinal)
                    || name.StartsWith("profiles.", StringComparison.Ordinal);
            }

            if (!dropping) result.Append(raw);
        }

        text = result.ToString();
        removedAnything = !string.Equals(text, before, StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    /// Removes the region written by the older marker-based writer
    /// (<see cref="CodexCliConfig.UpsertManagedBlock"/>), keeping any foreign table Codex has
    /// since moved inside it.
    ///
    /// <para>
    /// A machine that ran <c>pks codex init</c> before this protocol existed has a
    /// <c>[model_providers.pks-foundry]</c> table inside a comment-delimited block and no ledger
    /// entry for it. Left alone it would linger forever, unowned and unremovable by name, and on a
    /// profile that publishes no provider it would keep pointing a dead <c>model_provider</c> at a
    /// loopback port nothing is listening on.
    /// </para>
    /// </summary>
    public static string RemoveLegacyManagedBlock(string? content, out bool removedAnything)
    {
        var text = content ?? string.Empty;
        removedAnything = false;

        var begin = text.IndexOf(CodexCliConfig.BeginMarker, StringComparison.Ordinal);
        if (begin < 0) return text;

        var end = text.IndexOf(CodexCliConfig.EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return text;

        var region = text[(begin + CodexCliConfig.BeginMarker.Length)..end];
        var rescued = RescueForeignTables(region);

        var head = text[..begin].TrimEnd('\r', '\n');
        var tail = text[(end + CodexCliConfig.EndMarker.Length)..].TrimStart('\r', '\n');

        var sb = new StringBuilder();
        if (head.Length > 0) sb.Append(head).Append('\n');
        if (rescued.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(rescued).Append('\n');
        }

        if (tail.Length > 0)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(tail);
        }

        removedAnything = true;

        return sb.ToString();
    }

    /// <summary>
    /// Everything from the first table header in the legacy region that is not the pks provider.
    /// Those tables were appended by Codex after our end marker and are not ours to delete.
    /// </summary>
    private static string RescueForeignTables(string region)
    {
        var lineStart = 0;
        while (lineStart < region.Length)
        {
            var lineEnd = region.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = region.Length;
            var line = region[lineStart..lineEnd].Trim();

            if (IsTableHeader(line) && !IsPksProviderTable(line))
                return region[lineStart..].Trim('\r', '\n', ' ', '\t');

            lineStart = lineEnd + 1;
        }

        return string.Empty;
    }

    private static bool IsPksProviderTable(string line) =>
        line.Equals($"[model_providers.{CodexCliConfig.ProviderName}]", StringComparison.Ordinal) ||
        line.StartsWith($"[model_providers.{CodexCliConfig.ProviderName}.", StringComparison.Ordinal);

    // ---- primitives ----

    /// <summary>
    /// Locates a bare top-level key's line.
    ///
    /// <para>
    /// Three offsets, not two, and the distinction is load-bearing: <c>ContentEnd</c> stops before
    /// the line terminator and <c>LineEnd</c> includes it. Replacing a value splices at
    /// <c>ContentEnd</c> so the original newline survives; removing the key splices at
    /// <c>LineEnd</c> so the newline goes with it. Using one offset for both silently eats a
    /// newline on every write, which makes a re-run join the key to the next line — and that next
    /// line is usually a table header, so the key becomes part of that table.
    /// </para>
    ///
    /// <para>
    /// Only searched before the first table header: after one, a bare key belongs to that table.
    /// </para>
    /// </summary>
    private static (int Start, int ContentEnd, int LineEnd) FindTopLevelKeyLine(string text, string key)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var sliceEnd = lineEnd < 0 ? text.Length : lineEnd;
            var line = text[pos..sliceEnd];
            var trimmed = line.Trim();

            if (IsTableHeader(trimmed)) return (-1, -1, -1);

            if (KeyMatches(trimmed, key))
            {
                var contentEnd = sliceEnd;
                // Keep a CR with its LF: on a CRLF file, splicing before the CR would strip it
                // from that one line and leave the file with mixed terminators.
                if (contentEnd > pos && text[contentEnd - 1] == '\r') contentEnd--;

                return (pos, contentEnd, lineEnd < 0 ? text.Length : lineEnd + 1);
            }

            if (lineEnd < 0) break;
            pos = lineEnd + 1;
        }

        return (-1, -1, -1);
    }

    /// <summary>A key assignment for <paramref name="key"/>, bare or quoted, ignoring comments.</summary>
    private static bool KeyMatches(string trimmedLine, string key)
    {
        if (trimmedLine.Length == 0 || trimmedLine.StartsWith('#')) return false;

        var eq = trimmedLine.IndexOf('=');
        if (eq <= 0) return false;

        var name = trimmedLine[..eq].Trim().Trim('"', '\'');

        return name.Equals(key, StringComparison.Ordinal);
    }

    private static int SkipLeadingComments(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var sliceEnd = lineEnd < 0 ? text.Length : lineEnd;
            var trimmed = text[pos..sliceEnd].Trim();

            if (trimmed.Length > 0 && !trimmed.StartsWith('#')) return pos;
            if (lineEnd < 0) return text.Length;

            pos = lineEnd + 1;
        }

        return pos;
    }

    private static bool IsTableHeader(string trimmedLine) =>
        trimmedLine.Length >= 3 && trimmedLine[0] == '[' && trimmedLine[^1] == ']';

    /// <summary>
    /// Does this header name the table, or one of its children?
    /// Tolerates quoting (<c>["a.b"]</c>) and whitespace, both of which Codex emits.
    /// </summary>
    private static bool TableHeaderMatches(string trimmedHeader, string tableName)
    {
        var inner = trimmedHeader[1..^1].Trim();
        if (inner.StartsWith('[')) return false; // [[array.of.tables]] — not ours

        if (inner.Equals(tableName, StringComparison.Ordinal)) return true;
        if (inner.StartsWith(tableName + ".", StringComparison.Ordinal)) return true;

        // Quoted final segment, e.g. mcp_servers."my-server" for mcp_servers.my-server.
        var unquoted = string.Join('.', inner.Split('.').Select(p => p.Trim().Trim('"', '\'')));

        return unquoted.Equals(tableName, StringComparison.Ordinal)
            || unquoted.StartsWith(tableName + ".", StringComparison.Ordinal);
    }

    /// <summary>Lines with their original terminators, so rewriting preserves CRLF and the final newline.</summary>
    private static IEnumerable<(string Line, string Raw)> Lines(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            if (lineEnd < 0)
            {
                yield return (text[pos..], text[pos..]);
                break;
            }

            var raw = text[pos..(lineEnd + 1)];
            yield return (raw.TrimEnd('\n', '\r'), raw);
            pos = lineEnd + 1;
        }
    }
}
