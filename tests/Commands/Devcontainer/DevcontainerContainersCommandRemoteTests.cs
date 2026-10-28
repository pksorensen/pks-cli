using System.Text.Json;
using FluentAssertions;
using Moq;
using PKS.Commands.Devcontainer;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Devcontainer;

/// <summary>
/// <c>pks devcontainer list --remote &lt;target&gt;</c>: the non-interactive path a desktop tool
/// drives. No prompt, managed key materialised + shredded, action guard consulted, and in json
/// mode nothing but the document on stdout — an unreachable host included, as data.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DevcontainerContainersCommandRemoteTests
{
    private const string DockerPsLine =
        "{\"ID\":\"abc123def456\",\"Names\":\"vigilant_x\",\"Status\":\"Up 3 days\",\"Image\":\"vsc-agentic-live-www-abc\",\"CreatedAt\":\"2026-10-20 07:00:00 +0000 UTC\",\"Labels\":\"devcontainer.local_folder=/workspaces/agentic-live-www,devcontainer.metadata=[{\\\"remoteUser\\\":\\\"node\\\"}]\"}";

    private const string InspectJson =
        "[{\"Mounts\":[{\"Type\":\"volume\",\"Name\":\"agentic-live-www-vol\"},{\"Type\":\"bind\",\"Source\":\"/x\"}]}]";

    private static readonly SshTarget Hetzner = new()
    {
        Host = "projects.example.com",
        Username = "root",
        Port = 22,
        KeyPath = "",
        Label = "hetzner",
        ManagedKeyId = "key-1",
    };

    private static Mock<ISshTargetConfigurationService> ConfigWith(params SshTarget[] targets)
    {
        var mock = new Mock<ISshTargetConfigurationService>();
        mock.Setup(x => x.ListTargetsAsync()).ReturnsAsync(targets.ToList());
        mock.Setup(x => x.FindTargetAsync(It.IsAny<string>()))
            .ReturnsAsync((string spec) => targets.FirstOrDefault(t => t.Label == spec || t.Host == spec));
        return mock;
    }

    private static Mock<ISshCommandRunner> RunnerReturning(string psStdout, bool psSuccess = true, string psStderr = "")
    {
        var mock = new Mock<ISshCommandRunner>();
        mock.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("docker ps")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = psSuccess ? 0 : 255, StdOut = psStdout, StdErr = psStderr });
        mock.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("docker inspect")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = InspectJson });
        return mock;
    }

    private static Mock<ISshKeyStore> KeyStoreMaterializing(string path)
    {
        var mock = new Mock<ISshKeyStore>();
        mock.Setup(x => x.MaterializeAsync("key-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MaterializedKey(path));
        return mock;
    }

    private static Mock<IActionGuard> PermissiveGuard()
    {
        var mock = new Mock<IActionGuard>();
        mock.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    private static string TempKeyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pks-test-key-{Guid.NewGuid():N}");
        File.WriteAllText(path, "not a real key");
        return path;
    }

    private static (DevcontainerContainersCommand Command, TestConsole Console, StringWriter Stdout, StringWriter Stderr) Build(
        Mock<ISshTargetConfigurationService> config,
        Mock<ISshCommandRunner> runner,
        Mock<ISshKeyStore>? keyStore = null,
        Mock<IActionGuard>? guard = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200; // keep table cells on one line so names can be asserted whole
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var spawner = new Mock<IDevcontainerSpawnerService>();
        spawner.Setup(x => x.ListManagedContainersAsync()).ReturnsAsync(new List<DevcontainerContainerInfo>());
        var command = new DevcontainerContainersCommand(
            spawner.Object,
            console,
            config.Object,
            runner.Object,
            (keyStore ?? KeyStoreMaterializing(TempKeyFile())).Object,
            (guard ?? PermissiveGuard()).Object,
            stdout,
            stderr);
        return (command, console, stdout, stderr);
    }

    [Fact]
    public async Task RemoteLabel_DoesNotPromptAndCallsRunnerWithResolvedHost()
    {
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().NotContain("Which host?");
        console.Output.Should().BeEmpty("json mode must not draw the banner or a spinner");
        runner.Verify(x => x.RunAsync(
            It.Is<RemoteHostConfig>(h => h.Host == "projects.example.com" && h.Username == "root"),
            It.Is<string>(c => c.StartsWith("docker ps")),
            It.IsAny<CancellationToken>()), Times.Once);
        stdout.ToString().TrimStart().Should().StartWith("{");
    }

    [Fact]
    public async Task Json_IsWrappedWithHostReachableError_AndKeepsLegacyFields()
    {
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, _, stdout, _) = Build(ConfigWith(Hetzner), runner);

        await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        root.GetProperty("host").GetString().Should().Be("hetzner");
        root.GetProperty("reachable").GetBoolean().Should().BeTrue();
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);

        var c = root.GetProperty("containers")[0];
        c.GetProperty("id").GetString().Should().Be("abc123def456");
        c.GetProperty("name").GetString().Should().Be("vigilant_x");
        c.GetProperty("project").GetString().Should().Be("agentic-live-www");
        c.GetProperty("localFolder").GetString().Should().Be("/workspaces/agentic-live-www");
        c.GetProperty("status").GetString().Should().Be("Up 3 days");
        c.GetProperty("image").GetString().Should().Be("vsc-agentic-live-www-abc");
        c.GetProperty("createdAt").GetString().Should().Be("2026-10-20T07:00:00Z");
        // Legacy names stay so existing consumers of the old array keep working.
        c.GetProperty("containerId").GetString().Should().Be("abc123def456");
        c.GetProperty("projectName").GetString().Should().Be("agentic-live-www");
        c.GetProperty("created").GetString().Should().Be("2026-10-20 07:00:00 +0000 UTC");
        c.GetProperty("volumes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task UnreachableHost_IsDataWithExitZero_InJsonMode()
    {
        var runner = RunnerReturning("", psSuccess: false, psStderr: "ssh: connect to host projects.example.com port 22: Connection timed out");
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().BeEmpty();
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("reachable").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Connection timed out");
        doc.RootElement.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EmptyRemote_StillEmitsTheWrapperInJsonMode()
    {
        var runner = RunnerReturning("");
        var (command, _, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("reachable").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ManagedKey_IsMaterializedUsedAndDisposed()
    {
        var keyPath = TempKeyFile();
        var keyStore = KeyStoreMaterializing(keyPath);
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, _, _, _) = Build(ConfigWith(Hetzner), runner, keyStore);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        keyStore.Verify(x => x.MaterializeAsync("key-1", It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(x => x.RunAsync(It.Is<RemoteHostConfig>(h => h.KeyPath == keyPath), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        File.Exists(keyPath).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownTarget_ExitsOne()
    {
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, _, stdout, stderr) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "nope", Format = "json" });

        exit.Should().Be(1);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("nope");
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActionGuardDenied_ExitsOne()
    {
        var guard = new Mock<IActionGuard>();
        guard.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ActionGuardDeniedException(ActionIds.SshConnect, "second factor refused"));
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, _, _, stderr) = Build(ConfigWith(Hetzner), runner, guard: guard);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(1);
        guard.Verify(x => x.RequireAsync(It.Is<ActionRequest>(r => r.ActionId == ActionIds.SshConnect), It.IsAny<CancellationToken>()), Times.Once);
        stderr.ToString().Should().Contain("second factor refused");
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RemoteLocal_SkipsSshEntirely()
    {
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "local", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().NotContain("Which host?");
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("host").GetString().Should().Be("local");
        doc.RootElement.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task TableMode_StillShowsBannerAndTable()
    {
        var runner = RunnerReturning(DockerPsLine + "\n");
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerContainersCommand.Settings { Remote = "hetzner", Format = "table" });

        exit.Should().Be(0);
        console.Output.Should().Contain("Containers");
        console.Output.Should().Contain("agentic-live-www");
        stdout.ToString().Should().BeEmpty();
    }
}
