namespace PKS.Infrastructure.Services.Brain;

/// A directory holding a *copy* of some agent's home, rescued from somewhere the
/// ordinary sources cannot reach — today, a docker volume pulled out by
/// `pks brain docker backup`.
///
/// <param name="Path">
/// The rescued volume directory, e.g.
/// `/workspaces/bulkdata/brain-docker-volumes/claude-code-config-abc123`. Each
/// source works out its own sub-path underneath (Claude's `projects/`, Codex's
/// `sessions/`, opencode's `opencode.db`) — the registry stores the volume root
/// and stays ignorant of tool layout.
/// </param>
/// <param name="Origin">
/// Stamped onto every event read from here as <see cref="Asf.AsfEvent.Origin"/>,
/// so the platform can split "what did I do in containers" out of the totals
/// later. `docker:&lt;volume name&gt;`.
/// </param>
public sealed record BrainSessionRoot(
    string Path,
    string Origin,
    DateTimeOffset AddedUtc,
    string? Note = null);

/// Where sessions live besides this machine's own tool directories.
///
/// The registry is append-mostly on purpose. Its entries routinely point at
/// `/workspaces/bulkdata`, which is a host-injected mount that disappears on a
/// container recreate and comes back when someone re-runs the mount script — and
/// worse, the *empty mount point* survives on the underlying disk, so a missing
/// root is indistinguishable from an empty one without checking. A registry that
/// self-pruned would quietly forget 28 rescued volumes the first time it ran
/// while the mount was down, and nothing would ever put them back.
///
/// So: <see cref="Usable"/> skips what it cannot read right now, silently and
/// without complaint, and only an explicit <see cref="Remove"/> takes an entry
/// out for good.
public interface IBrainRootRegistry
{
    /// `~/.pks-cli/brain/roots.json`.
    string RegistryPath { get; }

    /// Everything registered, present or not. For `brain sources`, which should
    /// show a root that is currently unreachable rather than hide it.
    IReadOnlyList<BrainSessionRoot> All();

    /// The subset that exists and is non-empty right now. What the sources read.
    IReadOnlyList<BrainSessionRoot> Usable();

    /// Upsert by path. Returns true when the path was not already registered.
    bool Add(BrainSessionRoot root);

    /// Returns how many of these were new.
    int AddRange(IEnumerable<BrainSessionRoot> roots);

    /// Returns true when something was removed.
    bool Remove(string path);
}
