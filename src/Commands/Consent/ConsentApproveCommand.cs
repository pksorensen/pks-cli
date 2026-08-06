using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

/// <summary>
/// Turn a pending consent request into a short-lived, use-counted grant.
/// </summary>
/// <remarks>
/// Interactivity alone is NOT the boundary — an agent can drive a TTY (tmux). What holds is the
/// combination: the sudo path (the in-container agent's only route to the pks user) is refused,
/// and when an authenticator is enrolled a TOTP code from the human's phone is required. The
/// grant binds to the request's target fingerprint, so approving here can never authorise a
/// different set of files than the one printed above the prompt.
/// </remarks>
[Description("Approve a consent request (interactive; requires the second factor when enrolled)")]
public class ConsentApproveCommand : Command<ConsentApproveCommand.Settings>
{
    private readonly IConsentStore _store;
    private readonly IEnumerable<ISecondFactor> _factors;
    private readonly IAnsiConsole _console;

    public ConsentApproveCommand(IConsentStore store, IEnumerable<ISecondFactor> factors, IAnsiConsole console)
    {
        _store = store;
        _factors = factors;
        _console = console;
    }

    public class Settings : ConsentSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Consent request id")]
        public string Id { get; set; } = string.Empty;

        [CommandOption("--uses")]
        [Description("How many times the grant may be spent (default: 1)")]
        public int Uses { get; set; } = 1;

        [CommandOption("--minutes")]
        [Description("How long the grant stays usable, in minutes (default: 10)")]
        public int Minutes { get; set; } = 10;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // The whole point of the out-of-band route is that the caller who asked cannot also answer.
        if (SecurityContext.IsSudoInvoked)
        {
            _console.MarkupLine("[red]Approval can't run via sudo inside the container[/] (that is the path the agent has).");
            _console.MarkupLine("[dim]Approve from your own terminal, or from the Docker host:\n  docker exec -it -u pks <container> pks consent approve " + Markup.Escape(settings.Id) + "[/]");
            return 1;
        }

        if (Console.IsInputRedirected || !_console.Profile.Capabilities.Interactive)
        {
            _console.MarkupLine("[red]Approval must run in an interactive terminal.[/]");
            return 1;
        }

        var request = await _store.GetAsync(settings.Id);
        if (request == null)
        {
            _console.MarkupLine($"[red]No consent request '{Markup.Escape(settings.Id)}'.[/]");
            _console.MarkupLine("[dim]List them with [bold]pks consent list[/].[/]");
            return 1;
        }

        if (request.Status != ConsentStatus.Pending)
        {
            _console.MarkupLine($"[yellow]Request '{Markup.Escape(request.Id)}' is {request.Status.ToString().ToLowerInvariant()}, not pending.[/]");
            return 1;
        }

        ConsentShowCommand.Render(_console, request);
        _console.WriteLine();

        if (!_console.Confirm($"[yellow]Approve this for {settings.Uses} use(s) over the next {settings.Minutes} minute(s)?[/]", defaultValue: false))
        {
            _console.MarkupLine("[yellow]Not approved.[/] [dim]Use [bold]pks consent deny " + Markup.Escape(request.Id) + "[/] to close it out.[/]");
            return 1;
        }

        var factor = await ResolveEnrolledFactorAsync();
        if (factor != null)
        {
            var challenge = new ActionRequest(
                request.ActionId, request.Summary, Resource: request.Resource, Targets: request.Targets);
            var result = await factor.ChallengeAsync(challenge);
            if (!result.Verified)
            {
                _console.MarkupLine($"[red]Not approved:[/] {Markup.Escape(result.Reason ?? "second factor failed")}");
                return 1;
            }
        }
        else
        {
            _console.MarkupLine("[dim]No authenticator enrolled — approving on terminal trust alone. " +
                                "Run [bold]pks authenticator init[/] to require a code here.[/]");
        }

        var approved = await _store.ApproveAsync(
            request.Id, settings.Uses, TimeSpan.FromMinutes(Math.Max(1, settings.Minutes)));

        _console.MarkupLine(
            $"[green]✓ Approved[/] {Markup.Escape(approved.Id)} — {approved.RemainingUses} use(s), " +
            $"valid until {approved.GrantExpiresUtc?.ToLocalTime():HH:mm}.");
        _console.MarkupLine("[dim]The original command can now be re-run; the grant is spent on first use.[/]");
        return 0;
    }

    private async Task<ISecondFactor?> ResolveEnrolledFactorAsync()
    {
        foreach (var factor in _factors)
            if (await factor.IsEnrolledAsync()) return factor;
        return null;
    }
}
