namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// The one place that decides what a runner's tmux session is called. Local detached starts
/// (<c>pks agentics runner start</c>) and SSH handoffs
/// (<see cref="IAgenticsRunnerSshHandoffService.HandoffAsync"/>) deliberately use the *same*
/// name for the same owner/project, so <c>status</c>/<c>logs</c>/<c>stop</c> are one code path
/// with either a local shell or an SSH command runner behind it. Change the shape here and both
/// sides move together.
/// </summary>
public static class RunnerTmuxSession
{
    public static string Name(string owner, string project) =>
        $"pks-agentics-{Sanitize(owner)}-{Sanitize(project)}";

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
