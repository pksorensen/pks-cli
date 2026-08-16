using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class LocalRunnerSupervisorTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "pks-supervisor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _workDir;
    private readonly Mock<IProcessRunner> _processRunner = new(MockBehavior.Strict);

    public LocalRunnerSupervisorTests() => _workDir = Path.Combine(_stateDir, "work");

    private LocalRunnerSupervisor MakeSupervisor() => new(_processRunner.Object, _stateDir);

    private void TmuxIsAvailable() => _processRunner
        .Setup(p => p.RunAsync("tmux", "-V", null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new ProcessResult(0, "tmux 3.3a", ""));

    private LocalRunnerStartRequest Request() => new(
        "pksorensen", "museliving", "https://agentics.dk", _workDir, 10,
        new RunnerLauncherCommand(RunnerLauncherKind.Pks, "/usr/local/bin/pks"));

    [Fact]
    public async Task Detached_start_opens_the_shared_tmux_session_name()
    {
        TmuxIsAvailable();
        string? tmuxArgs = null;
        _processRunner
            .Setup(p => p.RunAsync("tmux", It.Is<string>(a => a.StartsWith("new-session")), _workDir, It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((_, args, _, _) => tmuxArgs = args)
            .ReturnsAsync(new ProcessResult(0, "", ""));

        var record = await MakeSupervisor().StartDetachedAsync(Request());

        // Same name the SSH handoff uses, which is what lets status/logs/stop be one code path.
        record.TmuxSession.Should().Be(RunnerTmuxSession.Name("pksorensen", "museliving"));
        record.Mode.Should().Be(LocalRunnerMode.Tmux);
        tmuxArgs.Should().Contain("new-session -d -s pks-agentics-pksorensen-museliving");
    }

    [Fact]
    public async Task The_detached_runner_is_told_never_to_prompt()
    {
        TmuxIsAvailable();
        _processRunner
            .Setup(p => p.RunAsync("tmux", It.Is<string>(a => a.StartsWith("new-session")), _workDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        await MakeSupervisor().StartDetachedAsync(Request());

        // A tmux pane HAS a TTY, so without this the run command's interactive gates fire and the
        // runner sits on a capability prompt forever instead of polling. Observed once; pinned here.
        var script = await File.ReadAllTextAsync(Path.Combine(_stateDir, "pksorensen-museliving.sh"));
        script.Should().Contain("--no-prompt");
        script.Should().Contain("agentics runner run --project pksorensen/museliving");
        script.Should().Contain("/usr/local/bin/pks");
    }

    [Fact]
    public async Task The_work_dir_is_passed_explicitly_so_it_never_depends_on_cwd()
    {
        TmuxIsAvailable();
        _processRunner
            .Setup(p => p.RunAsync("tmux", It.IsAny<string>(), _workDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        await MakeSupervisor().StartDetachedAsync(Request());

        var script = await File.ReadAllTextAsync(Path.Combine(_stateDir, "pksorensen-museliving.sh"));
        script.Should().Contain($"--work-dir {_workDir}");
    }

    [Fact]
    public async Task A_tmux_refusal_is_an_error_not_a_silently_dead_runner()
    {
        TmuxIsAvailable();
        _processRunner
            .Setup(p => p.RunAsync("tmux", It.Is<string>(a => a.StartsWith("new-session")), _workDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "duplicate session: pks-agentics-pksorensen-museliving"));

        var act = () => MakeSupervisor().StartDetachedAsync(Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*duplicate session*");
    }

    [Fact]
    public async Task Stop_sends_SIGTERM_rather_than_killing_a_runner_mid_job()
    {
        var record = new LocalRunnerRecord
        {
            Owner = "pksorensen", Project = "museliving",
            Mode = LocalRunnerMode.Process, Pid = 4242, WorkDir = _workDir,
        };
        _processRunner
            .Setup(p => p.RunAsync("kill", "-TERM 4242", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        (await MakeSupervisor().StopAsync(record)).Should().BeTrue();

        // SIGKILL would orphan the job's devcontainers -- the runner's shutdown handler is what
        // cleans them up.
        _processRunner.Verify(p => p.RunAsync("kill", "-TERM 4242", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void The_default_work_dir_is_absolute()
    {
        var dir = LocalRunnerSupervisor.DefaultWorkDir("pksorensen", "museliving");

        Path.IsPathRooted(dir).Should().BeTrue();
        dir.Should().EndWith(Path.Combine("agentics-work", "pksorensen-museliving"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDir)) Directory.Delete(_stateDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
