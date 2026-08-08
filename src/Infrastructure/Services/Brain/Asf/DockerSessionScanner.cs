using System.Diagnostics;
using System.Formats.Tar;
using System.Text;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Agent sessions that ran *inside a container* and were left behind in a docker
/// volume. The three <see cref="IAgentSessionSource"/> implementations only look
/// at host paths, so this is a blind spot with real data in it: on the machine
/// this was written for, 28 dangling `claude-code-config-*` volumes held 143
/// Claude transcripts that no host-side scan could see.
///
/// Deliberately NOT an <see cref="IAgentSessionSource"/>. A Claude session is
/// `src: "claude"` in ASF whether it ran on the host or in a container — docker is
/// a *location*, not a fourth tool — so inventing a fourth source kind would put a
/// lie in the event envelope. This scanner reports the inventory; wiring it into
/// ingest means teaching the existing sources to read from a mounted volume, which
/// is a separate change with its own decision to make (see the project-handle note
/// on <see cref="DockerAgentStore.ProjectDirs"/>).
public interface IDockerSessionScanner
{
    /// Cheap: is there a docker daemon we can talk to at all? One `docker version`.
    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);

    /// Cheap: every volume name, plus which of them *look* like an agent's config
    /// volume. Names only — no container is started, nothing is read.
    Task<DockerVolumeInventory> InventoryAsync(CancellationToken ct = default);

    /// Expensive: mounts the given volumes read-only into a throwaway container and
    /// lists every session file inside. Roughly one container per 25 volumes.
    /// Returns one row per file — aggregate with <see cref="DockerScan"/>.
    Task<IReadOnlyList<DockerSessionFile>> ScanAsync(
        IReadOnlyList<string> volumeNames,
        CancellationToken ct = default);

    /// Copies the session data out of each volume into
    /// `<paramref name="destinationRoot"/>/&lt;volume&gt;/`, one throwaway container
    /// per volume so a bad volume costs only itself.
    ///
    /// The copy is a tar stream read back over stdout rather than a writable bind
    /// mount: the helper container runs as root, so anything it wrote to a mount
    /// would land root-owned on a host directory the user has to read afterwards.
    /// Extracting in-process makes the files the caller's own.
    Task<DockerRescueResult> RescueAsync(
        IReadOnlyList<string> volumeNames,
        string destinationRoot,
        bool everything = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}

