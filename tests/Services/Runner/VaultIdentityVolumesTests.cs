using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="VaultIdentityVolumes"/> (ADR 0011). The naming rule is a security
/// boundary rather than a convenience: two stations that resolve to the same volume share a vault
/// identity, and one of them is typically the station that reads attacker-controlled content. So
/// the tests here are mostly about what must *not* collide, and about the null that keeps an
/// unscopeable job from borrowing somebody else's identity.
/// </summary>
public class VaultIdentityVolumesTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_IsKeyedOnOwnerProjectLineAndStation()
    {
        VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", "line-1", "sync")
            .Should().Be("pks-vault-acme-widgets-line-1-sync");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_DistinguishesStationsOfTheSameLine()
    {
        // The load-bearing case. The audit station must not land on the sync station's volume,
        // because the entire guarantee of ADR 0011 is that a station with no grant holds no key.
        var sync = VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", "line-1", "sync");
        var audit = VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", "line-1", "audit");

        sync.Should().NotBe(audit);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_DistinguishesTheSameStationIdAcrossLinesAndProjects()
    {
        // Station ids come from a line's own definition, so "sync" is a name two unrelated lines
        // will pick independently. Without the line and project segments they would share keys.
        var a = VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", "line-1", "sync");
        var b = VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", "line-2", "sync");
        var c = VaultIdentityVolumes.ResolveVolumeName("Acme", "Gadgets", "line-1", "sync");

        a.Should().NotBe(b);
        a.Should().NotBe(c);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData(null, "sync")]
    [InlineData("line-1", null)]
    [InlineData("", "sync")]
    [InlineData("line-1", "  ")]
    public void ResolveVolumeName_IsNull_WithoutFullStationContext(string? lineId, string? stationId)
    {
        // No shared fallback on purpose: a volume that several unscopeable jobs share would hand
        // one job the identity another job enrolled. None is the safe answer, and the runner turns
        // it into a visible warning rather than a silent mount.
        VaultIdentityVolumes.ResolveVolumeName("Acme", "Widgets", lineId, stationId)
            .Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ResolveVolumeName_SanitizesSegmentsIntoLegalDockerVolumeNames()
    {
        // Real ids carry characters Docker will not take — the platform's own look like
        // "itm_20260829T112136Z-db2d…".
        VaultIdentityVolumes.ResolveVolumeName("Poul K", "Muse_Living", "alp_2026:1", "Sync Site")
            .Should().Be("pks-vault-poul-k-muse-living-alp-2026-1-sync-site");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildMountArg_IsEmpty_WhenNoVolumeResolved()
    {
        // Callers concatenate this unconditionally into the `devcontainer up` line.
        VaultIdentityVolumes.BuildMountArg(null).Should().BeEmpty();
        VaultIdentityVolumes.BuildMountArg("   ").Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void BuildMountArg_MountsAtTheFixedTarget_WithALeadingSpace()
    {
        VaultIdentityVolumes.BuildMountArg("pks-vault-acme-widgets-line-1-sync")
            .Should().Be(" --mount type=volume,source=pks-vault-acme-widgets-line-1-sync,target=/opt/pks-vault");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void IdentityPath_SitsInsideTheMountTarget()
    {
        // `vault agent run --identity` is handed this path, and enrol writes it. If the two ever
        // disagree the station enrols successfully and then cannot find itself on the next run.
        VaultIdentityVolumes.IdentityPath.Should().StartWith(VaultIdentityVolumes.MountTarget + "/");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void MountTarget_DoesNotCollideWithTheClaudeCredentialsVolume()
    {
        VaultIdentityVolumes.MountTarget.Should().NotBe(ClaudeCredentialVolumes.MountTarget);
    }
}
