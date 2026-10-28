using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Where a devcontainer command talks to docker: the local daemon, or a registered SSH target.
/// Owns the materialized pks-held key for the lifetime of the command — dispose it in a <c>finally</c>.
/// </summary>
public sealed class DevcontainerRemoteHost : IDisposable
{
    private readonly MaterializedKey? _materializedKey;

    private DevcontainerRemoteHost(RemoteHostConfig? host, string label, MaterializedKey? materializedKey)
    {
        Host = host;
        Label = label;
        _materializedKey = materializedKey;
    }

    /// <summary>The SSH host to run docker through, or <c>null</c> for the local daemon.</summary>
    public RemoteHostConfig? Host { get; }

    /// <summary>What the output reports as <c>host</c>: the target's label, its hostname, or <c>local</c>.</summary>
    public string Label { get; }

    public bool IsLocal => Host == null;

    public static DevcontainerRemoteHost Local { get; } = new(null, "local", null);

    /// <summary>
    /// Resolves <c>--remote &lt;TARGET&gt;</c> without prompting. <c>local</c> (any case) is the local
    /// daemon; anything else must be a registered target's label or host.
    /// </summary>
    /// <returns>The host, or <c>null</c> with <paramref name="error"/> set (unknown target, guard refused, key unavailable).</returns>
    public static async Task<(DevcontainerRemoteHost? Host, string? Error)> ResolveAsync(
        string remoteSpec,
        ISshTargetConfigurationService? configService,
        ISshKeyStore? keyStore,
        IActionGuard? guard,
        CancellationToken ct = default)
    {
        if (string.Equals(remoteSpec.Trim(), "local", StringComparison.OrdinalIgnoreCase))
            return (Local, null);

        if (configService == null)
            return (null, "SSH targets are not available in this build.");

        var target = await configService.FindTargetAsync(remoteSpec.Trim());
        if (target == null)
            return (null, $"SSH target '{remoteSpec}' not found. Register it with: pks ssh register");

        return await FromTargetAsync(target, keyStore, guard, ct);
    }

    /// <summary>
    /// Turns a chosen target into a usable host: consults the action guard (the same <c>ssh.connect</c>
    /// choke-point as <c>pks ssh run</c>, inert until a factor is enrolled) and materializes a pks-held
    /// key when the target has one instead of a key path.
    /// </summary>
    public static async Task<(DevcontainerRemoteHost? Host, string? Error)> FromTargetAsync(
        SshTarget target,
        ISshKeyStore? keyStore,
        IActionGuard? guard,
        CancellationToken ct = default)
    {
        if (guard != null)
        {
            try
            {
                await guard.RequireAsync(new ActionRequest(ActionIds.SshConnect,
                    $"List devcontainers on {target.Username}@{target.Host}:{target.Port}"), ct);
            }
            catch (ActionGuardDeniedException ex)
            {
                return (null, $"Connection denied: {ex.Message}");
            }
        }

        MaterializedKey? materialized = null;
        var keyPath = target.KeyPath;
        if (!string.IsNullOrEmpty(target.ManagedKeyId))
        {
            if (keyStore == null)
                return (null, "The target uses a pks-held key but no key store is available.");
            try
            {
                materialized = await keyStore.MaterializeAsync(target.ManagedKeyId, ct);
                keyPath = materialized.Path;
            }
            catch (Exception ex)
            {
                return (null, $"Could not access pks-held key: {ex.Message}");
            }
        }

        var host = new RemoteHostConfig
        {
            Host = target.Host,
            Username = target.Username,
            Port = target.Port,
            KeyPath = keyPath,
        };
        var label = string.IsNullOrEmpty(target.Label) ? target.Host : target.Label;
        return (new DevcontainerRemoteHost(host, label, materialized), null);
    }

    public void Dispose() => _materializedKey?.Dispose();
}
