using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Event, session and project identity. Spec: docs/specs/asf/03-chunks-and-hashing.md.
///
/// The one invariant everything else rests on: `id` is computed over the FULL
/// event, before redaction, with `id` and `level` excluded from the hash. The
/// exporter always has full local data — it hashes first and redacts after — so
/// the same event exported at `metrics` on Monday and at `full` on Friday
/// carries the same id, and the server enriches the stored row instead of
/// inserting a duplicate. That is what makes level changes non-destructive and
/// keeps totals honest across them.
public static class AsfEventId
{
    /// "e_" + sha256 hex over (src, session, seq, kind, contentHash).
    ///
    /// Two layers because content alone is not unique — the same `Bash: ls`
    /// appears a thousand times. session+seq disambiguate position; contentHash
    /// means a re-parse yielding different content at the same position gets a
    /// different id rather than silently overwriting.
    public static string Compute(AsfEvent fullEvent)
    {
        var contentHash = ContentHash(fullEvent);
        var key = new JsonObject
        {
            ["src"] = fullEvent.Src,
            ["session"] = fullEvent.Session,
            ["seq"] = fullEvent.Seq,
            ["kind"] = fullEvent.Kind,
            ["contentHash"] = contentHash,
        };

        return "e_" + CanonicalJson.Sha256Hex(key);
    }

    /// sha256 over the canonical JSON of the fully-masked, unredacted event with
    /// `id`, `level`, `origin` and `serverMasked` removed. `level` is excluded
    /// precisely so the id is level-independent; `serverMasked` is a receiver
    /// annotation that must not change identity; `origin` records which *copy* of
    /// a session was read, and two copies of one session are one event.
    ///
    /// Anything added to <see cref="AsfEvent"/> that describes the reading rather
    /// than the session belongs on this list. Forgetting it is how a session
    /// rescued from a docker volume ends up counted twice next to the same
    /// session read from the host.
    public static string ContentHash(AsfEvent fullEvent)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(fullEvent, CanonicalJson.SerializerOptions)
            as JsonObject
            ?? throw new InvalidOperationException("AsfEvent did not serialize to a JSON object.");

        node.Remove("id");
        node.Remove("level");
        node.Remove("origin");
        node.Remove("serverMasked");

        return CanonicalJson.Sha256Hex(node);
    }

    /// Assigns Id in place and returns the event, for fluent parser code.
    public static AsfEvent WithId(this AsfEvent fullEvent)
    {
        fullEvent.Id = Compute(fullEvent);

        return fullEvent;
    }

    /// "s_" + first 32 hex of sha256("&lt;src&gt;:&lt;native session id&gt;").
    /// Deterministic and non-reversible: the same session yields the same handle
    /// on every machine, and the native id never leaves the client.
    public static string SessionHandle(string src, string nativeSessionId) =>
        "s_" + Truncated($"{src}:{nativeSessionId}", 32);

    /// "p_" + first 32 hex of sha256(absolute project root).
    public static string ProjectHandle(string projectRoot) => "p_" + Truncated(projectRoot, 32);

    /// "m_" + first 32 hex of sha256(hostname). Lets the UI say "3 machines"
    /// without learning any of their names.
    public static string MachineHandle(string? hostName = null) =>
        "m_" + Truncated(hostName ?? Environment.MachineName, 32);

    /// Hash of a path or branch name, for the levels that omit the cleartext.
    public static string OpaqueHash(string value) => Truncated(value, 32);

    private static string Truncated(string value, int hexChars)
    {
        var hash = CanonicalJson.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        return hash[..Math.Min(hexChars, hash.Length)];
    }
}
