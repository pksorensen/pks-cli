namespace PKS.Infrastructure.Services.Security;

/// <summary>Stable identifiers for gateable actions. Referenced at each choke-point.</summary>
public static class ActionIds
{
    public const string VmCreate = "vm.create";
    public const string VmStart = "vm.start";
    public const string VmStop = "vm.stop";
    public const string VmDestroy = "vm.destroy";
    public const string VmAutoshutdownWrite = "vm.autoshutdown.write";
    public const string CloudAuthWrite = "cloud.auth.write";
    public const string DevcontainerSpawnRemote = "devcontainer.spawn.remote";
    public const string PksUpdate = "pks.update";
    public const string PolicyWrite = "policy.write";
    public const string AuthenticatorWrite = "authenticator.write";
    public const string SshConnect = "ssh.connect";
    public const string CertWrite = "cert.write";
    public const string RunnerCredentialForward = "runner.credential.forward";
    public const string StorageDelete = "storage.delete";
}

/// <param name="DefaultRequired">Whether two-factor is required for this action out of the box.</param>
/// <param name="Satisfies">Actions implicitly satisfied when this one is approved (composition).</param>
/// <param name="FailClosed">
/// When true, an un-enrolled second factor does NOT wave the action through: approval falls back
/// to an out-of-band <c>pks consent approve</c>. Set for irreversible actions where the opt-in
/// fail-open default would be the wrong trade.
/// </param>
public sealed record ActionDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool DefaultRequired,
    string Category,
    IReadOnlyList<string>? Satisfies = null,
    bool FailClosed = false);

public interface IActionCatalog
{
    IReadOnlyList<ActionDefinition> All { get; }
    ActionDefinition? Find(string id);
}

/// <summary>
/// The set of actions that two-factor can guard. New gateable operations are added here and
/// referenced from their choke-point; `pks actions` toggles them and the policy store records
/// per-action state, defaulting to <see cref="ActionDefinition.DefaultRequired"/>.
/// </summary>
public sealed class ActionCatalog : IActionCatalog
{
    private static readonly IReadOnlyList<ActionDefinition> Defs = new[]
    {
        new ActionDefinition(ActionIds.VmCreate, "Create VM", "Provision a new cloud VM (incurs cost)", true, "Compute"),
        new ActionDefinition(ActionIds.VmStart, "Start VM", "Power on a stopped VM (resumes billing)", true, "Compute"),
        new ActionDefinition(ActionIds.VmStop, "Stop VM", "Deallocate / power off a VM", false, "Compute"),
        new ActionDefinition(ActionIds.VmDestroy, "Destroy VM", "Permanently delete a VM and its resources", true, "Compute"),
        new ActionDefinition(ActionIds.VmAutoshutdownWrite, "Change auto-shutdown", "Modify idle/scheduled shutdown policy", false, "Compute"),
        new ActionDefinition(ActionIds.CloudAuthWrite, "Store cloud credentials", "Store or replace Scaleway/Moonshot/Azure/Foundry credentials", true, "Cloud"),
        new ActionDefinition(ActionIds.DevcontainerSpawnRemote, "Spawn remote devcontainer", "Spawn a devcontainer on a remote VM (auto-starts it)", true, "Devcontainer", new[] { ActionIds.VmStart }),
        new ActionDefinition(ActionIds.PksUpdate, "Update pks", "Replace or self-update the pks binary", true, "Control plane"),
        new ActionDefinition(ActionIds.PolicyWrite, "Change 2FA policy", "Change which actions require two-factor", true, "Control plane"),
        new ActionDefinition(ActionIds.AuthenticatorWrite, "Re-enroll authenticator", "Re-enroll or disable the second factor", true, "Control plane"),
        new ActionDefinition(ActionIds.SshConnect, "Open SSH session", "Connect to a registered SSH host using a pks-held key", true, "Access"),
        new ActionDefinition(ActionIds.CertWrite, "Create/replace signing cert", "Create or replace a pks-held code-signing certificate", true, "Control plane"),
        new ActionDefinition(ActionIds.RunnerCredentialForward, "Forward credential to SSH target", "Copy a local credential (GitHub token, Foundry credentials) to a remote SSH target's config, 0600", true, "Access"),
        new ActionDefinition(ActionIds.StorageDelete, "Delete storage files", "Permanently delete files from a storage share (no recycle bin)", true, "Storage", null, FailClosed: true),
    };

    public IReadOnlyList<ActionDefinition> All => Defs;
    public ActionDefinition? Find(string id) => Defs.FirstOrDefault(d => d.Id == id);
}
