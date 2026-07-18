using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for the pure argv builder behind <c>pks agentics runner claude-login</c>
/// (docs/remote-runner-targets-plan.md Phase 5, work item 2). Per the GOAL, the interactive
/// ssh -t + docker run -it flow itself is not unit-testable -- only the exact argv it launches is,
/// via <see cref="ClaudeLoginCommandBuilder.Build"/> and a mocked
/// <see cref="IInteractiveProcessLauncher"/> at the command layer.
/// </summary>
public class ClaudeLoginCommandBuilderTests
{
    private static SshTarget MakeTarget(string? managedKeyId = null) => new()
    {
        Host = "203.0.113.10",
        Username = "runner",
        Port = 2222,
        KeyPath = "/home/user/.ssh/id_ed25519",
        Label = "my-target",
        ManagedKeyId = managedKeyId,
    };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_UsesSshAsFileName()
    {
        var (fileName, _) = ClaudeLoginCommandBuilder.Build(MakeTarget(), "pks-claude-acme-widgets", keyPath: "/home/user/.ssh/id_ed25519");
        fileName.Should().Be("ssh");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_AllocatesATty()
    {
        var (_, args) = ClaudeLoginCommandBuilder.Build(MakeTarget(), "pks-claude-acme-widgets", keyPath: "/home/user/.ssh/id_ed25519");
        args.Should().Contain("-t");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_IncludesPortAndUserAtHost()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        args.Should().Contain("-p");
        args.Should().Contain(target.Port.ToString());
        args.Should().Contain($"{target.Username}@{target.Host}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_WithKeyPath_IncludesIdentityFile()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        args.Should().Contain("-i");
        args.Should().Contain(target.KeyPath);
        args.Should().Contain("IdentitiesOnly=yes");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_WithoutKeyPath_OmitsIdentityFile()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: null);

        args.Should().NotContain("-i");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_RemoteCommand_MountsVolumeAtClaudeConfigDir()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        var remoteCommand = args[^1];
        remoteCommand.Should().Contain("-v pks-claude-acme-widgets:/home/node/.claude");
        remoteCommand.Should().Contain("CLAUDE_CONFIG_DIR=/home/node/.claude");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_RemoteCommand_IsInteractiveDockerRun()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        var remoteCommand = args[^1];
        remoteCommand.Should().Contain("docker run");
        remoteCommand.Should().Contain("-it");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_RemoteCommand_InstallsAndRunsClaude()
    {
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        var remoteCommand = args[^1];
        remoteCommand.Should().Contain("npm install -g @anthropic-ai/claude-code");
        remoteCommand.Should().Contain("claude");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Build_NeverEmitsDoubleQuotes_InArgumentList()
    {
        // args flow through ProcessStartInfo.ArgumentList, which handles its own OS-specific
        // quoting -- manually embedded double-quotes anywhere but inside the single remote-command
        // string would be a bug (either redundant or corrupting).
        var target = MakeTarget();
        var (_, args) = ClaudeLoginCommandBuilder.Build(target, "pks-claude-acme-widgets", keyPath: target.KeyPath);

        for (var i = 0; i < args.Count - 1; i++)
            args[i].Should().NotContain("\"");
    }
}
