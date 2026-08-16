using FluentAssertions;
using PKS.Commands.Agentics.Runner;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// <c>status</c>, <c>logs</c> and <c>stop</c> serve two worlds -- a runner on this machine and one
/// handed off to an SSH target -- from one positional argument. They cannot collide because an
/// owner/project always contains a slash and an SSH target label never does; these tests pin that
/// rule so the two command families stay merged.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerSelectorTests
{
    [Fact]
    public void A_slash_means_a_runner_on_this_machine()
    {
        new AgenticsRunnerSelectorSettings { Selector = "pksorensen/museliving" }
            .LocalOwnerProject.Should().Be("pksorensen/museliving");
    }

    [Fact]
    public void A_bare_label_means_an_ssh_target()
    {
        new AgenticsRunnerSelectorSettings { Selector = "hetzner" }
            .LocalOwnerProject.Should().BeNull();
    }

    [Fact]
    public void The_project_option_can_name_the_local_runner_on_its_own()
    {
        new AgenticsRunnerSelectorSettings { Project = "pksorensen/museliving" }
            .LocalOwnerProject.Should().Be("pksorensen/museliving");
    }

    [Fact]
    public void A_target_plus_project_stays_on_the_ssh_path()
    {
        // `status hetzner --project o/p` disambiguates *within* a target; --project must not
        // hijack it into the local path.
        new AgenticsRunnerSelectorSettings { Selector = "hetzner", Project = "pksorensen/museliving" }
            .LocalOwnerProject.Should().BeNull();
    }

    [Fact]
    public void No_selector_at_all_is_neither_until_the_store_is_consulted()
    {
        new AgenticsRunnerSelectorSettings().LocalOwnerProject.Should().BeNull();
    }
}
