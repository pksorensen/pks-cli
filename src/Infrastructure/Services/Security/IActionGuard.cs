namespace PKS.Infrastructure.Services.Security;

/// <summary>A sensitive operation about to run, presented to the user for approval.</summary>
public sealed record ActionRequest(string ActionId, string Summary, string? CostHint = null);

/// <summary>Thrown when a gated action is not approved (wrong code, lockout, or refusal).</summary>
public sealed class ActionGuardDeniedException : Exception
{
    public string ActionId { get; }
    public ActionGuardDeniedException(string actionId, string message) : base(message) => ActionId = actionId;
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
    /// factor is enrolled yet (trust-on-first-use); the factor verified; or the same action
    /// (or a composing parent) was already satisfied in this invocation.
    /// </summary>
    Task RequireAsync(ActionRequest request, CancellationToken ct = default);
}
