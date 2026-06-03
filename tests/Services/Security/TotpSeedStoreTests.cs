using FluentAssertions;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

public class TotpSeedStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pks-2fa-{Guid.NewGuid():N}.json");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // "12345678901234567890"

    private TotpSeedStore NewStore() => new(_path, _clock);

    public void Dispose() { try { File.Delete(_path); } catch { } }

    private async Task<TotpSeedStore> EnrolledStoreAsync(params string[] recovery)
    {
        var store = NewStore();
        await store.EnrollAsync(new TotpEnrollment(Secret, recovery));
        return store;
    }

    private string CurrentCode(int stepOffset = 0)
        => TotpService.ComputeCode(Secret, TotpService.TimeStep(_clock.Now) + stepOffset);

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task NotEnrolled_ByDefault()
    {
        (await NewStore().IsEnrolledAsync()).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Enroll_ThenIsEnrolled_AndVerifiesCurrentCode()
    {
        var store = await EnrolledStoreAsync();
        (await store.IsEnrolledAsync()).Should().BeTrue();
        (await store.VerifyAsync(CurrentCode())).Status.Should().Be(VerifyStatus.Ok);
    }

    [Theory]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Verify_AcceptsAdjacentDriftWindow(int offset)
    {
        var store = await EnrolledStoreAsync();
        (await store.VerifyAsync(CurrentCode(offset))).Status.Should().Be(VerifyStatus.Ok);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Verify_RejectsOutOfWindowCode()
    {
        var store = await EnrolledStoreAsync();
        (await store.VerifyAsync(CurrentCode(-2))).Status.Should().Be(VerifyStatus.Invalid);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Verify_SameCodeTwice_SecondIsReplay()
    {
        var store = await EnrolledStoreAsync();
        var code = CurrentCode();
        (await store.VerifyAsync(code)).Status.Should().Be(VerifyStatus.Ok);
        (await store.VerifyAsync(code)).Status.Should().Be(VerifyStatus.Replay);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Verify_FiveFailures_LocksOut()
    {
        var store = await EnrolledStoreAsync();
        for (int i = 0; i < 4; i++)
            (await store.VerifyAsync("000000")).Status.Should().Be(VerifyStatus.Invalid);
        (await store.VerifyAsync("000000")).Status.Should().Be(VerifyStatus.LockedOut);
        // A correct code is still refused while locked out.
        (await store.VerifyAsync(CurrentCode())).Status.Should().Be(VerifyStatus.LockedOut);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task RecoveryCode_VerifiesOnce_ThenRejected()
    {
        var recovery = TotpService.GenerateRecoveryCodes(2).ToArray();
        var store = await EnrolledStoreAsync(recovery);

        (await store.RecoveryCodesRemainingAsync()).Should().Be(2);
        (await store.VerifyAsync(recovery[0])).Status.Should().Be(VerifyStatus.Ok);
        (await store.RecoveryCodesRemainingAsync()).Should().Be(1);
        (await store.VerifyAsync(recovery[0])).Status.Should().Be(VerifyStatus.Invalid); // single-use
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Clear_RemovesEnrollment()
    {
        var store = await EnrolledStoreAsync();
        await store.ClearAsync();
        (await store.IsEnrolledAsync()).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task SeedFile_IsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        await EnrolledStoreAsync();
        var mode = File.GetUnixFileMode(_path);
        (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(UnixFileMode.None);
    }
}