/// <param name="Volumes">Volumes that yielded at least one file.</param>
/// <param name="Files">Files written to disk.</param>
/// <param name="Bytes">Their total size on disk.</param>
/// <param name="Skipped">Volumes that held nothing worth copying.</param>
/// <param name="Failed">Volume name → why. Never fatal; the other volumes still copy.</param>
/// <param name="Rescued">
/// Volume name → the directory it was written to, for the volumes that yielded
/// something. This is what the caller registers as a session root, so the copies
/// stop being an inert mirror and become discoverable by the sources.
/// </param>
public sealed record DockerRescueResult(
    int Volumes,
    int Files,
    long Bytes,
    int Skipped,
    IReadOnlyDictionary<string, string> Failed,
    IReadOnlyDictionary<string, string> Rescued)
{
    public static readonly DockerRescueResult Empty =
        new(0, 0, 0, 0, new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// One session file found inside a volume. Deliberately unaggregated: the probe
/// runs in busybox sh, where grouping means associative arrays it does not have,
/// so it emits rows and <see cref="DockerScan"/> does the arithmetic in C#.
/// <param name="ProjectDir">
/// The directory name as it existed *inside the container* — e.g.
/// `-workspaces-repo`. Not a host path, and that is the open decision before
/// ingest: ASF hashes the session's cwd into the project handle, so a container's
/// `/workspaces/repo` and the host checkout of the same repo land under two
/// different handles unless a mount mapping is applied.
/// </param>
public sealed record DockerSessionFile(
    string VolumeName,
    string Tool,
    string ProjectDir,
    long Bytes,
    DateTimeOffset Modified);

/// <param name="AllVolumes">Every volume on the daemon.</param>
/// <param name="Candidates">
/// Those whose *name* matches a known agent-config pattern. A heuristic, and
/// deliberately so: the alternative is mounting all 200+ volumes to find out.
/// </param>
public sealed record DockerVolumeInventory(
    IReadOnlyList<string> AllVolumes,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Dangling)
{
    public static readonly DockerVolumeInventory Empty =
        new([], [], []);
}

/// A tool × project rollup across every volume — the shape worth printing. Twenty
/// volumes holding one session each is noise; "130 claude sessions from
/// `-workspaces-repo`" is the finding.
public sealed record DockerProjectGroup(
    string Tool,
    string ProjectDir,
    int Volumes,
    int Sessions,
    long Bytes,
    DateTimeOffset Oldest,
    DateTimeOffset Newest);

/// Aggregations over a scan's file rows.
public static class DockerScan
{
    public static IReadOnlyList<DockerProjectGroup> ByProject(IEnumerable<DockerSessionFile> files) =>
        files
            .GroupBy(f => (f.Tool, f.ProjectDir))
            .Select(g => new DockerProjectGroup(
                g.Key.Tool,
                g.Key.ProjectDir,
                g.Select(f => f.VolumeName).Distinct(StringComparer.Ordinal).Count(),
                g.Count(),
                g.Sum(f => f.Bytes),
                g.Min(f => f.Modified),
                g.Max(f => f.Modified)))
            .OrderByDescending(g => g.Sessions)
            .ThenBy(g => g.ProjectDir, StringComparer.Ordinal)
            .ToList();

    public static IReadOnlyList<string> Volumes(IEnumerable<DockerSessionFile> files) =>
        files.Select(f => f.VolumeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

public sealed class DockerSessionScanner : IDockerSessionScanner
{
    /// Volumes per helper container. Each mount costs a `-v` argument, and the
    /// argument list is the only thing that grows, so this is about keeping one
    /// failure from losing the whole scan rather than about a hard limit.
    private const int BatchSize = 25;

    /// Images tried in order. The first one already present locally wins, so a scan
    /// on a normal dev machine pulls nothing. busybox's `find` has no `-printf`,
    /// which is why the probe script below sticks to `-name`/`-path` and `du`.
    private static readonly string[] ScanImages = ["alpine:3", "alpine:latest", "busybox:latest"];

    /// Name patterns for the volumes worth mounting. `claude-code-config-*` is what
    /// the devcontainer feature creates, and is the reason only Claude data survives
    /// this way — Codex and opencode write to paths that feature does not persist.
    private static readonly (string Fragment, string Tool)[] NameHints =
    [
        ("claude-code-config", AsfSource.Claude),
        ("claude-config", AsfSource.Claude),
        ("dotclaude", AsfSource.Claude),
        ("codex", AsfSource.Codex),
        ("opencode", AsfSource.OpenCode),
    ];

    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct = default)
    {
        var probe = await RunAsync(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(10), ct);

        return probe.ExitCode == 0 && probe.Stdout.Trim().Length > 0;
    }

    public async Task<DockerVolumeInventory> InventoryAsync(CancellationToken ct = default)
    {
        var all = await RunAsync(["volume", "ls", "-q"], TimeSpan.FromSeconds(30), ct);
        if (all.ExitCode != 0) return DockerVolumeInventory.Empty;

        var names = SplitLines(all.Stdout);

        var dangling = await RunAsync(["volume", "ls", "-q", "-f", "dangling=true"], TimeSpan.FromSeconds(30), ct);

        return new DockerVolumeInventory(
            names,
            names.Where(n => ToolHintFor(n) is not null).ToList(),
            dangling.ExitCode == 0 ? SplitLines(dangling.Stdout) : []);
    }

    public async Task<IReadOnlyList<DockerSessionFile>> ScanAsync(
        IReadOnlyList<string> volumeNames,
        CancellationToken ct = default)
    {
        if (volumeNames.Count == 0) return [];

        var image = await ResolveScanImageAsync(ct);
        if (image is null) return [];

        var files = new List<DockerSessionFile>();

        for (var i = 0; i < volumeNames.Count; i += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = volumeNames.Skip(i).Take(BatchSize).ToList();
            var args = new List<string> { "run", "--rm", "--network", "none" };
            foreach (var name in batch)
            {
                args.Add("-v");
                args.Add($"{name}:/scan/{name}:ro");
            }

            args.Add(image);
            args.Add("sh");
            args.Add("-c");
            args.Add(ProbeScript);

            // A batch that fails (a volume with a driver we cannot mount, a daemon
            // hiccup) must not lose the batches around it.
            var result = await RunAsync(args, TimeSpan.FromMinutes(5), ct);
            if (result.ExitCode != 0) continue;

            files.AddRange(ParseProbeOutput(result.Stdout));
        }

        return files;
    }

    public async Task<DockerRescueResult> RescueAsync(
        IReadOnlyList<string> volumeNames,
        string destinationRoot,
        bool everything = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (volumeNames.Count == 0) return DockerRescueResult.Empty;

        var image = await ResolveScanImageAsync(ct);
        if (image is null) return DockerRescueResult.Empty;

        Directory.CreateDirectory(destinationRoot);

        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var rescued = new Dictionary<string, string>(StringComparer.Ordinal);
        var volumes = 0;
        var skipped = 0;
        var files = 0;
        var bytes = 0L;

        foreach (var name in volumeNames)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(name);

            var target = Path.Combine(destinationRoot, name);

            try
            {
                Directory.CreateDirectory(target);
                await ExtractVolumeAsync(image, name, target, everything, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable volume — a driver we cannot mount, a truncated
                // stream, a daemon hiccup — must not cost the other 27.
                failed[name] = ex.Message;

                continue;
            }

            var written = new DirectoryInfo(target).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            if (written.Count == 0)
            {
                // A candidate whose name matched but which holds no session data.
                // Leaving the empty directory behind would make the destination
                // look like it rescued more than it did.
                skipped++;
                Directory.Delete(target, recursive: true);

                continue;
            }

            volumes++;
            files += written.Count;
            bytes += written.Sum(f => f.Length);
            rescued[name] = target;
        }

        return new DockerRescueResult(volumes, files, bytes, skipped, failed, rescued);
    }

    /// `docker run … tar cf - <paths>` with the archive read straight off stdout.
    ///
    /// `.credentials.json` is excluded in both modes and that is not a detail: a
    /// Claude config volume holds a live OAuth refresh token, and a backup is by
    /// definition a copy onto somewhere more durable and usually more shared.
    /// Nothing in a session transcript needs it.
    private static async Task ExtractVolumeAsync(
        string image,
        string volumeName,
        string target,
        bool everything,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in new[]
                 {
                     "run", "--rm", "--network", "none",
                     "-v", $"{volumeName}:/v:ro",
                     image, "sh", "-c", everything ? RescueAllScript : RescueSessionsScript,
                 })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("docker did not start");

        // stderr has to be drained concurrently or a chatty container fills its
        // pipe buffer and both sides wait forever.
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await TarFile.ExtractToDirectoryAsync(
            process.StandardOutput.BaseStream, target, overwriteFiles: true, ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException((await stderr).Trim() is { Length: > 0 } e ? e : "tar failed");
    }

    /// What a coding agent actually left behind, across the three tools: Claude's
    /// `projects/`, Codex's `sessions/`, and opencode's database plus the
    /// `tool-output/` spill directory it deletes on a 7-day timer.
    private const string RescueSessionsScript = """
        cd /v 2>/dev/null || exit 0
        set --
        for p in projects sessions archived_sessions storage tool-output opencode.db history.jsonl; do
          [ -e "$p" ] && set -- "$@" "$p"
        done
        [ $# -eq 0 ] && exit 0
        exec tar cf - --exclude=*.credentials.json "$@"
        """;

    private const string RescueAllScript = """
        cd /v 2>/dev/null || exit 0
        exec tar cf - --exclude=*.credentials.json --exclude=*.credentials.json.bak .
        """;

    /// Emits one pipe-delimited line per session file:
    /// `volume|tool|projectDir|bytes|mtimeEpoch`.
    ///
    /// Written against busybox, not GNU: `find -printf` does not exist there and
    /// neither does `xargs -d`, so size and mtime come from `stat -c '%s %Y'`, which
    /// both busybox and coreutils support. All grouping is left to the caller —
    /// aggregating here would need associative arrays that this shell lacks.
    ///
    /// The project is the first component *under* `projects/`, not the file's own
    /// parent: Claude nests subagent and workflow transcripts one level deeper
    /// (`<slug>/subagents/…`, `<slug>/wf_<id>/…`), so the immediate parent reports
    /// a project literally named `subagents`. Codex rollouts and the opencode DB
    /// carry no project in their path at all — theirs lives in the file.
    private const string ProbeScript = """
        for d in /scan/*; do
          [ -d "$d" ] || continue
          vol=$(basename "$d")
          for spec in "claude:*/projects/*.jsonl" "codex:*/sessions/*.jsonl" "opencode:*/opencode.db"; do
            tool=${spec%%:*}
            pat=${spec#*:}
            find "$d" -type f -path "$pat" 2>/dev/null | while IFS= read -r f; do
              meta=$(stat -c '%s %Y' "$f" 2>/dev/null) || continue
              case "$tool" in
                claude) rel=${f#*/projects/}; proj=${rel%%/*} ;;
                *) proj="-" ;;
              esac
              echo "$vol|$tool|$proj|${meta%% *}|${meta##* }"
            done
          done
        done
        """;

    internal static IEnumerable<DockerSessionFile> ParseProbeOutput(string stdout)
    {
        foreach (var line in SplitLines(stdout))
        {
            var parts = line.Split('|');
            if (parts.Length != 5) continue;
            if (!long.TryParse(parts[3], out var bytes)) continue;
            if (!long.TryParse(parts[4], out var epoch) || epoch <= 0) continue;

            yield return new DockerSessionFile(
                parts[0],
                parts[1],
                parts[2],
                bytes,
                DateTimeOffset.FromUnixTimeSeconds(epoch));
        }
    }

    /// The tool a volume's *name* suggests, or null when the name says nothing.
    public static string? ToolHintFor(string volumeName)
    {
        foreach (var (fragment, tool) in NameHints)
        {
            if (volumeName.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return tool;
        }

        return null;
    }

    private async Task<string?> ResolveScanImageAsync(CancellationToken ct)
    {
        var local = await RunAsync(["images", "--format", "{{.Repository}}:{{.Tag}}"], TimeSpan.FromSeconds(30), ct);
        if (local.ExitCode == 0)
        {
            var present = new HashSet<string>(SplitLines(local.Stdout), StringComparer.Ordinal);
            var hit = ScanImages.FirstOrDefault(present.Contains);
            if (hit is not null) return hit;
        }

        // Nothing suitable locally — pull the smallest of them once.
        var pull = await RunAsync(["pull", "--quiet", ScanImages[0]], TimeSpan.FromMinutes(3), ct);

        return pull.ExitCode == 0 ? ScanImages[0] : null;
    }

    private static List<string> SplitLines(string value) =>
        value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start()) return (-1, "", "docker did not start");
        }
        catch (Exception ex)
        {
            // docker not installed, or not on PATH. Never fatal — every caller
            // treats "no docker" as "nothing to report".
            return (-1, "", ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            return (-1, stdout.ToString(), "timed out");
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
