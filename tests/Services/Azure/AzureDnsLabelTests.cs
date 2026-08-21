using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Services.Azure;

/// <summary>
/// The DNS label is derived from a VM name, and Azure's grammar for it is narrower than the
/// grammar for VM names. A violation comes back as a 400 whose message names the label, not the VM,
/// so the mismatch is easier to prevent here than to recognise there.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class AzureDnsLabelTests
{
    [Theory]
    [InlineData("t3-host-001", "t3-host-001")]
    [InlineData("T3-Host-001", "t3-host-001")]      // labels are lowercase
    [InlineData("pks_vm_ab12", "pks-vm-ab12")]      // underscores are not legal
    [InlineData("-leading-dash-", "leading-dash")]  // may not start or end with one
    [InlineData("a--b", "a-b")]                     // no doubled separators
    [InlineData("9lives", "t3-9lives")]             // must start with a letter
    public void Sanitize_produces_a_label_azure_accepts(string raw, string expected) =>
        AzureVmService.SanitizeDnsLabel(raw).Should().Be(expected);

    [Fact]
    public void Sanitize_respects_the_length_bounds()
    {
        AzureVmService.SanitizeDnsLabel("ab").Length.Should().BeGreaterThanOrEqualTo(3);

        var long_ = AzureVmService.SanitizeDnsLabel(new string('a', 200));
        long_.Length.Should().BeLessThanOrEqualTo(63);
        long_.Should().NotEndWith("-");
    }

    [Theory]
    // A rule already covering the port means there is nothing to add — the check that keeps a
    // re-run from stacking duplicate rules, and from colliding with one added by hand.
    [InlineData("*", "443", true)]
    [InlineData("443", "443", true)]
    [InlineData("80-443", "443", true)]
    [InlineData("80-442", "443", false)]
    [InlineData("80", "443", false)]
    public void Port_range_cover_is_read_the_way_azure_writes_it(string declared, string want, bool covered) =>
        AzureVmService.PortRangeCovers(declared, want).Should().Be(covered);
}
