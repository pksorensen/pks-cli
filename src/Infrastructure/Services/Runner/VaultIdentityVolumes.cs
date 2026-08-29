namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Naming + mount helpers for the stable <c>pks-vault-*</c> Docker volumes that hold one
/// station's pks-agent-vault agent identity (ADR 0011). Sibling of
/// <see cref="ClaudeCredentialVolumes"/> and deliberately not a fourth scope on it: the Claude
/// volume holds an OAuth session that is *meant* to be shared across stations (its default scope
/// is per-project), whereas a vault identity is the one thing that must never be shared, because
/// sharing it hands the credential to the station whose job is reading attacker-controlled
/// content. Different content, different lifetime, different sharing rule.
/// </summary>
public static class VaultIdentityVolumes
{
    /// <summary>
    /// The volume that holds the identity for one station of one assembly line:
    /// <c>pks-vault-{owner}-{project}-{line}-{station}</c>.
    ///
    /// There is no scope parameter, and that is the point. A vault identity is always
    /// station-scoped; a knob here would be a knob for widening the blast radius.
    ///
    /// Returns null when either id is missing, which is how an ad-hoc or legacy dispatch with no
    /// station context ends up with no vault identity at all — the safe answer. A shared fallback
    /// volume would be worse than none: it would silently give some other station's identity to a
    /// job that was never granted anything.
    /// </summary>
    public static string? ResolveVolumeName(string owner, string project, string? assemblyLineId, string? stationId)
    {
        if (string.IsNullOrWhiteSpace(assemblyLineId) || string.IsNullOrWhiteSpace(stationId))
        {
            return null;
        }

        return $"pks-vault-{ClaudeCredentialVolumes.Sanitize(owner)}-{ClaudeCredentialVolumes.Sanitize(project)}"
             + $"-{ClaudeCredentialVolumes.Sanitize(assemblyLineId)}-{ClaudeCredentialVolumes.Sanitize(stationId)}";
    }

    /// <summary>
    /// Where the identity volume is mounted inside a job devcontainer, and the value exported as
    /// <c>VAULT_IDENTITY_DIR</c>.
    ///
    /// Fixed rather than home-relative for the same reason as
    /// <see cref="ClaudeCredentialVolumes.MountTarget"/>: the container's user depends on the
    /// image's <c>USER</c>, so a path under <c>~</c> works for the house images and silently
    /// breaks for stock ones.
    /// </summary>
    public const string MountTarget = "/opt/pks-vault";

    /// <summary>
    /// The identity file itself, inside <see cref="MountTarget"/>. Exported as
    /// <c>AGENTICS_VAULT_IDENTITY</c> — the vault CLI's own override, checked by
    /// <c>agentid.DefaultPath()</c> before it falls back to
    /// <c>~/.config/agentics/vault/identity.json</c>. That fallback is the failure this constant
    /// exists to prevent: a container's home is ephemeral, so a station that resolved to it would
    /// re-enrol on every run and report each fresh, unapproved fingerprint as "no access".
    /// </summary>
    public const string IdentityPath = MountTarget + "/identity.json";

    /// <summary>
    /// The <c>--mount</c> fragment that puts <paramref name="volumeName"/> at
    /// <see cref="MountTarget"/>, with its own leading space so it composes with the other
    /// optional mount fragments. Empty string when no volume was resolved, so the caller can
    /// concatenate unconditionally.
    /// </summary>
    public static string BuildMountArg(string? volumeName) =>
        string.IsNullOrWhiteSpace(volumeName)
            ? string.Empty
            : $" --mount type=volume,source={volumeName},target={MountTarget}";
}
