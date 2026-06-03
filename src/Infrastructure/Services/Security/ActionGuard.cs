using Spectre.Console;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// Default <see cref="IActionGuard"/>. Consults the per-action policy, resolves the enrolled
/// second factor, and challenges the user before a gated action runs. Trust-on-first-use:
/// when nothing is enrolled, every action passes (so the very first <c>pks authenticator init</c>
/// is reachable). Once enrolled, control-plane actions (policy/authenticator/update) and any
/// action toggled on require a code. Satisfied actions are remembered for the lifetime of the
/// process (one CLI invocation) so a command isn't prompted twice, and a composing action
/// (e.g. devcontainer.spawn.remote) implicitly satisfies the actions it declares.
/// </summary>
public sealed class ActionGuard : IActionGuard
{
    private readonly IActionPolicyStore _policy;
    private readonly IActionCatalog _catalog;
    private readonly IEnumerable<ISecondFactor> _factors;
    private readonly IAnsiConsole _console;
    private readonly HashSet<string> _satisfied = new();
    private readonly object _gate = new();

    public ActionGuard(
        IActionPolicyStore policy,
        IActionCatalog catalog,
        IEnumerable<ISecondFactor> factors,
        IAnsiConsole console)
    {
        _policy = policy;
        _catalog = catalog;
        _factors = factors;
        _console = console;
    }

    public async Task RequireAsync(ActionRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_satisfied.Contains(request.ActionId)) return;
        }

        if (!await _policy.IsRequiredAsync(request.ActionId))
        {
            MarkSatisfied(request.ActionId);
            return;
        }

        var factor = await ResolveEnrolledFactorAsync();
        if (factor == null)
        {
            // Two-factor is OPT-IN: with no authenticator enrolled the gate is inert, so existing
            // workflows are unchanged until the operator opts in by enrolling (no breaking change).
            // Enrollment itself is kept off the agent's reach (AuthenticatorInitCommand refuses the
            // sudo path), so the agent can't enable the gate and then satisfy it with its own seed.
            MarkSatisfied(request.ActionId);
            return;
        }

        RenderApprovalPanel(request);
        var result = await factor.ChallengeAsync(request, ct);
        if (!result.Verified)
            throw new ActionGuardDeniedException(request.ActionId, result.Reason ?? "Two-factor verification failed.");

        MarkSatisfied(request.ActionId);
    }

    private void MarkSatisfied(string actionId)
    {
        lock (_gate)
        {
            _satisfied.Add(actionId);
            var def = _catalog.Find(actionId);
            if (def?.Satisfies != null)
                foreach (var sub in def.Satisfies) _satisfied.Add(sub);
        }
    }

    private async Task<ISecondFactor?> ResolveEnrolledFactorAsync()
    {
        foreach (var factor in _factors)
            if (await factor.IsEnrolledAsync()) return factor;
        return null;
    }

    private void RenderApprovalPanel(ActionRequest request)
    {
        var lines = new List<string> { $"[bold]{Markup.Escape(request.Summary)}[/]" };
        if (!string.IsNullOrEmpty(request.CostHint))
            lines.Add($"[yellow]{Markup.Escape(request.CostHint)}[/]");
        lines.Add($"[dim]action: {Markup.Escape(request.ActionId)}[/]");

        _console.Write(new Panel(string.Join("\n", lines))
            .Border(BoxBorder.Rounded)
            .BorderStyle("yellow")
            .Header(" [bold yellow]🔒 Two-factor approval required[/] "));
    }
}
