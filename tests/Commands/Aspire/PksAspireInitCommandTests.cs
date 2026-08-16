using System.Reflection;
using FluentAssertions;
using PKS.Commands.Aspire;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Commands.Aspire;

/// <summary>
/// `pks aspire init` copies one embedded file into an AppHost, so the only thing that can really go
/// wrong is the file not being in the build — and that failure is invisible until somebody runs the
/// command. It happened once already: the resource is named `PksDeclare.cs.template`, MSBuild's
/// AssignCulture read the middle extension `.cs` as the Czech culture, and the resource went into a
/// `cs/` satellite assembly. Build green, `--list-steps` green, command dead. Hence a test that asks
/// the assembly the same question the command asks it.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class PksAspireInitCommandTests
{
    private static Assembly CommandAssembly => typeof(PksAspireInitCommand).Assembly;

    [Fact]
    public void PksDeclare_is_embedded_in_the_main_assembly()
    {
        // Not a satellite assembly, not under a resource namespace prefix — the exact name the
        // command passes to GetManifestResourceStream.
        CommandAssembly.GetManifestResourceNames().Should().Contain("PksDeclare.cs");
    }

    [Fact]
    public void PksDeclare_carries_the_apphost_half_of_the_contract()
    {
        using var stream = CommandAssembly.GetManifestResourceStream("PksDeclare.cs");
        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();

        // The three things an AppHost has to be able to say, and the step name pks looks for.
        source.Should().Contain("AddPksCapability");
        source.Should().Contain("SuggestedValue");
        source.Should().Contain("pks-declare");
        source.Should().Contain("PKS_DECLARE_OUT");
    }
}
