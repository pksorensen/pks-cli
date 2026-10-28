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
/// <c>pks devcontainer sessions --remote &lt;target&gt; --format json</c> is consumed by a desktop
/// tool that never answers prompts, so these tests pin: no prompt, nothing but JSON on stdout,
/// unreachable host as data (exit 0), managed-key materialisation + disposal, and action-guard routing.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class DevcontainerSessionsCommandTests
{
    private const string DockerPsLine =
        "{\"ID\":\"abc123def456\",\"Names\":\"vigilant_x\",\"Status\":\"Up 3 days\",\"Image\":\"vsc-agentic-live-www-abc\",\"CreatedAt\":\"2026-10-20 07:00:00 +0000 UTC\",\"Labels\":\"devcontainer.local_folder=/workspaces/agentic-live-www,devcontainer.metadata=[{\\\"remoteUser\\\":\\\"node\\\"}]\"}";

    private const string ProbeOutput =
        "BEGIN\\tabc123def456\n"
        + "SESSION\t1\t{\"pid\":2456920,\"sessionId\":\"49c69828-1145-4c75-bfef-79ba7ddd\",\"cwd\":\"/workspaces/agentic-live-www\",\"startedAt\":1792616282506,\"name\":\"heypoul-meeting-mode-notch\",\"status\":\"busy\",\"kind\":\"interactive\",\"updatedAt\":1793181518735}\n"
        + "SESSION\t0\t{\"pid\":1,\"sessionId\":\"stale\"}\n"
        + "TMUX\tcc-plan-sync\t1\t1792568235\t0\n"
        + "END\\tabc123def456\t0\n";

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

    private static Mock<ISshCommandRunner> RunnerReturning(string psStdout, string probeStdout, bool psSuccess = true, string psStderr = "")
    {
        var mock = new Mock<ISshCommandRunner>();
        mock.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("docker ps")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = psSuccess ? 0 : 255, StdOut = psStdout, StdErr = psStderr });
        mock.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("echo ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = probeStdout.Replace("\\t", "\t") });
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

    private static (DevcontainerSessionsCommand Command, TestConsole Console, StringWriter Stdout, StringWriter Stderr) Build(
        Mock<ISshTargetConfigurationService> config,
        Mock<ISshCommandRunner> runner,
        Mock<ISshKeyStore>? keyStore = null,
        Mock<IActionGuard>? guard = null)
    {
        var console = new TestConsole();
        console.Profile.Width = 200; // keep table cells on one line so names can be asserted whole
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var command = new DevcontainerSessionsCommand(
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
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().NotContain("Which host?");
        runner.Verify(x => x.RunAsync(
            It.Is<RemoteHostConfig>(h => h.Host == "projects.example.com" && h.Username == "root" && h.Port == 22),
            It.Is<string>(c => c.StartsWith("docker ps")),
            It.IsAny<CancellationToken>()), Times.Once);
        stdout.ToString().TrimStart().Should().StartWith("{");
    }

    [Fact]
    public async Task Json_ContainsContractFieldsAndOnlyAliveSessions()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().BeEmpty("json mode must not draw a banner or spinner on the console");

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        root.GetProperty("host").GetString().Should().Be("hetzner");
        root.GetProperty("reachable").GetBoolean().Should().BeTrue();
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("fetchedAt").GetString().Should().EndWith("Z");

        var containers = root.GetProperty("containers");
        containers.GetArrayLength().Should().Be(1);
        var c = containers[0];
        c.GetProperty("id").GetString().Should().Be("abc123def456");
        c.GetProperty("name").GetString().Should().Be("vigilant_x");
        c.GetProperty("project").GetString().Should().Be("agentic-live-www");
        c.GetProperty("localFolder").GetString().Should().Be("/workspaces/agentic-live-www");
        c.GetProperty("status").GetString().Should().Be("Up 3 days");
        c.GetProperty("staleSessionFiles").GetInt32().Should().Be(1);
        c.GetProperty("probeError").ValueKind.Should().Be(JsonValueKind.Null);

        var sessions = c.GetProperty("sessions");
        sessions.GetArrayLength().Should().Be(1);
        var s = sessions[0];
        s.GetProperty("source").GetString().Should().Be("claude");
        s.GetProperty("sessionId").GetString().Should().Be("49c69828-1145-4c75-bfef-79ba7ddd");
        s.GetProperty("pid").GetInt64().Should().Be(2456920);
        s.GetProperty("name").GetString().Should().Be("heypoul-meeting-mode-notch");
        s.GetProperty("status").GetString().Should().Be("busy");
        s.GetProperty("kind").GetString().Should().Be("interactive");
        s.GetProperty("cwd").GetString().Should().Be("/workspaces/agentic-live-www");
        s.GetProperty("startedAt").GetString().Should().Be("2026-10-21T20:58:02Z");
        s.GetProperty("updatedAt").GetString().Should().Be("2026-10-28T09:58:38Z");

        var tmux = c.GetProperty("tmux");
        tmux.GetArrayLength().Should().Be(1);
        tmux[0].GetProperty("name").GetString().Should().Be("cc-plan-sync");
        tmux[0].GetProperty("windows").GetInt32().Should().Be(1);
        tmux[0].GetProperty("attached").GetBoolean().Should().BeFalse();
        tmux[0].GetProperty("createdAt").GetString().Should().Be("2026-10-21T07:37:15Z");
    }

    [Fact]
    public async Task ProbeUsesRemoteUserFromMetadataLabel()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, _, _) = Build(ConfigWith(Hetzner), runner);

        await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        // The probe script travels base64-encoded; decode what the runner received and check the exec line.
        var sent = runner.Invocations
            .Select(i => i.Arguments[1] as string)
            .First(c => c != null && c.StartsWith("echo "))!;
        var b64 = sent.Substring("echo ".Length).Split(' ')[0];
        var script = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        script.Should().Contain("-u node abc123def456");
    }

    [Fact]
    public async Task UnreachableHost_IsDataNotFailure_InJsonMode()
    {
        var runner = RunnerReturning("", "", psSuccess: false, psStderr: "ssh: connect to host projects.example.com port 22: Connection timed out");
        var (command, console, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().BeEmpty();
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("host").GetString().Should().Be("hetzner");
        doc.RootElement.GetProperty("reachable").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Connection timed out");
        doc.RootElement.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UnreachableHost_InTableMode_ExitsNonZero()
    {
        var runner = RunnerReturning("", "", psSuccess: false, psStderr: "ssh: connect timed out");
        var (command, console, _, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "table" });

        exit.Should().Be(1);
        console.Output.Should().Contain("connect timed out");
    }

    [Fact]
    public async Task ManagedKey_IsMaterializedUsedAndDisposed()
    {
        var keyPath = TempKeyFile();
        var keyStore = KeyStoreMaterializing(keyPath);
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, _, _) = Build(ConfigWith(Hetzner), runner, keyStore);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        keyStore.Verify(x => x.MaterializeAsync("key-1", It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(x => x.RunAsync(It.Is<RemoteHostConfig>(h => h.KeyPath == keyPath), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        File.Exists(keyPath).Should().BeFalse("the materialized key must be shredded after the command");
    }

    [Fact]
    public async Task ManagedKey_IsDisposedEvenWhenHostIsUnreachable()
    {
        var keyPath = TempKeyFile();
        var keyStore = KeyStoreMaterializing(keyPath);
        var runner = RunnerReturning("", "", psSuccess: false, psStderr: "boom");
        var (command, _, _, _) = Build(ConfigWith(Hetzner), runner, keyStore);

        await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        File.Exists(keyPath).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownTarget_ExitsOne()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, stdout, stderr) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "nope", Format = "json" });

        exit.Should().Be(1);
        stdout.ToString().Should().BeEmpty("an error must not be mistaken for a document");
        stderr.ToString().Should().Contain("nope");
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ActionGuard_IsRequiredForSshConnect_AndDenialExitsOne()
    {
        var guard = new Mock<IActionGuard>();
        guard.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ActionGuardDeniedException(ActionIds.SshConnect, "second factor refused"));
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, stdout, stderr) = Build(ConfigWith(Hetzner), runner, guard: guard);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(1);
        guard.Verify(x => x.RequireAsync(It.Is<ActionRequest>(r => r.ActionId == ActionIds.SshConnect), It.IsAny<CancellationToken>()), Times.Once);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("second factor refused");
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProbeFailure_IsPerContainer_AndDoesNotAbortTheList()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", "BEGIN\\tabc123def456\nOCI runtime exec failed\nEND\\tabc123def456\t126\n");
        var (command, _, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var c = doc.RootElement.GetProperty("containers")[0];
        c.GetProperty("sessions").GetArrayLength().Should().Be(0);
        c.GetProperty("probeError").GetString().Should().Contain("OCI runtime exec failed");
    }

    [Fact]
    public async Task ProbeCommandFailure_MarksEveryContainer_AndStillReturnsTheList()
    {
        var runner = new Mock<ISshCommandRunner>();
        runner.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("docker ps")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 0, StdOut = DockerPsLine + "\n" });
        runner.Setup(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.Is<string>(c => c.StartsWith("echo ")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SshCommandResult { ExitCode = 255, StdErr = "ssh dropped" });
        var (command, _, stdout, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "json" });

        exit.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("reachable").GetBoolean().Should().BeTrue();
        var c = doc.RootElement.GetProperty("containers")[0];
        c.GetProperty("probeError").GetString().Should().Contain("ssh dropped");
    }

    [Fact]
    public async Task InvalidFormat_ExitsOne()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, _, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "yaml" });

        exit.Should().Be(1);
    }

    [Fact]
    public async Task TableMode_RendersSessionNamesAndTmux()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, console, _, _) = Build(ConfigWith(Hetzner), runner);

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "hetzner", Format = "table" });

        exit.Should().Be(0);
        console.Output.Should().Contain("vigilant_x");
        console.Output.Should().Contain("heypoul-meeting-mode-notch");
        console.Output.Should().Contain("busy");
        console.Output.Should().Contain("cc-plan-sync");
    }

    [Fact]
    public async Task RemoteLocal_UsesTheLocalShellAndNeverSsh()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, _, stdout, _) = Build(ConfigWith(Hetzner), runner);
        var localCalls = new List<string>();
        command.LocalShellRunner = (cmd, _) =>
        {
            localCalls.Add(cmd);
            return Task.FromResult(cmd.StartsWith("docker ps")
                ? new SshCommandResult { ExitCode = 0, StdOut = DockerPsLine + "\n" }
                : new SshCommandResult { ExitCode = 0, StdOut = ProbeOutput.Replace("\\t", "\t") });
        };

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Remote = "local", Format = "json" });

        exit.Should().Be(0);
        localCalls.Should().HaveCount(2);
        runner.Verify(x => x.RunAsync(It.IsAny<RemoteHostConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("host").GetString().Should().Be("local");
        doc.RootElement.GetProperty("containers")[0].GetProperty("sessions").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task NoRemoteAndNoTargets_FallsBackToLocalWithoutPrompting()
    {
        var runner = RunnerReturning(DockerPsLine + "\n", ProbeOutput);
        var (command, console, stdout, _) = Build(ConfigWith(), runner);
        command.LocalShellRunner = (cmd, _) => Task.FromResult(new SshCommandResult { ExitCode = 0, StdOut = cmd.StartsWith("docker ps") ? "" : "" });

        var exit = await command.ExecuteAsync(null!, new DevcontainerSessionsCommand.Settings { Format = "json" });

        exit.Should().Be(0);
        console.Output.Should().NotContain("Which host?");
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("host").GetString().Should().Be("local");
        doc.RootElement.GetProperty("containers").GetArrayLength().Should().Be(0);
    }
}
