using Spectre.Console;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// TOTP second factor — provider #1. Prompts (masked) for a 6-digit authenticator code or a
/// recovery code and verifies it against <see cref="ITotpSeedStore"/>. Must not be invoked from
/// inside a live <c>AnsiConsole.Status(...)</c> display (Spectre forbids nested interaction);
/// gated call-sites run the underlying operation outside any spinner.
/// </summary>
public sealed class TotpSecondFactor : ISecondFactor
{
    private const int MaxPromptAttempts = 3;

    private readonly ITotpSeedStore _store;
    private readonly IAnsiConsole _console;

    public TotpSecondFactor(ITotpSeedStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public string ProviderKey => "totp";

    public Task<bool> IsEnrolledAsync() => _store.IsEnrolledAsync();

    public async Task<SecondFactorResult> ChallengeAsync(ActionRequest request, CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= MaxPromptAttempts; attempt++)
        {
            var code = _console.Prompt(
                new TextPrompt<string>("[cyan]Authenticator code[/] [dim](6 digits, or a recovery code)[/]:")
                    .PromptStyle("cyan")
                    .Secret()
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(code))
                return SecondFactorResult.Fail("No code entered.");

            var outcome = await _store.VerifyAsync(code.Trim());
            switch (outcome.Status)
            {
                case VerifyStatus.Ok:
                    return SecondFactorResult.Ok();
                case VerifyStatus.NotEnrolled:
                    return SecondFactorResult.Fail("No authenticator is enrolled.");
                case VerifyStatus.LockedOut:
                    return SecondFactorResult.Fail(outcome.Detail ?? "Locked out.");
                default:
                    if (attempt >= MaxPromptAttempts)
                        return SecondFactorResult.Fail(outcome.Detail ?? "Invalid code.");
                    _console.MarkupLine($"[red]{Markup.Escape(outcome.Detail ?? "Invalid code.")}[/] [dim]({MaxPromptAttempts - attempt} attempt(s) left)[/]");
                    break;
            }
        }
        return SecondFactorResult.Fail("Invalid code.");
    }
}
