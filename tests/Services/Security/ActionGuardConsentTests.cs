using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

/// <summary>
/// The resource-scoped route through <see cref="ActionGuard"/>: what happens when a caller with no
/// second factor and no terminal — i.e. an agent — asks for an irreversible action.
/// </summary>
public class ActionGuardConsentTests : IDisposable
{
    private readonly string _dir;
    private readonly ConsentStore _consent;
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

    private const string Resource = "azure-fileshare:acct/share";
    private static readonly string[] Targets = ["reports/a.csv", "reports/b.csv"];

    public ActionGuardConsentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pks-guard-consent-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _consent = new ConsentStore(Path.Combine(_dir, "consent.json"), _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Mock<IActionPolicyStore> Policy(bool required = true)
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

    private ActionGuard Guard(Mock<ISecondFactor> factor, bool required = true)
        => new(Policy(required).Object, new ActionCatalog(), new[] { factor.Object }, new TestConsole(), _consent);

    private static ActionRequest DeleteRequest(IReadOnlyList<string>? targets = null) => new(
        ActionIds.StorageDelete, "delete files", Resource: Resource, Targets: targets ?? Targets);

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task FailClosedAction_NotEnrolled_FilesAConsentRequest_AndDenies()
    {
        // storage.delete opts out of the opt-in fail-open default: with nothing enrolled it must
        // NOT wave through the way vm.start does.
        var factor = Factor(enrolled: false);

        var act = () => Guard(factor).RequireAsync(DeleteRequest());

        var ex = (await act.Should().ThrowAsync<ActionGuardDeniedException>()).Which;
        ex.RequestId.Should().NotBeNullOrEmpty();
        ex.Message.Should().Contain("pks consent approve");

        var filed = await _consent.GetAsync(ex.RequestId!);
        filed.Should().NotBeNull();
        filed!.Targets.Should().BeEquivalentTo(Targets);
        filed.Status.Should().Be(ConsentStatus.Pending);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ApprovedGrant_LetsTheActionThrough_WithoutAChallenge()
    {
        var factor = Factor(enrolled: false);
        var ex = (await ((Func<Task>)(() => Guard(factor).RequireAsync(DeleteRequest())))
            .Should().ThrowAsync<ActionGuardDeniedException>()).Which;

        await _consent.ApproveAsync(ex.RequestId!, uses: 1, TimeSpan.FromMinutes(10));

        // A fresh guard: the grant, not per-process memo state, is what carries the approval.
        await Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest());
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ApprovedGrant_IsSpentAfterOneUse()
    {
        var ex = (await ((Func<Task>)(() => Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest())))
            .Should().ThrowAsync<ActionGuardDeniedException>()).Which;
        await _consent.ApproveAsync(ex.RequestId!, uses: 1, TimeSpan.FromMinutes(10));

        await Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest());

        var again = () => Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest());
        await again.Should().ThrowAsync<ActionGuardDeniedException>("the grant was single-use");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ApprovedGrant_DoesNotCoverAWiderTargetSet()
    {
        var ex = (await ((Func<Task>)(() => Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest())))
            .Should().ThrowAsync<ActionGuardDeniedException>()).Which;
        await _consent.ApproveAsync(ex.RequestId!, uses: 5, TimeSpan.FromMinutes(10));

        var widened = () => Guard(Factor(enrolled: false))
            .RequireAsync(DeleteRequest([.. Targets, "reports/secret.csv"]));

        await widened.Should().ThrowAsync<ActionGuardDeniedException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Enrolled_NoTerminal_FilesAConsentRequest_RatherThanAHardDenial()
    {
        // TestConsole is non-interactive, which is exactly the agent's situation: a factor exists
        // but there is no TTY to type the code into.
        var act = () => Guard(Factor(enrolled: true)).RequireAsync(DeleteRequest());

        var ex = (await act.Should().ThrowAsync<ActionGuardDeniedException>()).Which;
        ex.RequestId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task SatisfiedMemo_IsScopedToTheResource()
    {
        var ex = (await ((Func<Task>)(() => Guard(Factor(enrolled: false)).RequireAsync(DeleteRequest())))
            .Should().ThrowAsync<ActionGuardDeniedException>()).Which;
        await _consent.ApproveAsync(ex.RequestId!, uses: 5, TimeSpan.FromMinutes(10));

        var guard = Guard(Factor(enrolled: false));
        await guard.RequireAsync(DeleteRequest());

        // Same action, same process, a different share: approving one must not cover the other.
        var other = () => guard.RequireAsync(new ActionRequest(
            ActionIds.StorageDelete, "delete files", Resource: "azure-fileshare:acct/other", Targets: Targets));

        await other.Should().ThrowAsync<ActionGuardDeniedException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ActionTurnedOff_SkipsConsentEntirely()
    {
        await Guard(Factor(enrolled: false), required: false).RequireAsync(DeleteRequest());
        (await _consent.ListAsync(includeResolved: true)).Should().BeEmpty();
    }
}
