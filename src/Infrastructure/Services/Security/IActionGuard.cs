namespace PKS.Infrastructure.Services.Security;

/// <summary>A sensitive operation about to run, presented to the user for approval.</summary>
/// <param name="Resource">
/// Optional opaque key for the thing being acted on (e.g. <c>azure-fileshare:account/share</c>).
/// When set, approval is scoped to that resource instead of the action as a whole, and an
/// out-of-band <c>pks consent</c> request becomes possible for non-interactive callers.
/// </param>
/// <param name="Targets">
/// The exact items the action will touch. Approval binds to this resolved set — never to the
/// pattern that produced it — so items appearing after approval are not covered.
/// </param>
public sealed record ActionRequest(
    string ActionId,
    string Summary,
    string? CostHint = null,
    string? Resource = null,
    IReadOnlyList<string>? Targets = null);

/// <summary>Thrown when a gated action is not approved (wrong code, lockout, or refusal).</summary>
public sealed class ActionGuardDeniedException : Exception
{
    public string ActionId { get; }

    /// <summary>Set when a consent request was filed; the human approves this id out of band.</summary>
    public string? RequestId { get; }

    public ActionGuardDeniedException(string actionId, string message, string? requestId = null) : base(message)
    {
        ActionId = actionId;
        RequestId = requestId;
    }
}

/// <summary>
/// Requires a second factor before a sensitive action runs. Enforcement is keyed on the
/// semantic <see cref="ActionRequest.ActionId"/> (see <see cref="IActionCatalog"/>), so every
/// command that funnels through the same action is caught by a single policy toggle — the
/// guard is invoked at the shared choke-point (e.g. inside the VM provider), not per command.
/// </summary>
public interface IActionGuard
{
    /// <summary>
    /// Throws <see cref="ActionGuardDeniedException"/> if the action requires a second factor
    /// and it is not satisfied. Returns normally when: the action is not gated by policy; no
    /// factor is enrolled yet (trust-on-first-use); an out-of-band consent grant covers this
    /// exact resource and target set; the factor verified; or the same action (or a composing
    /// parent) was already satisfied in this invocation.
    /// </summary>
    Task RequireAsync(ActionRequest request, CancellationToken ct = default);
}
