namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Removes the containers and volumes a devcontainer job leaves behind, across every runner surface
/// (<c>pks agentics runner</c>, <c>pks github runner</c>, and anything added later).
///
/// <para>This exists because the previous reaper leaked in three independent ways: it discovered
/// containers by one label scheme out of two, it removed containers without their volumes, and it was
/// never called from <c>runner start</c>, so a runner killed by a reboot cleaned up nothing at all.
/// On the si14agents Coolify host that filled the root filesystem to 90%.</para>
/// </summary>
public interface IRunnerReaper
{
    /// <summary>
    /// Resolves what a reap would remove, without removing anything. Backs both <c>--dry-run</c> and
    /// the execute path, so the two can never disagree about scope.
    /// </summary>
    Task<ReapPlan> PlanAsync(ReapOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Plans and then executes. Individual failures are collected into
    /// <see cref="ReapResult.Failures"/> rather than thrown: a reap runs at <c>runner start</c>, and
    /// a volume it cannot remove must not stop the runner from coming up.
    /// </summary>
    Task<ReapResult> ReapAsync(ReapOptions options, CancellationToken cancellationToken = default);
}
