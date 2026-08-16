using FluentAssertions;
using PKS.Commands.Aspire;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Commands.Aspire;

/// <summary>
/// The declare pass has to describe the composition the *run* will have, and the two passes do not
/// agree by default: `aspire do` defaults to the Production environment, `aspire run` applies the
/// AppHost's launch profile, and every Aspire template writes `DOTNET_ENVIRONMENT=Development` into
/// that profile. .NET loads user secrets only in Development, so a declare pass left on the default
/// reads none of them and calls parameters unanswered that the run answers without asking anybody.
///
/// It is one flag, which is exactly why it needs a test: nothing else in the output changes when it
/// goes missing — the report is simply wrong, and wrong in the direction of asking for more.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class PksAspireRunCommandTests
{
    private static PksAspireRunCommand.Settings NoSettings() => new();

    [Fact]
    public void Declare_pass_runs_in_Development_so_user_secrets_are_visible()
    {
        var arguments = PksAspireRunCommand.DeclareArguments(NoSettings(), []);

        arguments.Should().ContainInOrder("--environment", "Development");
    }

    [Fact]
    public void Declare_pass_takes_the_environment_it_is_given()
    {
        var arguments = PksAspireRunCommand.DeclareArguments(
            new PksAspireRunCommand.Settings { Environment = "Staging" }, []);

        arguments.Should().ContainInOrder("--environment", "Staging");
        arguments.Should().NotContain("Development");
    }

    [Fact]
    public void AppHost_arguments_come_last_so_they_cannot_be_read_as_options()
    {
        var arguments = PksAspireRunCommand.DeclareArguments(NoSettings(), ["--fabric", "--environment", "nonsense"]);

        // Everything the AppHost was given stays behind the separator, including a repeat of an
        // option this command also uses — the run pass would pass it through the same way.
        var separator = arguments.IndexOf("--");
        separator.Should().BeGreaterThan(0);
        arguments[separator..].Should().Equal("--", "--fabric", "--environment", "nonsense");
        arguments[..separator].Should().ContainInOrder("--environment", "Development");
    }

    [Fact]
    public void No_separator_when_the_AppHost_was_given_nothing()
    {
        // `aspire do … --` with nothing after it is not wrong, but it is noise in a process line
        // somebody will eventually have to read.
        PksAspireRunCommand.DeclareArguments(NoSettings(), []).Should().NotContain("--");
    }

    [Fact]
    public void AppHost_path_is_passed_through_when_one_was_given()
    {
        var arguments = PksAspireRunCommand.DeclareArguments(
            new PksAspireRunCommand.Settings { AppHost = "v1/src/apphost" }, []);

        arguments.Should().ContainInOrder("--apphost", "v1/src/apphost");
    }

    [Fact]
    public void The_step_is_named_before_any_option()
    {
        var arguments = PksAspireRunCommand.DeclareArguments(NoSettings(), ["--fabric"]);

        arguments[0].Should().Be("do");
        arguments[1].Should().Be("pks-declare");
    }
}
