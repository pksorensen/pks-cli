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
/// <remarks>
/// Resource-scoped requests (<see cref="ActionRequest.Resource"/> set) add a second route to
/// approval: an out-of-band consent request that a human resolves with <c>pks consent approve</c>.
/// That route exists because the in-band TOTP prompt is unreachable without a TTY — which is
/// exactly the case for an agent — and because "approved" must mean approved for <em>these
/// targets</em>, not for the action in general.
/// </remarks>
public sealed class ActionGuard : IActionGuard
{
    /// <summary>How long an unapproved request stays approvable.</summary>
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(15);

    private readonly IActionPolicyStore _policy;
    private readonly IActionCatalog _catalog;
    private readonly IEnumerable<ISecondFactor> _factors;
    private readonly IAnsiConsole _console;
    private readonly IConsentStore? _consent;
    private readonly HashSet<string> _satisfied = new();
    private readonly object _gate = new();

    public ActionGuard(
        IActionPolicyStore policy,
        IActionCatalog catalog,
        IEnumerable<ISecondFactor> factors,
        IAnsiConsole console,
        IConsentStore? consent = null)
    {
        _policy = policy;
        _catalog = catalog;
        _factors = factors;
        _console = console;
        _consent = consent;
    }

    public async Task RequireAsync(ActionRequest request, CancellationToken ct = default)
    {
        // Scope the per-process memo to the resource: approving a delete for one share must not
        // silently cover a second share touched later in the same invocation.
        var key = SatisfiedKey(request.ActionId, request.Resource);

        lock (_gate)
        {
            if (_satisfied.Contains(key)) return;
        }

        if (!await _policy.IsRequiredAsync(request.ActionId))
        {
            MarkSatisfied(request);
            return;
        }

        var definition = _catalog.Find(request.ActionId);
        var scoped = request.Resource != null && request.Targets is { Count: > 0 } && _consent != null;
        var fingerprint = scoped ? ConsentStore.Fingerprint(request.Targets!) : null;

        // An out-of-band grant, if one covers this exact resource and target set.
        if (scoped && await _consent!.TryConsumeAsync(request.ActionId, request.Resource!, fingerprint!, ct))
        {
            _console.MarkupLine($"[green]✓[/] [dim]Using approved consent grant for {Markup.Escape(request.ActionId)}.[/]");
            MarkSatisfied(request);
            return;
        }

        var factor = await ResolveEnrolledFactorAsync();
        var interactive = _console.Profile.Capabilities.Interactive;

        if (factor == null)
        {
            // Two-factor is OPT-IN: with no authenticator enrolled the gate is inert, so existing
            // workflows are unchanged until the operator opts in by enrolling (no breaking change).
            // Enrollment itself is kept off the agent's reach (AuthenticatorInitCommand refuses the
            // sudo path), so the agent can't enable the gate and then satisfy it with its own seed.
            // FailClosed actions opt out of that leniency and demand explicit consent instead.
            if (definition?.FailClosed != true)
            {
                MarkSatisfied(request);
                return;
            }

            if (scoped)
                throw await RequestConsentAsync(request, ct);

            if (!interactive)
                throw new ActionGuardDeniedException(request.ActionId,
                    $"'{request.ActionId}' requires approval and cannot run without a terminal.");

            RenderApprovalPanel(request);
            if (!_console.Confirm("[yellow]Proceed?[/]", defaultValue: false))
                throw new ActionGuardDeniedException(request.ActionId, "Declined.");

            MarkSatisfied(request);
            return;
        }

        // A factor is enrolled but there is no terminal to challenge on — file a consent request
        // rather than failing with a bare denial the caller can do nothing about.
        if (scoped && !interactive)
            throw await RequestConsentAsync(request, ct);

        RenderApprovalPanel(request);
        var result = await factor.ChallengeAsync(request, ct);
        if (!result.Verified)
            throw new ActionGuardDeniedException(request.ActionId, result.Reason ?? "Two-factor verification failed.");

        MarkSatisfied(request);
    }

    /// <summary>File (or re-use) a pending request and build the denial that tells the human what to run.</summary>
    private async Task<ActionGuardDeniedException> RequestConsentAsync(ActionRequest request, CancellationToken ct)
    {
        var pending = await _consent!.CreateAsync(
            request.ActionId, request.Resource!, request.Summary, request.Targets!, PendingTtl, ct);

        var message =
            $"Approval required for '{request.ActionId}' on {request.Resource}.\n" +
            $"{request.Targets!.Count} target(s). Request id: {pending.Id}\n\n" +
            $"A human must approve this from an interactive terminal:\n" +
            $"  pks consent approve {pending.Id}\n\n" +
            $"Review it first with:  pks consent show {pending.Id}\n" +
            $"The request expires {pending.ExpiresUtc.ToLocalTime():HH:mm}.";

        return new ActionGuardDeniedException(request.ActionId, message, pending.Id);
    }

    private static string SatisfiedKey(string actionId, string? resource) => $"{actionId}\u0000{resource ?? string.Empty}";

    private void MarkSatisfied(ActionRequest request)
    {
        lock (_gate)
        {
            _satisfied.Add(SatisfiedKey(request.ActionId, request.Resource));
            var def = _catalog.Find(request.ActionId);
            if (def?.Satisfies != null)
                foreach (var sub in def.Satisfies) _satisfied.Add(SatisfiedKey(sub, request.Resource));
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
        if (!string.IsNullOrEmpty(request.Resource))
            lines.Add($"[dim]resource: {Markup.Escape(request.Resource)}[/]");
        if (request.Targets is { Count: > 0 })
            lines.Add($"[dim]targets: {request.Targets.Count}[/]");
        lines.Add($"[dim]action: {Markup.Escape(request.ActionId)}[/]");

        _console.Write(new Panel(string.Join("\n", lines))
            .Border(BoxBorder.Rounded)
            .BorderStyle("yellow")
            .Header(" [bold yellow]🔒 Two-factor approval required[/] "));
    }
}
