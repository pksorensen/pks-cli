using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Brain;

/// File-backed <see cref="IBrainRootRegistry"/> over `~/.pks-cli/brain/roots.json`.
///
/// Reads are cached for the lifetime of the instance — the sources ask for the
/// roots once per discovery pass and the file changes only when a backup runs —
/// and every write goes through a temp file + rename so a killed process cannot
/// leave a half-written registry behind.
public sealed class BrainRootRegistry : IBrainRootRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IBrainPathResolver _paths;
    private readonly object _gate = new();
    private List<BrainSessionRoot>? _cache;

    public BrainRootRegistry(IBrainPathResolver paths) => _paths = paths;

    public string RegistryPath => Path.Combine(_paths.GlobalRoot, "roots.json");

    public IReadOnlyList<BrainSessionRoot> All()
    {
        lock (_gate)
        {
            return _cache ??= Load();
        }
    }

    public IReadOnlyList<BrainSessionRoot> Usable() =>
        All().Where(IsReadableNow).ToList();

    public bool Add(BrainSessionRoot root) => AddRange([root]) > 0;

    public int AddRange(IEnumerable<BrainSessionRoot> roots)
    {
        lock (_gate)
        {
            var current = _cache ??= Load();
            var added = 0;

            foreach (var root in roots)
            {
                var full = Normalize(root.Path);
                if (full is null) continue;

                var index = current.FindIndex(r =>
                    string.Equals(Normalize(r.Path), full, StringComparison.Ordinal));

                if (index >= 0)
                {
                    // Re-registering an existing root refreshes its origin and note
                    // but keeps the original AddedUtc: the answer to "since when has
                    // the brain known about this" should not reset on every backup.
                    current[index] = current[index] with { Origin = root.Origin, Note = root.Note };

                    continue;
                }

                current.Add(root with { Path = full });
                added++;
            }

            if (added > 0 || current.Count > 0) Save(current);

            return added;
        }
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            var current = _cache ??= Load();
            var full = Normalize(path);
            var removed = current.RemoveAll(r =>
                string.Equals(Normalize(r.Path), full, StringComparison.Ordinal));

            if (removed > 0) Save(current);

            return removed > 0;
        }
    }

    /// Exists *and* holds something. An empty directory is the signature of a
    /// vanished bind mount whose mount point stayed behind, which is exactly the
    /// case that must not be mistaken for "this root has no sessions".
    private static bool IsReadableNow(BrainSessionRoot root)
    {
        try
        {
            return Directory.Exists(root.Path) &&
                   Directory.EnumerateFileSystemEntries(root.Path).Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<BrainSessionRoot> Load()
    {
        var path = RegistryPath;
        if (!File.Exists(path)) return [];

        try
        {
            var doc = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(path), Json);

            return doc?.Roots ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt registry must not take ingest down with it. The host's own
            // sources still work; the rescued copies are simply invisible until the
            // next backup rewrites the file.
            return [];
        }
    }

    private void Save(List<BrainSessionRoot> roots)
    {
        var path = RegistryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new RegistryFile { Roots = roots }, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private sealed class RegistryFile
    {
        [JsonPropertyName("v")] public int V { get; set; } = 1;
        [JsonPropertyName("roots")] public List<BrainSessionRoot> Roots { get; set; } = [];
    }
}
