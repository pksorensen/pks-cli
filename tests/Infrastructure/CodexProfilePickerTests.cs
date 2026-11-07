using FluentAssertions;
using PKS.Commands.Codex;
using PKS.Infrastructure.Services.Agent.Setup;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// The profile picker — the one piece of this feature a reader always sees, and the piece a
/// scripted run must never see. Both branches were unexercised by every other test here, because
/// they all pass <c>--use</c> or hit the refresh path.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class CodexProfilePickerTests
{
    private static AspProfileList TwoGroups() => new()
    {
        Source = new AspSource { Id = "agentics", DisplayName = "Agentics Factory" },
        Default = "beta",
        Profiles =
        [
            new AspProfile { Id = "alpha", DisplayName = "Alpha", Group = "poul" },
            new AspProfile { Id = "beta", DisplayName = "Beta", Group = "poul" },
            new AspProfile { Id = "gamma", DisplayName = "Gamma", Group = "acme" },
        ],
    };

    private static CodexConnectCommand Command(TestConsole console) =>
        new(client: null!, console);

    [Fact]
    public void WithOneProfile_DoesNotPrompt()
    {
        // A prompt with a single answer is a keystroke asked of somebody for no reason.
        var console = new TestConsole().Interactive();
        var list = TwoGroups();
        list.Profiles = [list.Profiles[0]];

        Command(console).ChooseProfile(list, null)!.Id.Should().Be("alpha");
        console.Output.Should().NotContain("Which profile");
    }

    [Fact]
    public void NonInteractive_TakesThePlatformsDefault_RatherThanHanging()
    {
        // The failure this prevents: a SelectionPrompt with nobody at the keyboard blocks for
        // ever, so a CI step or a piped run would hang instead of failing or proceeding.
        var console = new TestConsole(); // not interactive

        var picked = Command(console).ChooseProfile(TwoGroups(), null);

        picked!.Id.Should().Be("beta", "the document's `default`, not simply the first");
        console.Output.Should().Contain("--use");
    }

    [Fact]
    public void PickerChoices_GroupByOwner_MarkTheDefault_AndNeverCollide()
    {
        var list = TwoGroups();
        // Two owners publishing the same display name is normal, and a prompt keyed by string
        // would otherwise map both rows to whichever profile was added last.
        list.Profiles = [.. list.Profiles, new AspProfile { Id = "delta", DisplayName = "Beta", Group = "acme" }];

        var (groups, byLabel) = CodexConnectCommand.BuildPickerChoices(list);

        groups.Select(g => g.Group).Should().Equal("acme", "poul");
        groups.Single(g => g.Group == "poul").Labels.Should().Equal("Alpha", "Beta (default)");

        byLabel.Should().HaveCount(4, "every profile must be reachable, duplicates included");
        byLabel.Values.Select(p => p.Id).Should().BeEquivalentTo(["alpha", "beta", "gamma", "delta"]);
        byLabel["Beta (default)"].Id.Should().Be("beta");
    }

    [Fact]
    public void PickerChoices_WithNoGroup_FallBackToOne_RatherThanVanishing()
    {
        var list = TwoGroups();
        list.Profiles = [new AspProfile { Id = "solo" }];

        var (groups, byLabel) = CodexConnectCommand.BuildPickerChoices(list);

        groups.Should().ContainSingle().Which.Group.Should().Be("other");
        byLabel.Should().ContainSingle().Which.Key.Should().Be("solo", "the id stands in for a missing name");
    }

    [Fact]
    public void WithUse_NamingAKnownProfile_SkipsThePromptEntirely()
    {
        var console = new TestConsole().Interactive();

        Command(console).ChooseProfile(TwoGroups(), "GAMMA")!.Id
            .Should().Be("gamma", "matching is case-insensitive; ids come from a URL or a shell");

        console.Output.Should().NotContain("Which profile");
    }

    [Fact]
    public void WithUse_NamingSomethingElse_FailsAndListsWhatThereIs()
    {
        // Returning null means "stop", and the reader needs to know what to type instead —
        // a bare "not found" would leave them guessing at ids they have never seen.
        var console = new TestConsole().Interactive();

        Command(console).ChooseProfile(TwoGroups(), "nope").Should().BeNull();

        console.Output.Should().Contain("nope");
        console.Output.Should().Contain("alpha").And.Contain("beta").And.Contain("gamma");
    }
}
