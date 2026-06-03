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
}

/// <param name="DefaultRequired">Whether two-factor is required for this action out of the box.</param>
/// <param name="Satisfies">Actions implicitly satisfied when this one is approved (composition).</param>
public sealed record ActionDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool DefaultRequired,
    string Category,
    IReadOnlyList<string>? Satisfies = null);

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
        new ActionDefinition(ActionIds.CloudAuthWrite, "Store cloud credentials", "Store or replace Scaleway/Azure/Foundry credentials", true, "Cloud"),
        new ActionDefinition(ActionIds.DevcontainerSpawnRemote, "Spawn remote devcontainer", "Spawn a devcontainer on a remote VM (auto-starts it)", true, "Devcontainer", new[] { ActionIds.VmStart }),
        new ActionDefinition(ActionIds.PksUpdate, "Update pks", "Replace or self-update the pks binary", true, "Control plane"),
        new ActionDefinition(ActionIds.PolicyWrite, "Change 2FA policy", "Change which actions require two-factor", true, "Control plane"),
        new ActionDefinition(ActionIds.AuthenticatorWrite, "Re-enroll authenticator", "Re-enroll or disable the second factor", true, "Control plane"),
    };

    public IReadOnlyList<ActionDefinition> All => Defs;
    public ActionDefinition? Find(string id) => Defs.FirstOrDefault(d => d.Id == id);
}
