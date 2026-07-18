using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Bridges <see cref="SshTarget"/> (the persisted registered-target shape, which can carry either a
/// plain <c>KeyPath</c> or a pks-held <c>ManagedKeyId</c>) to <see cref="RemoteHostConfig"/> (the
/// shape <see cref="ISshCommandRunner"/> actually takes). <c>RemoteHostConfig</c> has no
/// <c>ManagedKeyId</c> field of its own, so without this converter a target that stores its key in
/// the pks key store (rather than a plain file path) silently loses its credential the moment it's
/// routed through <see cref="ISshCommandRunner"/> -- see docs/remote-runner-targets-plan.md Phase 4,
/// obstacle (a). Mirrors the materialize/dispose pattern already used inline by
/// <c>Commands/Ssh/SshConnectCommand.cs</c>.
/// </summary>
public static class SshTargetExtensions
{
    /// <summary>
    /// Resolves this target to a <see cref="RemoteHostConfig"/>. When the target uses a pks-held key
    /// (<c>ManagedKeyId</c> set), the key is decrypted to a short-lived 0600 temp file via
    /// <paramref name="keyStore"/> and the returned <see cref="MaterializedKey"/> handle must be
    /// disposed by the caller once the SSH work is done -- disposing it shreds the temp file. When
    /// the target already uses a plain <c>KeyPath</c> (or no key at all), no materialization happens
    /// and the returned handle is <c>null</c> -- there is nothing to clean up.
    /// </summary>
    public static async Task<(RemoteHostConfig Config, MaterializedKey? MaterializedKey)> ToRemoteHostConfigAsync(
        this SshTarget target,
        ISshKeyStore keyStore,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyStore);

        var keyPath = target.KeyPath;
        MaterializedKey? materialized = null;

        if (!string.IsNullOrEmpty(target.ManagedKeyId))
        {
            materialized = await keyStore.MaterializeAsync(target.ManagedKeyId, ct);
            keyPath = materialized.Path;
        }

        var config = new RemoteHostConfig
        {
            Host = target.Host,
            Username = target.Username,
            Port = target.Port,
            KeyPath = keyPath,
        };

        return (config, materialized);
    }
}
