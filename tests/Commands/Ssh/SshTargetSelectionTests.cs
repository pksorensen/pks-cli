using FluentAssertions;
using PKS.Commands.Ssh;
using PKS.Infrastructure.Services;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Ssh;

/// <summary>
/// Unit tests for the shared SSH target picker (docs/remote-runner-targets-plan.md Phase 4,
/// work item 2): auto-select on exactly one target, zero-state message on none, and the
/// interactive-prompt path for many, plus the name-arg fast path.
/// </summary>
public class SshTargetSelectionTests
{
    private static SshTarget Target(string host, string? label = null) =>
        new SshTarget { Host = host, Username = "root", Port = 22, KeyPath = "/key", Label = label };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_ZeroTargets_ReturnsNull_AndPrintsActionableMessage()
    {
        var console = new TestConsole();

        var result = await SshTargetSelection.PickAsync(console, Array.Empty<SshTarget>(), null, "Pick:");

        result.Should().BeNull();
        console.Output.Should().Contain("No SSH targets registered");
        console.Output.Should().Contain("pks ssh register");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_ExactlyOneTarget_AutoSelectsWithoutPrompting()
    {
        var console = new TestConsole();
        var only = Target("only.example.com", "only");

        var result = await SshTargetSelection.PickAsync(console, new List<SshTarget> { only }, null, "Pick:");

        result.Should().BeSameAs(only);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_ManyTargets_NoNameArg_PromptsInteractively()
    {
        var console = new TestConsole().Interactive();
        var targets = new List<SshTarget>
        {
            Target("first.example.com", "first"),
            Target("second.example.com", "second"),
            Target("third.example.com", "third"),
        };

        // Selection prompt defaults to the first choice; navigate down once to "second" and confirm.
        console.Input.PushKey(ConsoleKey.DownArrow);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await SshTargetSelection.PickAsync(console, targets, null, "Pick a target:");

        result.Should().BeSameAs(targets[1]);
        console.Output.Should().Contain("Pick a target:");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_NameArgMatchesLabel_ReturnsMatch_WithoutPrompting()
    {
        var console = new TestConsole();
        var targets = new List<SshTarget>
        {
            Target("first.example.com", "first"),
            Target("second.example.com", "second"),
        };

        var result = await SshTargetSelection.PickAsync(console, targets, "second", "Pick:");

        result.Should().BeSameAs(targets[1]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_NameArgMatchesHost_CaseInsensitive_ReturnsMatch()
    {
        var console = new TestConsole();
        var targets = new List<SshTarget> { Target("Server1.Example.com") };

        var result = await SshTargetSelection.PickAsync(console, targets, "server1.example.com", "Pick:");

        result.Should().BeSameAs(targets[0]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task PickAsync_NameArgNoMatch_ReturnsNull_AndPrintsError()
    {
        var console = new TestConsole();
        var targets = new List<SshTarget> { Target("only.example.com", "only") };

        var result = await SshTargetSelection.PickAsync(console, targets, "does-not-exist", "Pick:");

        result.Should().BeNull();
        console.Output.Should().Contain("not found");
    }
}
