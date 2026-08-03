using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ZstdSharp;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Buffers ASF events for one (day, src, level) bucket and seals them into a
/// content-addressed chunk. Spec: docs/specs/asf/03-chunks-and-hashing.md.
///
/// The chunk hash is sha256 over the **uncompressed** NDJSON bytes, never over
/// the compressed file. Compression level, library version and even the choice of
/// codec are then free to change without turning every chunk on the server into a
/// new one — and re-exporting the same events after such a change is a no-op
/// instead of a full re-upload.
///
/// Determinism is the other half of that promise. Given the same events in the
/// same order, the bytes are identical: canonical JSON (sorted keys, no
/// whitespace, absent members omitted), one event per line, `\n` endings, no
/// trailing newline ambiguity — the file always ends with exactly one.
public sealed class AsfChunkWriter
{
    /// Seal at 8 MiB uncompressed. Large enough that a busy day is a handful of
    /// chunks, small enough that a failed upload re-sends little.
    public const int DefaultSealBytes = 8 * 1024 * 1024;

    private readonly List<byte[]> _lines = new();
    private readonly SortedSet<string> _sessionIds = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, int> _kinds = new(StringComparer.Ordinal);
    private long _bytes;
    private DateTimeOffset? _firstTs;
    private DateTimeOffset? _lastTs;

    public AsfChunkWriter(DateOnly day, string src, string level, int sealBytes = DefaultSealBytes)
    {
        Day = day;
        Src = src;
        Level = level;
        SealBytes = sealBytes;
    }

    public DateOnly Day { get; }
    public string Src { get; }
    public string Level { get; }
    public int SealBytes { get; }

    public int Count => _lines.Count;
    public long UncompressedBytes => _bytes;
    public bool IsEmpty => _lines.Count == 0;

    /// True once the buffer has reached the seal threshold. The caller checks this
    /// after each Add so a chunk is closed on a line boundary.
    public bool ShouldSeal => _bytes >= SealBytes;

    public void Add(AsfEvent e)
    {
        if (e.Id is null)
            throw new InvalidOperationException("Event must be sealed with an id before it can be chunked.");

        _lines.Add(CanonicalJson.SerializeToUtf8(e));
        _bytes += _lines[^1].Length + 1; // + '\n'
        _sessionIds.Add(e.Session);
        _kinds[e.Kind] = _kinds.GetValueOrDefault(e.Kind) + 1;

        // Events arrive in (session, seq) order, not timestamp order — a session
        // read later can start earlier — so the window is a min/max, not first/last.
        if (_firstTs is null || e.Ts < _firstTs) _firstTs = e.Ts;
        if (_lastTs is null || e.Ts > _lastTs) _lastTs = e.Ts;
    }

    /// Writes `<dir>/<src>-<level>-<nn>.ndjson.zst` plus its manifest sidecar and
    /// returns the manifest. `ordinal` is chosen by the caller from what already
    /// exists on disk, so a chunk file is never overwritten.
    ///
    /// `sealedAt` and `exporter` land in the sidecar only — never in the hashed
    /// NDJSON — so two machines exporting the same source data still agree on the
    /// chunk hash. Passing `sealedAt` in rather than reading the clock here is what
    /// lets the determinism test compare two runs byte for byte.
    public ChunkManifest Seal(string dir, int ordinal, DateTimeOffset sealedAt, string exporter, string exportRoot)
    {
        if (IsEmpty) throw new InvalidOperationException("Refusing to seal an empty chunk.");

        Directory.CreateDirectory(dir);

        var raw = new byte[_bytes];
        var pos = 0;
        foreach (var line in _lines)
        {
            line.CopyTo(raw, pos);
            pos += line.Length;
            raw[pos++] = (byte)'\n';
        }

        var hash = CanonicalJson.Hex(SHA256.HashData(raw));
        var name = $"{Src}-{Level}-{ordinal:D2}";
        var chunkPath = Path.Combine(dir, name + ".ndjson.zst");

        using (var compressor = new Compressor(CompressionLevel))
        {
            File.WriteAllBytes(chunkPath, compressor.Wrap(raw).ToArray());
        }

        var manifest = new ChunkManifest
        {
            ChunkHash = hash,
            Src = Src,
            Level = Level,
            Day = Day.ToString("yyyy-MM-dd"),
            Ordinal = ordinal,
            Events = _lines.Count,
            FirstTs = _firstTs,
            LastTs = _lastTs,
            SessionIds = _sessionIds.ToList(),
            Kinds = new Dictionary<string, int>(_kinds, StringComparer.Ordinal),
            UncompressedBytes = _bytes,
            Bytes = new FileInfo(chunkPath).Length,
            Path = Path.GetRelativePath(exportRoot, chunkPath).Replace('\\', '/'),
            SealedAt = sealedAt,
            Exporter = exporter,
        };

        File.WriteAllText(
            Path.Combine(dir, name + ".manifest.json"),
            CanonicalJson.Serialize(manifest) + "\n",
            new UTF8Encoding(false));

        return manifest;
    }

    /// 10 is zstd's "good ratio, still fast" band. It has no effect on the chunk
    /// hash — see the class comment — so it can be retuned freely.
    private const int CompressionLevel = 10;

    /// Reverses Seal for verification and for re-reading a chunk before upload.
    public static IEnumerable<string> ReadLines(string chunkPath)
    {
        using var decompressor = new Decompressor();
        var raw = decompressor.Unwrap(File.ReadAllBytes(chunkPath)).ToArray();

        return Encoding.UTF8.GetString(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// The hash a chunk file actually has, recomputed from its contents. Used by
    /// `brain export --verify` and by the uploader before it claims a hash.
    public static string HashOf(string chunkPath)
    {
        using var decompressor = new Decompressor();

        return CanonicalJson.Hex(
            SHA256.HashData(decompressor.Unwrap(File.ReadAllBytes(chunkPath)).ToArray()));
    }
}

/// The per-chunk sidecar. Also the body the sync API's manifest step posts, which
/// is why it carries everything needed to decide "do I already have this?"
/// without touching the chunk itself.
public sealed class ChunkManifest
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("chunkHash")] public string ChunkHash { get; set; } = "";
    [JsonPropertyName("src")] public string Src { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("day")] public string Day { get; set; } = "";
    [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
    [JsonPropertyName("events")] public int Events { get; set; }
    [JsonPropertyName("firstTs")] public DateTimeOffset? FirstTs { get; set; }
    [JsonPropertyName("lastTs")] public DateTimeOffset? LastTs { get; set; }
    [JsonPropertyName("sessionIds")] public List<string> SessionIds { get; set; } = new();
    [JsonPropertyName("kinds")] public Dictionary<string, int> Kinds { get; set; } = new();
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("uncompressedBytes")] public long UncompressedBytes { get; set; }
    [JsonPropertyName("sealedAt")] public DateTimeOffset? SealedAt { get; set; }
    [JsonPropertyName("exporter")] public string? Exporter { get; set; }

    /// Path relative to the export root, so the manifest survives a moved or
    /// synced home directory. Never uploaded — the push command strips it.
    [JsonPropertyName("path")] public string? Path { get; set; }

    /// Set once the receiver has acknowledged this chunk.
    [JsonPropertyName("uploadedAt")] public DateTimeOffset? UploadedAt { get; set; }
    [JsonPropertyName("syncId")] public string? SyncId { get; set; }
}
