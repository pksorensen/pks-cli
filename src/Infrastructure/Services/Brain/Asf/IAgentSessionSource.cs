using System.Runtime.CompilerServices;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// One coding tool's session storage, normalized to an ASF event stream.
///
/// Replaces the Claude-only <see cref="ISessionDiscoveryService"/> as the entry
/// point for ingest and export: the pipeline asks every registered source what it
/// has, and each yields the same <see cref="AsfEvent"/> shape regardless of
/// whether the underlying store is Claude's transcript JSONL, Codex's rollouts or
/// opencode's SQLite database.
///
/// Spec: docs/specs/asf/01-event-envelope.md (per-source mapping tables).
public interface IAgentSessionSource
{
    /// One of <see cref="AsfSource"/>.
    string Kind { get; }

    /// False when the tool is not installed on this machine. Not an error —
    /// most machines run a subset of the three.
    bool IsAvailable { get; }

    /// Human-readable location, for `pks brain sources`.
    string Location { get; }

    IEnumerable<DiscoveredAgentSession> Discover(string? projectFilter = null);

    /// Streams the session as ASF events in source order. Implementations must be
    /// deterministic: same bytes in, same events (and therefore same ids) out.
    IAsyncEnumerable<AsfEvent> ReadAsync(
        DiscoveredAgentSession session,
        ISecretMasker masker,
        CancellationToken ct = default);

    /// Writes the session's raw, unparsed form for the blob backup, and returns
    /// the bytes written. Null means "this session has no raw form worth keeping".
    ///
    /// This is the escape hatch from our own parser: ASF is lossy by design, and
    /// when a future version learns to read something we discard today, the blob
    /// is what makes re-deriving it possible. File-backed sources copy the file;
    /// opencode synthesizes a per-session dump, because its rows live in a shared
    /// database that would otherwise be copied once per session.
    ///
    /// Spec: docs/specs/asf/05-blob-backup.md.
    long? WriteRawBackup(DiscoveredAgentSession session, Stream destination);
}

/// A session found on disk, before parsing.
/// <param name="SourceKind">claude | codex | opencode.</param>
/// <param name="NativeSessionId">The tool's own session id, never uploaded.</param>
/// <param name="ProjectSlug">Display grouping — Claude's encoded slug, or the directory leaf.</param>
/// <param name="ProjectRoot">Absolute project directory, hashed into the ASF `project` handle.</param>
/// <param name="SourcePath">
/// File to read, or `&lt;db&gt;#&lt;sessionId&gt;` for opencode where many sessions share one file.
/// </param>
public sealed record DiscoveredAgentSession(
    string SourceKind,
    string NativeSessionId,
    string ProjectSlug,
    string ProjectRoot,
    string SourcePath,
    DateTime LastModifiedUtc,
    long Bytes)
{
    /// Cursor key: unique across sources, stable across runs.
    public string CursorKey => $"{SourceKind}:{NativeSessionId}";

    /// The file to archive as a raw blob. Null when the session has no single
    /// backing file (opencode — its per-session dump is synthesized instead).
    public string? BackingFile => SourcePath.Contains('#') ? null : SourcePath;
}

/// Small helpers shared by the three parsers.
internal static class SourceReadHelpers
{
    /// Reads a JSONL file line by line, skipping blank and unparseable lines.
    /// A single corrupt line must never abort a session — transcripts are written
    /// live and a torn last line is normal.
    internal static async IAsyncEnumerable<System.Text.Json.Nodes.JsonNode> ReadJsonLinesAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(path);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;

            System.Text.Json.Nodes.JsonNode? node;
            try
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (node is not null) yield return node;
        }
    }

    /// Reads the first few lines of a JSONL file and returns the first object that
    /// satisfies <paramref name="predicate"/>. Used at discovery time to learn a
    /// session's real `cwd` without parsing the whole file — the project handle
    /// must be the hash of the *actual* directory, or the same repo lands under two
    /// different handles depending on which tool wrote the session.
    internal static System.Text.Json.Nodes.JsonObject? PeekFirstObject(
        string path,
        Func<System.Text.Json.Nodes.JsonObject, bool> predicate,
        int maxLines = 20)
    {
        try
        {
            using var reader = new StreamReader(path);
            for (var i = 0; i < maxLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.Length == 0) continue;

                System.Text.Json.Nodes.JsonNode? node;
                try
                {
                    node = System.Text.Json.Nodes.JsonNode.Parse(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                if (node is System.Text.Json.Nodes.JsonObject obj && predicate(obj)) return obj;
            }
        }
        catch (IOException)
        {
            // Session being written right now, or permissions. Discovery falls back.
        }

        return null;
    }

    /// Epoch-ms (as number or string) to DateTimeOffset.
    internal static DateTimeOffset FromEpochMs(System.Text.Json.Nodes.JsonNode? node, DateTimeOffset fallback)
    {
        if (node is null) return fallback;
        try
        {
            var value = node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => node.GetValue<long>(),
                System.Text.Json.JsonValueKind.String => long.TryParse(node.GetValue<string>(), out var parsed) ? parsed : 0,
                _ => 0,
            };

            return value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    internal static string? Str(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? node.GetValue<string>()
                : node.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    internal static long? Num(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<long>() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static double? Dbl(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<double>() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool? Bool(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            var kind = node.GetValueKind();

            return kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? node.GetValue<bool>()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// Raw backup for the two file-backed sources: the transcript verbatim.
    /// Returns null when the file has gone since discovery, which is normal —
    /// `claude --resume` rewrites and Codex compaction replaces files mid-run.
    internal static long? CopyBackingFile(DiscoveredAgentSession session, Stream destination)
    {
        if (session.BackingFile is not { Length: > 0 } path || !File.Exists(path)) return null;

        using var input = File.OpenRead(path);
        input.CopyTo(destination);

        return input.Length;
    }

    /// Counts added/removed lines in a unified diff.
    internal static (int Added, int Removed) CountDiffLines(string? unifiedDiff)
    {
        if (string.IsNullOrEmpty(unifiedDiff)) return (0, 0);

        var added = 0;
        var removed = 0;
        foreach (var line in unifiedDiff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal)) continue;
            if (line.StartsWith('+')) added++;
            else if (line.StartsWith('-')) removed++;
        }

        return (added, removed);
    }
}
