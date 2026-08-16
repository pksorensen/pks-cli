namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure naming + remote-detection helpers for the stable <c>pks-claude-*</c> Docker volumes that
/// hold a devcontainer's <c>~/.claude</c> OAuth credentials (docs/remote-runner-targets-plan.md
/// Phase 5, work item 1). The naming rules are the single source of truth for
/// <c>AgenticsRunnerStartCommand.PatchDevcontainerVolumes</c>, which delegates here, and for the
/// SSH-handoff pre-flight / <c>runner status</c> / <c>runner claude-login</c> commands, so all
/// four call sites always agree on which volume a given owner/project/task/scope maps to.
/// </summary>
public static class ClaudeCredentialVolumes
{
    /// <summary>
    /// Resolves the stable volume name per ADR 0004's three scopes:
    /// <list type="bullet">
    ///   <item>"task" → pks-claude-{owner}-{project}-task-{taskId} (full isolation, OAuth per task)</item>
    ///   <item>"project" (default) → pks-claude-{owner}-{project} (shared across tasks of one project)</item>
    ///   <item>"runner" → pks-claude-{owner} (shared across all the operator's projects)</item>
    /// </list>
    /// An unrecognized scope, or "task" without a <paramref name="taskId"/>, falls back to "project".
    /// </summary>
    public static string ResolveVolumeName(string owner, string project, string? taskId, string? scope)
    {
        var effectiveScope = scope?.ToLowerInvariant() switch
        {
            "task" or "project" or "runner" => scope!.ToLowerInvariant(),
            _ => "project",
        };

        return effectiveScope switch
        {
            "task" when !string.IsNullOrEmpty(taskId) =>
                $"pks-claude-{Sanitize(owner)}-{Sanitize(project)}-task-{Sanitize(taskId)}",
            "runner" => $"pks-claude-{Sanitize(owner)}",
            _ => $"pks-claude-{Sanitize(owner)}-{Sanitize(project)}",
        };
    }

    /// <summary>
    /// Where the credentials volume is mounted inside a job devcontainer, and the value exported as
    /// <c>CLAUDE_CONFIG_DIR</c> so the agent looks there.
    ///
    /// Deliberately NOT <c>~/.claude</c>: the agent's home depends on which user the container ends
    /// up running as, which in turn depends on the image's <c>USER</c> — root for stock devcontainer
    /// images, <c>node</c> for the house ones. A fixed path makes the mount work for every image and
    /// every user, instead of only for the ones whose home happens to be <c>/home/node</c>.
    /// </summary>
    public const string MountTarget = "/opt/pks-claude";

    /// <summary>
    /// The <c>--mount</c> fragment that puts <paramref name="volumeName"/> at
    /// <see cref="MountTarget"/>, ready to be appended to a <c>devcontainer up</c> command line.
    /// Includes its own leading space so it composes with the other optional mount fragments.
    /// Returns an empty string when no volume was resolved, so the caller can concatenate it
    /// unconditionally.
    /// </summary>
    public static string BuildMountArg(string? volumeName) =>
        string.IsNullOrWhiteSpace(volumeName)
            ? string.Empty
            : $" --mount type=volume,source={volumeName},target={MountTarget}";

    internal static string Sanitize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]", "-");

    private const string PresentMarker = "PKS_CLAUDE_VOLUME_PRESENT";
    private const string MissingMarker = "PKS_CLAUDE_VOLUME_MISSING";

    /// <summary>
    /// Remote shell command that prints <see cref="PresentMarker"/> or <see cref="MissingMarker"/>
    /// depending on whether <paramref name="volumeName"/> exists as a Docker volume on the target.
    /// Never emits a double quote -- see <see cref="ParseDetectOutput"/> callers /
    /// <c>SshRunnerProbe.BuildProbeCommand</c> for why that matters.
    /// </summary>
    public static string BuildDetectCommand(string volumeName) =>
        $"docker volume inspect {volumeName} >/dev/null 2>&1 && echo {PresentMarker} || echo {MissingMarker}";

    /// <summary>Parses <see cref="BuildDetectCommand"/>'s stdout. Any output that isn't recognizably
    /// the present-marker (including empty/garbled output) is treated as "not present" -- a probe
    /// failure should read as a warning, not a false-positive "credentials are ready".</summary>
    public static bool ParseDetectOutput(string stdout) =>
        stdout.Contains(PresentMarker, StringComparison.Ordinal);
}
