using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

public static class QueuedWorkflowRunExtensions
{
    /// <summary>
    /// Returns the branch that should be used when looking up Coolify apps for this run.
    /// For <c>pull_request</c> events, <c>HeadBranch</c> is the PR source ref (e.g.
    /// <c>release-please--branches--main</c>) which never matches an app bound to the deploy
    /// target — so the PR base ref is used instead. For all other events, and as a defensive
    /// fallback when no base ref is available, <c>HeadBranch</c> is returned unchanged.
    /// </summary>
    public static string GetCoolifyLookupBranch(this QueuedWorkflowRun run)
    {
        if (string.Equals(run.Event, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            var baseRef = run.PullRequests.FirstOrDefault()?.Base.Ref;
            if (!string.IsNullOrEmpty(baseRef))
            {
                return baseRef;
            }
        }
        return run.HeadBranch;
    }
}
