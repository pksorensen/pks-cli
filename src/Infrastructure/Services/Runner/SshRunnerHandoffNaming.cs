namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure name-collision check for the SSH handoff (docs/remote-runner-targets-plan.md Phase 4, work
/// item 4). The server's <c>POST .../runners</c> endpoint upserts by <c>name</c> and rotates the
/// token in place on every call -- so registering the handed-off runner under a name that's already
/// in use (the operator's own machine, or any other live runner for this project) would silently
/// invalidate that runner's token out from under it. This is a hard refusal, not a warning: there is
/// no safe way to proceed once a collision is detected, so callers must pick a different name (or
/// let the operator choose one) before calling the registration endpoint at all.
/// </summary>
public static class SshRunnerHandoffNaming
{
    /// <summary>
    /// True when <paramref name="candidateName"/> collides with either the local machine's own
    /// runner name (<c>Dns.GetHostName()</c> -- what <c>ResolveOrRegisterAsync</c> auto-registers
    /// under) or any name already present in <paramref name="existingServerRunnerNames"/> (the
    /// project's current <c>GET .../runners</c> listing). Comparison is ordinal, case-insensitive --
    /// matching every other name comparison against server-issued runner names in this codebase.
    /// </summary>
    public static bool IsCollision(
        string candidateName,
        string localHostName,
        IEnumerable<string> existingServerRunnerNames)
    {
        ArgumentNullException.ThrowIfNull(candidateName);
        ArgumentNullException.ThrowIfNull(localHostName);
        ArgumentNullException.ThrowIfNull(existingServerRunnerNames);

        if (string.Equals(candidateName, localHostName, StringComparison.OrdinalIgnoreCase))
            return true;

        return existingServerRunnerNames.Any(name =>
            string.Equals(name, candidateName, StringComparison.OrdinalIgnoreCase));
    }
}
