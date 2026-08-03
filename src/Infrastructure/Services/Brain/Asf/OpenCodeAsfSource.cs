using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// opencode → ASF. Reads ~/.local/share/opencode/opencode.db **read-only**,
/// joining `session` → `message` → `part`.
///
/// Mapping table: docs/specs/asf/01-event-envelope.md §opencode.
///
/// This is the source the daily job exists for. opencode's `ToolOutputStore`
/// spills any tool output over 2000 lines or 50 KiB to
/// &lt;data&gt;/tool-output/tool_&lt;id&gt;, keeps only a head/tail in the database, and
/// runs an hourly cleanup that deletes spill files older than a hardcoded 7 days
/// (`RETENTION = Duration.days(7)` — no config key, no env var). After that the
/// pointer in the database refers to a file that no longer exists. The parser
/// therefore reads the spill file while it is still there.
public sealed class OpenCodeAsfSource : IAgentSessionSource
{
    private readonly IBrainPathResolver _paths;

    public OpenCodeAsfSource(IBrainPathResolver paths) => _paths = paths;

    public string Kind => AsfSource.OpenCode;

    public bool IsAvailable => File.Exists(_paths.OpenCodeDbPath);

    public string Location => _paths.OpenCodeDbPath;

    /// The spill marker opencode inlines into the truncated output — verified
    /// against the 1.18.11 bundle:
    ///   `... output truncated; full content saved to /path/to/tool_xyz ...`
    /// There is no structured field for this; the path only exists in the text.
    /// Replaces the marker once the spill file it points at is gone, so no local
    /// path survives into an event id or an upload.
    private const string ExpiredSpillMarker = "... output truncated; spill file expired ...";

    private static readonly Regex SpillMarker = new(
        @"\.\.\. output truncated; full content saved to (?<path>.+?) \.\.\.",
        RegexOptions.Compiled);

