using FluentAssertions;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

/// <summary>
/// The consent store is what makes "approved" mean approved *for these targets, once, for now*.
/// These tests pin the three properties that carry the security weight: the target fingerprint,
/// the use count, and the two expiries.
/// </summary>
public class ConsentStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero));

    private const string Resource = "azure-fileshare:acct/share";
    private static readonly string[] Targets = ["reports/a.csv", "reports/b.csv"];

    public ConsentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pks-consent-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "consent.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private ConsentStore NewStore() => new(_path, _time);

    private Task<ConsentRequest> CreateAsync(ConsentStore store, IReadOnlyList<string>? targets = null)
        => store.CreateAsync(ActionIds.StorageDelete, Resource, "delete files", targets ?? Targets, TimeSpan.FromMinutes(15));

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Approve_ThenConsume_Succeeds_Once()
    {
        var store = NewStore();
        var request = await CreateAsync(store);
        var fingerprint = ConsentStore.Fingerprint(Targets);

        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, fingerprint))
            .Should().BeFalse("a pending request is not yet a grant");

        await store.ApproveAsync(request.Id, uses: 1, TimeSpan.FromMinutes(10));

        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, fingerprint)).Should().BeTrue();
        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, fingerprint))
            .Should().BeFalse("a single-use grant is spent");

        (await store.GetAsync(request.Id))!.Status.Should().Be(ConsentStatus.Consumed);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Grant_DoesNotCover_ADifferentTargetSet()
    {
        // The attack this closes: approve a delete of two files, then add a third before executing.
        var store = NewStore();
        var request = await CreateAsync(store);
        await store.ApproveAsync(request.Id, uses: 1, TimeSpan.FromMinutes(10));

        var widened = ConsentStore.Fingerprint([.. Targets, "reports/secret.csv"]);

        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, widened)).Should().BeFalse();
        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, ConsentStore.Fingerprint(Targets)))
            .Should().BeTrue("the original set is still covered");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Grant_DoesNotCover_ADifferentResource()
    {
        var store = NewStore();
        var request = await CreateAsync(store);
        await store.ApproveAsync(request.Id, uses: 1, TimeSpan.FromMinutes(10));

        (await store.TryConsumeAsync(ActionIds.StorageDelete, "azure-fileshare:acct/other", ConsentStore.Fingerprint(Targets)))
            .Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Grant_ExpiresWithTime()
    {
        var store = NewStore();
        var request = await CreateAsync(store);
        await store.ApproveAsync(request.Id, uses: 5, TimeSpan.FromMinutes(10));

        _time.Now = _time.Now.AddMinutes(11);

        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, ConsentStore.Fingerprint(Targets)))
            .Should().BeFalse();
        (await store.GetAsync(request.Id))!.Status.Should().Be(ConsentStatus.Expired);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task PendingRequest_ExpiresAndCannotBeApproved()
    {
        var store = NewStore();
        var request = await CreateAsync(store);

        _time.Now = _time.Now.AddMinutes(16);

        var act = () => store.ApproveAsync(request.Id, uses: 1, TimeSpan.FromMinutes(10));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task IdenticalAsk_ReusesThePendingRequest()
    {
        // A retrying agent must not spray the human with a new id on every attempt.
        var store = NewStore();
        var first = await CreateAsync(store);
        var second = await CreateAsync(store);

        second.Id.Should().Be(first.Id);
        (await store.ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task DifferentTargets_GetTheirOwnRequest()
    {
        var store = NewStore();
        await CreateAsync(store);
        await CreateAsync(store, ["reports/c.csv"]);

        (await store.ListAsync()).Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Denied_CannotBeConsumed()
    {
        var store = NewStore();
        var request = await CreateAsync(store);
        await store.DenyAsync(request.Id, "wrong share");

        (await store.TryConsumeAsync(ActionIds.StorageDelete, Resource, ConsentStore.Fingerprint(Targets)))
            .Should().BeFalse();
        (await store.GetAsync(request.Id))!.DeniedReason.Should().Be("wrong share");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void Fingerprint_IsOrderAndSlashInsensitive()
    {
        ConsentStore.Fingerprint(["b.csv", "a.csv"]).Should().Be(ConsentStore.Fingerprint(["a.csv", "b.csv"]));
        ConsentStore.Fingerprint(["/a.csv"]).Should().Be(ConsentStore.Fingerprint(["a.csv"]));
        ConsentStore.Fingerprint(["a.csv"]).Should().NotBe(ConsentStore.Fingerprint(["a.csv", "b.csv"]));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task State_SurvivesANewStoreInstance()
    {
        // The grant is spent by a *different process* than the one that approved it.
        var request = await CreateAsync(NewStore());
        await NewStore().ApproveAsync(request.Id, uses: 1, TimeSpan.FromMinutes(10));

        (await NewStore().TryConsumeAsync(ActionIds.StorageDelete, Resource, ConsentStore.Fingerprint(Targets)))
            .Should().BeTrue();
    }
}
