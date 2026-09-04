namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure naming helpers for the Docker volumes a devcontainer job leaves behind, and the single
/// source of truth for which of them the reaper (<c>runner cleanup</c>) and the job-end sweep
/// (<c>RunnerContainerService.CleanupJobAsync</c>) are allowed to remove.
///
/// <para>Two distinct keys are at play, and conflating them is the bug this type exists to prevent:</para>
/// <list type="bullet">
///   <item><b>devcontainerId</b> — derived by the devcontainer CLI from the <c>--id-label</c> set we
///   pass. Because one of those labels is <c>devcontainer.local.volume={volumeName}</c> and the
///   volume name is per-job, the id is per-job too, and so are the four feature volumes keyed by it
///   (see <see cref="Prefixes"/>).</item>
///   <item><b>the workspace volume</b> — <c>devcontainer-{project}-{hash}</c>, created by us and
///   passed in as an external mount. It is <b>not</b> keyed by devcontainerId. Deriving
///   <c>devcontainer-{devcontainerId}</c> as a fifth sibling would name a volume that does not exist
///   while missing the one that does, so the workspace volume must always come from the container's
///   actual mounts (or from <c>RunnerJobState.VolumeName</c>).</item>
/// </list>
///
/// <para>Observed on the si14agents host 2026-08-24: container <c>nice_shirley</c> carried
/// <c>dind-var-lib-docker-1se3hgq99…</c> alongside workspace volume
/// <c>devcontainer-agentic-live-www-2c9c1947</c> — different suffixes, same container.</para>
/// </summary>
public static class DevcontainerVolumes
{
    /// <summary>
    /// The volume families the devcontainer CLI creates per devcontainerId, from our
    /// <c>.devcontainer/devcontainer.json</c> mounts. Each is <c>{prefix}{devcontainerId}</c>.
    ///
    /// <para><c>dind-var-lib-docker-</c> is the one that matters for disk: it holds the inner
    /// Docker daemon's image store and grew to 50 GB per job on the si14agents host.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> Prefixes = new[]
    {
        "dind-var-lib-docker-",
        "claude-code-config-",
        "claude-code-bashhistory-",
        "gcm-credentials-",
    };

    /// <summary>
    /// The prefix of the workspace-volume family. Deliberately excluded from <see cref="Prefixes"/>:
    /// it is not devcontainerId-keyed, and it is the one family that can hold unpushed work, so it is
    /// never swept by prefix — only removed via the job's own recorded <c>VolumeName</c>, or by an
    /// explicit opt-in on the manual command.
    /// </summary>
    public const string WorkspacePrefix = "devcontainer-";

    /// <summary>
    /// The family holding Claude Code session transcripts. Brain/ASF ingests these, so the automatic
    /// sweeps exclude it by default and only remove it when the caller opts in.
    /// </summary>
    public const string TranscriptPrefix = "claude-code-config-";

    /// <summary>
    /// The four volume names a devcontainer with this id may have created. Names are returned whether
    /// or not the volumes exist — a devcontainer only materialises the mounts its config declares
    /// (observed: <c>nice_shirley</c> had no gcm volume while <c>musing_lichterman</c> did), so
    /// callers must treat "no such volume" as success, not as an error.
    /// </summary>
    /// <param name="includeTranscripts">
    /// When false (the default) <see cref="TranscriptPrefix"/> is omitted, preserving Claude Code
    /// session transcripts for Brain/ASF ingest.
    /// </param>
    public static IReadOnlyList<string> SiblingsFor(string devcontainerId, bool includeTranscripts = false)
    {
        if (string.IsNullOrWhiteSpace(devcontainerId))
        {
            return Array.Empty<string>();
        }

        return Prefixes
            .Where(p => includeTranscripts || !string.Equals(p, TranscriptPrefix, StringComparison.Ordinal))
            .Select(p => p + devcontainerId)
            .ToList();
    }

    /// <summary>
    /// Extracts the devcontainerId from a volume name belonging to one of <see cref="Prefixes"/>.
    /// Returns false for the workspace family and for anything unrecognised, so a caller sweeping a
    /// <c>docker volume ls</c> listing cannot accidentally treat an application's volume as ours.
    /// </summary>
    public static bool TryGetDevcontainerId(string volumeName, out string devcontainerId)
    {
        devcontainerId = string.Empty;
        if (string.IsNullOrWhiteSpace(volumeName))
        {
            return false;
        }

        foreach (var prefix in Prefixes)
        {
            if (volumeName.Length > prefix.Length &&
                volumeName.StartsWith(prefix, StringComparison.Ordinal))
            {
                devcontainerId = volumeName[prefix.Length..];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the name belongs to a family this code owns and may remove — i.e. one of
    /// <see cref="Prefixes"/>. The workspace family returns false: see <see cref="WorkspacePrefix"/>.
    /// </summary>
    public static bool IsReapable(string volumeName) => TryGetDevcontainerId(volumeName, out _);

    /// <summary>True for the transcript family, which the automatic sweeps skip by default.</summary>
    public static bool IsTranscript(string volumeName) =>
        !string.IsNullOrEmpty(volumeName) &&
        volumeName.Length > TranscriptPrefix.Length &&
        volumeName.StartsWith(TranscriptPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True for the workspace family — the volumes that can hold unpushed work. Callers must not
    /// remove these without an explicit opt-in and a dirty-tree check.
    /// </summary>
    public static bool IsWorkspace(string volumeName) =>
        !string.IsNullOrEmpty(volumeName) &&
        volumeName.Length > WorkspacePrefix.Length &&
        volumeName.StartsWith(WorkspacePrefix, StringComparison.Ordinal) &&
        !IsReapable(volumeName);

    /// <summary>
    /// Given the volume names a container has mounted, returns the devcontainerId they imply, or an
    /// empty string when none of them is one of ours. Used to recover the id from a live container
    /// before it is removed — <c>docker inspect</c> reports mounts, not the id-label set the CLI
    /// hashed to produce the id.
    /// </summary>
    public static string DevcontainerIdFromMounts(IEnumerable<string> mountedVolumeNames)
    {
        foreach (var name in mountedVolumeNames ?? Array.Empty<string>())
        {
            if (TryGetDevcontainerId(name, out var id))
            {
                return id;
            }
        }

        return string.Empty;
    }
}
