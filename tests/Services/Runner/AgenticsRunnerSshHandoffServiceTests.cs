using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="AgenticsRunnerSshHandoffService.DetectClaudeCredentialVolumeAsync"/>
/// (docs/remote-runner-targets-plan.md Phase 5, work item 1): whether the remote already has the
/// default project-scoped <c>pks-claude-*</c> credential volume, so the SSH-handoff pre-flight and
/// <c>runner status</c> can warn instead of letting a headless remote spawn silently stall on a
/// missing Claude OAuth login.
/// </summary>
public class AgenticsRunnerSshHandoffServiceTests
{
    private static SshTarget MakeTarget() => new SshTarget
    {
        Host = "remote.example.com",
        Username = "runner",
        Port = 22,
        KeyPath = "/home/user/.ssh/id_ed25519",
    };

    private static AgenticsRunnerSshHandoffService MakeService(Mock<ISshCommandRunner> sshRunner) =>
        new AgenticsRunnerSshHandoffService(sshRunner.Object, new Mock<ISshKeyStore>().Object, new Mock<IHttpClientFactory>().Object);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task DetectClaudeCredentialVolumeAsync_VolumePresent_ReturnsTrue()
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = "PKS_CLAUDE_VOLUME_PRESENT\n" });

        var service = MakeService(sshRunner);

        var result = await service.DetectClaudeCredentialVolumeAsync(MakeTarget(), "acme", "widgets");

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task DetectClaudeCredentialVolumeAsync_VolumeMissing_ReturnsFalse()
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = "PKS_CLAUDE_VOLUME_MISSING\n" });

        var service = MakeService(sshRunner);

        var result = await service.DetectClaudeCredentialVolumeAsync(MakeTarget(), "acme", "widgets");

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task DetectClaudeCredentialVolumeAsync_HostUnreachable_ReturnsNull()
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 255, StdOut = "", StdErr = "ssh: connect to host remote.example.com port 22: Connection refused" });

        var service = MakeService(sshRunner);

        var result = await service.DetectClaudeCredentialVolumeAsync(MakeTarget(), "acme", "widgets");

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task DetectClaudeCredentialVolumeAsync_UsesDefaultProjectScopedVolumeName()
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        string? sentCommand = null;
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<RemoteHostConfig, string, CancellationToken>((_, cmd, _) => sentCommand = cmd)
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = "PKS_CLAUDE_VOLUME_PRESENT\n" });

        var service = MakeService(sshRunner);

        await service.DetectClaudeCredentialVolumeAsync(MakeTarget(), "Acme", "Widgets");

        sentCommand.Should().NotBeNull();
        sentCommand!.Should().Contain(ClaudeCredentialVolumes.ResolveVolumeName("Acme", "Widgets", null, "project"));
    }

    // ── ForwardConfigValueAsync ──────────────────────────────────────────────────────────────

    private static Mock<ISshCommandRunner> MakeForwardingSshRunnerMock(
        string? existingRemoteSettingsJson, out Func<string?> capturedLocalFile)
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        string? localFilePath = null;

        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(),
                It.Is<string>(cmd => cmd.StartsWith("mkdir -p")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });

        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(),
                It.Is<string>(cmd => cmd.StartsWith("cat ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = existingRemoteSettingsJson ?? "" });

        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(),
                It.Is<string>(cmd => cmd.StartsWith("chmod ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });

        sshRunner
            .Setup(x => x.ScpAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<RemoteHostConfig, string, string, bool, CancellationToken>((_, local, _, _, _) => localFilePath = local)
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });

        capturedLocalFile = () => localFilePath;
        return sshRunner;
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ForwardConfigValueAsync_NoExistingRemoteFile_WritesSingleKey_ReturnsNull()
    {
        var sshRunner = MakeForwardingSshRunnerMock(null, out var capturedLocalFile);
        var service = MakeService(sshRunner);

        var error = await service.ForwardConfigValueAsync(MakeTarget(), "github.auth.token", "{\"AccessToken\":\"gho_abc\"}");

        error.Should().BeNull();
        var writtenPath = capturedLocalFile();
        writtenPath.Should().NotBeNull();
        File.Exists(writtenPath).Should().BeFalse("the temp file must be shredded after the scp completes");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ForwardConfigValueAsync_ExistingRemoteFile_PreservesOtherKeys()
    {
        var existing = JsonSerializer.Serialize(new Dictionary<string, string> { ["some.other.key"] = "keep-me" });
        string? capturedContent = null;

        var sshRunner = new Mock<ISshCommandRunner>();
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(cmd => cmd.StartsWith("mkdir -p")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(cmd => cmd.StartsWith("cat ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = existing });
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(cmd => cmd.StartsWith("chmod ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });
        sshRunner
            .Setup(x => x.ScpAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<RemoteHostConfig, string, string, bool, CancellationToken>((_, local, _, _, _) => capturedContent = File.ReadAllText(local))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });

        var service = MakeService(sshRunner);

        var error = await service.ForwardConfigValueAsync(MakeTarget(), "github.auth.token", "gho_new_value");

        error.Should().BeNull();
        capturedContent.Should().NotBeNull();
        var merged = JsonSerializer.Deserialize<Dictionary<string, string>>(capturedContent!)!;
        merged.Should().Contain("some.other.key", "keep-me");
        merged.Should().Contain("github.auth.token", "gho_new_value");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ForwardConfigValueAsync_ScpFails_ReturnsErrorMessage_DoesNotThrow()
    {
        var sshRunner = new Mock<ISshCommandRunner>();
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(cmd => cmd.StartsWith("mkdir -p")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0 });
        sshRunner
            .Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(cmd => cmd.StartsWith("cat ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = "" });
        sshRunner
            .Setup(x => x.ScpAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 1, StdErr = "scp: permission denied" });

        var service = MakeService(sshRunner);

        var error = await service.ForwardConfigValueAsync(MakeTarget(), "github.auth.token", "gho_x");

        error.Should().NotBeNull();
        error.Should().Contain("permission denied");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task ForwardConfigValueAsync_CorruptRemoteFile_StartsFresh_ReturnsNull()
    {
        var sshRunner = MakeForwardingSshRunnerMock("{ not valid json", out _);
        var service = MakeService(sshRunner);

        var error = await service.ForwardConfigValueAsync(MakeTarget(), "github.auth.token", "gho_x");

        error.Should().BeNull();
    }
}
