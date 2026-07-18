namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure prompt-text builder for the opt-in, per-file credential-forwarding consent prompts
/// (docs/remote-runner-targets-plan.md Phase 5, work item 3, decision D3). Kept separate from the
/// interactive Confirm()/IActionGuard plumbing in AgenticsRunnerStartCommand so the exact wording
/// -- especially the "the gate is inert" honesty requirement -- is directly unit-testable: with
/// no authenticator enrolled, <see cref="Security.ActionGuard.RequireAsync"/> silently auto-satisfies
/// every gated action (trust-on-first-use), so a prompt that merely says "this is gated" would be a
/// false security guarantee. The prompt must say so plainly instead, and point at
/// <c>pks authenticator init</c> as the way to make the gate real.
/// </summary>
public static class CredentialForwardConsent
{
    /// <summary>Builds the operator-facing confirm-prompt text for forwarding one credential file.</summary>
    public static string BuildPrompt(string fileLabel, bool secondFactorEnrolled)
    {
        var gateNote = secondFactorEnrolled
            ? "This is gated by a two-factor prompt."
            : "No authenticator is enrolled, so this action is NOT actually gated by a second factor right now -- " +
              "confirming below is the only check standing between you and the copy. Enroll one with " +
              "[bold]pks authenticator init[/] if you want a real gate.";

        return $"Forward {fileLabel} to this target, written 0600 (mirrors your machine's own copy)? {gateNote}";
    }
}
