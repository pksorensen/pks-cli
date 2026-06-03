using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Authenticator;

/// <summary>
/// Enroll a TOTP authenticator that gates sensitive actions (see <c>pks actions</c>). The secret
/// and recovery codes are shown exactly ONCE and never again — the stored seed can only be used
/// to verify a code, never read back, which is what lets the gate hold against an agent that can
/// run pks but cannot read pks's files. Trust-on-first-use: the first enrollment is open; any
/// later re-enroll requires a current code (the <c>authenticator.write</c> action).
/// </summary>
[Description("Enroll a TOTP authenticator (shows QR data, secret and recovery codes once)")]
public class AuthenticatorInitCommand : Command<AuthenticatorInitCommand.Settings>
{
    private readonly ITotpSeedStore _store;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public AuthenticatorInitCommand(ITotpSeedStore store, IActionGuard guard, IAnsiConsole console)
    {
        _store = store;
        _guard = guard;
        _console = console;
    }

    public class Settings : AuthenticatorSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        // Enrollment must come from a context the in-container agent cannot reach. The agent can
        // only run pks via `sudo -u pks` (SUDO_USER set); a trusted host (`docker exec -u pks`) or
        // build-time (root) enrollment does not. Refusing the sudo path stops the agent from
        // enrolling its OWN authenticator and from capturing the generated secret off pks's stdout.
        if (SecurityContext.IsSudoInvoked)
        {
            _console.MarkupLine("[red]Enrollment can't run via sudo inside the container[/] (an agent could capture the secret).");
            _console.MarkupLine("[dim]" + Markup.Escape(SecurityContext.HostEnrollmentHint) + "[/]");
            return 1;
        }

        // The secret is shown once and must not be captured to a file — require a real terminal.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            _console.MarkupLine("[red]Enrollment must run in an interactive terminal[/] (use [cyan]docker exec -it -u pks …[/]).");
            return 1;
        }

        // Self-lock: if already enrolled, re-enrolling requires the current factor. (First
        // enrollment from a trusted, non-sudo context is open — trust-on-first-use.)
        if (await _store.IsEnrolledAsync())
        {
            _console.MarkupLine("[yellow]An authenticator is already enrolled.[/] Re-enrolling will replace it and invalidate the old codes.");
            try
            {
                await _guard.RequireAsync(new ActionRequest(
                    ActionIds.AuthenticatorWrite,
                    "Re-enroll the authenticator (replaces your current two-factor)"));
            }
            catch (ActionGuardDeniedException ex)
            {
                _console.MarkupLine($"[red]Re-enrollment denied:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        var secret = TotpService.GenerateSecretBase32();
        var account = $"{Environment.UserName}@{Environment.MachineName}";
        var otpauth = TotpService.BuildOtpAuthUri(secret, account);
        var recovery = TotpService.GenerateRecoveryCodes();

        var grouped = string.Join("   ", recovery);
        var body = string.Join("\n", new[]
        {
            "[bold]Add this to your authenticator app[/] (Google Authenticator, 1Password, Authy, …):",
            string.Empty,
            $"  [cyan]Setup URI:[/] {Markup.Escape(otpauth)}",
            $"  [cyan]Manual secret:[/] [bold]{Markup.Escape(secret)}[/]   [dim](SHA1, 6 digits, 30s)[/]",
            string.Empty,
            "[bold]Recovery codes[/] [dim](each usable once if you lose your phone)[/]:",
            $"  {Markup.Escape(grouped)}",
            string.Empty,
            "[yellow]This is the only time the secret and recovery codes are shown. Store them now.[/]",
        });

        _console.Write(new Panel(body)
            .Border(BoxBorder.Rounded)
            .BorderStyle("cyan")
            .Header(" [bold cyan]Authenticator enrollment[/] "));

        // Confirm the user successfully added it by entering a live code (verified against the
        // freshly generated secret — nothing is persisted until this succeeds).
        var entered = _console.Prompt(
            new TextPrompt<string>("[cyan]Enter the 6-digit code from your app to confirm:[/]")
                .PromptStyle("cyan")
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]A code is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        if (!VerifyFreshCode(secret, entered))
        {
            _console.MarkupLine("[red]That code didn't match. Enrollment cancelled — nothing was saved.[/]");
            return 1;
        }

        await _store.EnrollAsync(new TotpEnrollment(secret, recovery));

        _console.MarkupLine("[green]✓ Authenticator enrolled.[/] Sensitive actions now require a code.");
        _console.MarkupLine("[dim]Review which actions are gated with [bold]pks actions[/].[/]");
        return 0;
    }

    private static bool VerifyFreshCode(string secret, string code)
    {
        var step = TotpService.TimeStep(DateTimeOffset.UtcNow);
        for (long s = step - 1; s <= step + 1; s++)
            if (TotpService.CodesEqual(code, TotpService.ComputeCode(secret, s)))
                return true;
        return false;
    }
}