    public IEnumerable<DiscoveredAgentSession> Discover(string? projectFilter = null)
    {
        if (!File.Exists(_paths.OpenCodeDbPath)) yield break;

        using var conn = OpenReadOnly(_paths.OpenCodeDbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT s.id, s.directory, s.time_updated,
                   (SELECT COALESCE(SUM(LENGTH(CAST(p.data AS BLOB))), 0)
                    FROM part p WHERE p.session_id = s.id)
            FROM session s
            ORDER BY s.id
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var directory = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var root = _paths.Normalize(directory) ?? directory;
            var slug = root.Length > 0 ? _paths.EncodeSlug(root) : "-unknown";

            if (projectFilter is { Length: > 0 } &&
                !slug.Contains(projectFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var updated = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            // The session has no file of its own, so "size" is the weight of its
            // rows. That is a real byte count — it shows up in the Size column and
            // in SessionMetadata.SourceBytes — and it also serves as the cursor's
            // change detector, since appending a part always moves it.
            var bytes = reader.IsDBNull(3) ? 0L : reader.GetInt64(3);

            yield return new DiscoveredAgentSession(
                Kind,
                sessionId,
                slug,
                root,
                // Many sessions share one file, so the path carries the session id.
                // DiscoveredAgentSession.BackingFile is null for these — the blob
                // backup synthesizes a per-session dump instead of copying a 38 MB
                // database once per session.
                $"{_paths.OpenCodeDbPath}#{sessionId}",
                updated > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(updated).UtcDateTime
                    : File.GetLastWriteTimeUtc(_paths.OpenCodeDbPath),
                bytes);
        }
    }

    public async IAsyncEnumerable<AsfEvent> ReadAsync(
        DiscoveredAgentSession session,
        ISecretMasker masker,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var dbPath = session.SourcePath.Split('#')[0];
        if (!File.Exists(dbPath)) yield break;

        await using var conn = OpenReadOnly(dbPath);
        var row = ReadSessionRow(conn, session.NativeSessionId);
        if (row is null) yield break;

        var builder = new AsfEventBuilder(
            masker, Kind, row.Version, session.NativeSessionId, session.ProjectRoot);

        var created = DateTimeOffset.FromUnixTimeMilliseconds(row.TimeCreated);
        var start = builder.Begin(AsfKind.SessionStart, created);
        builder.SetContext(start, row.Directory, null);
        start.ProjectPath = row.Directory;
        start.Agent = row.Agent;
        start.Model = ModelId(row.Model);
        start.Entrypoint = "opencode";

        yield return builder.Seal(start);

        var turnId = (string?)null;
        var turnIndex = 0;

        foreach (var part in ReadParts(conn, session.NativeSessionId, ct))
        {
            if (part.Data is not JsonObject data) continue;

            var ts = DateTimeOffset.FromUnixTimeMilliseconds(part.TimeCreated);
            var type = SourceReadHelpers.Str(data["type"]);

            switch (type)
            {
                case "step-start":
                    turnId = $"t{turnIndex++}";

                    continue;

                case "text":
                {
                    var text = SourceReadHelpers.Str(data["text"]);
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var e = builder.Begin(
                        part.Role == "user" ? AsfKind.Prompt : AsfKind.Assistant, ts);
                    e.TurnId = turnId;
                    e.Model = part.Model;
                    e.StopReason = part.Role == "user" ? null : part.Finish;

                    yield return builder.Seal(builder.SetText(e, text));

                    continue;
                }

                case "reasoning":
                {
                    var e = builder.Begin(AsfKind.Thinking, ts);
                    e.TurnId = turnId;
                    e.Model = part.Model;
                    e.DurationMs = TimeSpanMs(data["time"]);

                    yield return builder.Seal(
                        builder.SetThinking(e, SourceReadHelpers.Str(data["text"])));

                    continue;
                }

                case "tool":
                {
                    foreach (var e in TranslateTool(builder, data, ts, turnId, part.Model))
                    {
                        yield return builder.Seal(e);
                    }

                    continue;
                }

                case "patch":
                {
                    if (data["files"] is not JsonArray files) continue;
                    var hash = SourceReadHelpers.Str(data["hash"]);

                    foreach (var file in files)
                    {
                        var path = SourceReadHelpers.Str(file);
                        if (path is null) continue;

                        var e = builder.Begin(AsfKind.FileOp, ts);
                        e.TurnId = turnId;
                        e.Op = "edit";
                        e.Ok = true;
                        e.Reason = hash;
                        builder.SetPath(e, path);

                        yield return builder.Seal(e);
                    }

                    continue;
                }

                case "step-finish":
                {
                    var tokens = data["tokens"] as JsonObject;
                    var e = builder.Begin(AsfKind.Usage, ts);
                    e.TurnId = turnId;
                    e.Model = part.Model;
                    e.StopReason = SourceReadHelpers.Str(data["reason"]);
                    e.InputTokens = SourceReadHelpers.Num(tokens?["input"]);
                    e.OutputTokens = SourceReadHelpers.Num(tokens?["output"]);
                    e.ReasoningTokens = SourceReadHelpers.Num(tokens?["reasoning"]);
                    e.CacheReadTokens = SourceReadHelpers.Num(tokens?["cache"]?["read"]);
                    e.CacheWriteTokens = SourceReadHelpers.Num(tokens?["cache"]?["write"]);
                    e.CostUsd = SourceReadHelpers.Dbl(data["cost"]);

                    yield return builder.Seal(e);

                    continue;
                }
            }
        }

        var ended = row.TimeArchived is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(row.TimeArchived.Value)
            : DateTimeOffset.FromUnixTimeMilliseconds(row.TimeUpdated);
        var end = builder.Begin(AsfKind.SessionEnd, ended);
        end.EndedAt = ended;
        end.Reason = row.TimeArchived is > 0 ? "archived" : "idle";
        end.Turns = turnIndex;
        end.CostUsd = row.Cost;
        end.Synthetic = true;

        yield return builder.Seal(end);
    }

    /// A `part type:"tool"` becomes two ASF events. The level projection has to
    /// redact arguments and output independently, and an interrupted call with no
    /// result must still be countable.
    private IEnumerable<AsfEvent> TranslateTool(
        AsfEventBuilder builder,
        JsonObject data,
        DateTimeOffset ts,
        string? turnId,
        string? model)
    {
        var state = data["state"] as JsonObject;
        var status = SourceReadHelpers.Str(state?["status"]);
        var tool = SourceReadHelpers.Str(data["tool"]) ?? "unknown";
        var callId = SourceReadHelpers.Str(data["callID"]);

        var call = builder.Begin(AsfKind.ToolCall, ts);
        call.TurnId = turnId;
        call.Model = model;
        call.CallId = callId;
        call.Tool = tool;
        call.IsMcp = tool.Contains('_') && tool.StartsWith("mcp", StringComparison.Ordinal) ? true : null;
        builder.SetArgs(call, state?["input"]);

        yield return call;

        // "pending"/"running" — the session was interrupted mid-call. The call
        // above is still real and still counts; there is simply no result yet.
        if (status is not ("completed" or "error")) yield break;

        var result = builder.Begin(AsfKind.ToolResult, ts);
        result.TurnId = turnId;
        result.Model = model;
        result.CallId = callId;
        result.Tool = tool;
        result.IsError = status == "error" ? true : null;
        result.DurationMs = TimeSpanMs(state?["time"]);

        var output = status == "error"
            ? SourceReadHelpers.Str(state?["error"])
            : SourceReadHelpers.Str(state?["output"]);

        // metadata.truncated is the tool's own "I capped the result set" flag
        // (grep/glob match limits), which is a different thing from the spill.
        if (SourceReadHelpers.Bool(state?["metadata"]?["truncated"]) == true) result.Truncated = true;

        var spill = output is null ? null : SpillMarker.Match(output);
        if (spill is { Success: true })
        {
            result.Truncated = true;
            result.Spilled = true;

            var spillPath = spill.Groups["path"].Value;
            var recovered = TryReadSpill(spillPath);
            if (recovered is not null)
            {
                // Recovered inside the 7-day window: transport the full output, but
                // keep `spilled` set so the record is honest about where it came from.
                builder.SetOutput(result, recovered);

                yield return result;

                yield break;
            }

            // Already swept. Keep the head/tail the database retained rather than
            // dropping the event — a short output is better than a missing one, and
            // `spilled:true` says why it is short.
            result.Reason = "spill-expired";

            // The marker still names the swept file by absolute path. Left in place
            // it would (a) publish a local filesystem path and (b) make the event id
            // machine-dependent, since the id hashes the full output — the same
            // session backed up from two checkouts would dedupe as two events.
            output = SpillMarker.Replace(output!, ExpiredSpillMarker);
        }

        builder.SetOutput(result, output ?? "");

        yield return result;
    }

    private static string? TryReadSpill(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// `{start, end}` epoch-ms.
    private static long? TimeSpanMs(JsonNode? time)
    {
        var start = SourceReadHelpers.Num(time?["start"]);
        var end = SourceReadHelpers.Num(time?["end"]);

        return start is > 0 && end is > 0 ? Math.Max(0, end.Value - start.Value) : null;
    }

    /// `session.model` is a JSON blob `{id, providerID, variant}`.
    private static string? ModelId(string? modelJson)
    {
        if (string.IsNullOrWhiteSpace(modelJson)) return null;

        try
        {
            var node = JsonNode.Parse(modelJson);
            var id = SourceReadHelpers.Str(node?["id"]);
            var provider = SourceReadHelpers.Str(node?["providerID"]);

            return provider is null ? id : $"{provider}/{id}";
        }
        catch (JsonException)
        {
            return modelJson;
        }
    }

    /// Read-only, and explicitly non-mutating: opencode may be running right now,
    /// and a stray write or a recovered WAL would corrupt a live session.
    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        conn.Open();

        return conn;
    }

    private sealed record SessionRow(
        string Id,
        string Directory,
        string? Version,
        string? Agent,
        string? Model,
        double Cost,
        long TimeCreated,
        long TimeUpdated,
        long? TimeArchived);

    private static SessionRow? ReadSessionRow(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, directory, version, agent, model, cost,
                   time_created, time_updated, time_archived
            FROM session WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new SessionRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? 0d : reader.GetDouble(5),
            reader.IsDBNull(6) ? 0L : reader.GetInt64(6),
            reader.IsDBNull(7) ? 0L : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8));
    }

    private sealed record PartRow(
        string Id,
        string MessageId,
        long TimeCreated,
        JsonNode? Data,
        string? Role,
        string? Model,
        string? Finish);

    /// Ordered by `time_created` then `id` — `id` breaks ties deterministically, and
    /// determinism here is what keeps `seq`, and therefore every event id, stable
    /// across runs.
    private static IEnumerable<PartRow> ReadParts(
        SqliteConnection conn,
        string sessionId,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.message_id, p.time_created, p.data, m.data
            FROM part p
            JOIN message m ON m.id = p.message_id
            WHERE p.session_id = $id
            ORDER BY p.time_created, p.id
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (ct.IsCancellationRequested) yield break;

            JsonNode? data = null;
            JsonNode? message = null;
            try
            {
                data = reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3));
                message = reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4));
            }
            catch (JsonException)
            {
                continue;
            }

            var modelId = SourceReadHelpers.Str(message?["modelID"]);
            var providerId = SourceReadHelpers.Str(message?["providerID"]);

            yield return new PartRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                data,
                SourceReadHelpers.Str(message?["role"]),
                modelId is null ? null : providerId is null ? modelId : $"{providerId}/{modelId}",
                SourceReadHelpers.Str(message?["finish"]));
        }
    }
}
