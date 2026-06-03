using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

public class ActionGuardTests
{
    private static Mock<IActionPolicyStore> Policy(bool required)
    {
        var p = new Mock<IActionPolicyStore>();
        p.Setup(x => x.IsRequiredAsync(It.IsAny<string>())).ReturnsAsync(required);
        return p;
    }

    private static Mock<ISecondFactor> Factor(bool enrolled, bool verifies = true)
    {
        var f = new Mock<ISecondFactor>();
        f.SetupGet(x => x.ProviderKey).Returns("totp");
        f.Setup(x => x.IsEnrolledAsync()).ReturnsAsync(enrolled);
        f.Setup(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(verifies ? SecondFactorResult.Ok() : SecondFactorResult.Fail("bad code"));
        return f;
    }

    private static ActionGuard Guard(Mock<IActionPolicyStore> policy, Mock<ISecondFactor> factor)
        => new(policy.Object, new ActionCatalog(), new[] { factor.Object }, new TestConsole());

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ActionOff_DoesNotChallenge()
    {
        var factor = Factor(enrolled: true);
        await Guard(Policy(required: false), factor).RequireAsync(new ActionRequest(ActionIds.VmStart, "start"));
        factor.Verify(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IDisposable WithSudoEnv(string? sudoUser)
    {
        var prevUser = Environment.GetEnvironmentVariable("SUDO_USER");
        var prevUid = Environment.GetEnvironmentVariable("SUDO_UID");
        Environment.SetEnvironmentVariable("SUDO_USER", sudoUser);
        Environment.SetEnvironmentVariable("SUDO_UID", sudoUser == null ? null : "1000");
        return new Restore(() =>
        {
            Environment.SetEnvironmentVariable("SUDO_USER", prevUser);
            Environment.SetEnvironmentVariable("SUDO_UID", prevUid);
        });
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _a;
        public Restore(Action a) => _a = a;
        public void Dispose() => _a();
    }

    [Theory]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    [InlineData(null)]   // genuine workstation, no sudo isolation
    [InlineData("node")] // in-container sudo path
    public async Task NotEnrolled_FailsOpen_OptIn(string? sudoUser)
    {
        // Two-factor is opt-in: with nothing enrolled the gate is inert regardless of how pks was
        // invoked, so existing workflows are unchanged (no breaking change). Enrollment is what
        // turns protection on, and the agent can't enroll (AuthenticatorInitCommand refuses sudo).
        using var _ = WithSudoEnv(sudoUser);
        var factor = Factor(enrolled: false);
        await Guard(Policy(required: true), factor).RequireAsync(new ActionRequest(ActionIds.VmStart, "start"));
        factor.Verify(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task RequiredAndEnrolled_ValidCode_Passes()
    {
        var factor = Factor(enrolled: true, verifies: true);
        await Guard(Policy(required: true), factor).RequireAsync(new ActionRequest(ActionIds.VmStart, "start"));
        factor.Verify(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task RequiredAndEnrolled_InvalidCode_Throws()
    {
        var guard = Guard(Policy(required: true), Factor(enrolled: true, verifies: false));
        var act = () => guard.RequireAsync(new ActionRequest(ActionIds.VmStart, "start"));
        await act.Should().ThrowAsync<ActionGuardDeniedException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task SameAction_OnlyChallengedOncePerInvocation()
    {
        var factor = Factor(enrolled: true);
        var guard = Guard(Policy(required: true), factor);
        await guard.RequireAsync(new ActionRequest(ActionIds.VmStart, "start"));
        await guard.RequireAsync(new ActionRequest(ActionIds.VmStart, "start again"));
        factor.Verify(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ComposingAction_SatisfiesNested_NoSecondChallenge()
    {
        // devcontainer.spawn.remote declares it satisfies vm.start.
        var factor = Factor(enrolled: true);
        var guard = Guard(Policy(required: true), factor);
        await guard.RequireAsync(new ActionRequest(ActionIds.DevcontainerSpawnRemote, "spawn"));
        await guard.RequireAsync(new ActionRequest(ActionIds.VmStart, "auto-start"));
        factor.Verify(x => x.ChallengeAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
