using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="CredentialForwardConsent"/>'s pure prompt-text builder
/// (docs/remote-runner-targets-plan.md Phase 5, work item 3, decision D3). The prompt must say
/// plainly, not imply, when the two-factor gate behind it is currently inert (no authenticator
/// enrolled) -- this is the honesty requirement the GOAL calls out explicitly.
/// </summary>
public class CredentialForwardConsentTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildPrompt_NoFactorEnrolled_StatesGateIsInert_AndOffersInit()
    {
        var prompt = CredentialForwardConsent.BuildPrompt("GitHub token", secondFactorEnrolled: false);

        prompt.Should().Contain("GitHub token");
        prompt.Should().ContainAny("not gated", "NOT actually gated", "no second factor", "isn't gated");
        prompt.Should().Contain("pks authenticator init");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildPrompt_FactorEnrolled_DoesNotClaimInertGate_AndDoesNotSuggestInit()
    {
        var prompt = CredentialForwardConsent.BuildPrompt("Foundry credentials", secondFactorEnrolled: true);

        prompt.Should().Contain("Foundry credentials");
        prompt.Should().NotContain("pks authenticator init");
        prompt.Should().NotContainAny("not gated", "NOT actually gated", "isn't gated");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildPrompt_MentionsZeroSixHundredPermissions()
    {
        CredentialForwardConsent.BuildPrompt("GitHub token", secondFactorEnrolled: true)
            .Should().Contain("0600");
    }
}
