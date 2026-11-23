using FluentAssertions;
using Moq;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// `runner start` with no --project starts one detached runner per saved registration. It used to
/// refuse outright ("pass --project to say which one"), which made the saved list — already an
/// opt-in list — unusable as one.
/// </summary>
public class AgenticsRunnerStartCommandTests
{
    [Fact]
    public async Task Starts_one_detached_runner_per_registration()
    {
        var harness = new Harness(Reg("pksorensen", "pksorensen"), Reg("pksorensen", "agentics-wire"));

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings());

        exit.Should().Be(0);
        harness.Started.Should().BeEquivalentTo("pksorensen/pksorensen", "pksorensen/agentics-wire");
        harness.Upserted.Should().HaveCount(2, "each runner is recorded so list/logs/stop can find it");
    }

    [Fact]
    public async Task Repeated_project_starts_only_those_named()
    {
        var harness = new Harness(Reg("demo", "a"), Reg("demo", "b"), Reg("demo", "c"));

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings
        {
            Projects = ["demo/a", "demo/c"],
        });

        exit.Should().Be(0);
        harness.Started.Should().BeEquivalentTo("demo/a", "demo/c");
    }

    [Fact]
    public async Task One_project_failing_does_not_strand_the_others()
    {
        // A stale token on project two is not a reason to leave project three unserved.
        var harness = new Harness(Reg("demo", "a"), Reg("demo", "b"), Reg("demo", "c"));
        harness.FailFor = "demo/b";

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings());

        exit.Should().Be(1, "the exit code still reports that something failed");
        harness.Started.Should().BeEquivalentTo("demo/a", "demo/c");
        harness.Console.Output.Should().Contain("demo/b");
    }

    [Fact]
    public async Task Already_running_counts_as_success_so_start_is_idempotent()
    {
        var harness = new Harness(Reg("demo", "a"));
        harness.Alive.Add("demo/a");

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings());

        exit.Should().Be(0);
        harness.Started.Should().BeEmpty("the running runner is left alone without --restart");
    }

    [Fact]
    public async Task Refuses_a_shared_work_dir_across_several_projects()
    {
        // Two runners checking out into one directory would overwrite each other. Also covers
        // repeated --project, where the count is only known after resolution.
        var harness = new Harness(Reg("demo", "a"), Reg("demo", "b"));

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings { WorkDir = "/tmp/shared" });

        exit.Should().Be(1);
        harness.Started.Should().BeEmpty();
        harness.Console.Output.Should().Contain("--work-dir");
    }

    [Fact]
    public async Task Asks_for_a_registration_when_none_is_saved()
    {
        var harness = new Harness();

        var exit = await harness.RunAsync(new AgenticsRunnerStartCommand.Settings());

        exit.Should().Be(1);
        harness.Console.Output.Should().Contain("--project");
    }

    private static AgenticsRunnerRegistration Reg(string owner, string project) => new()
    {
        Id = $"{owner}-{project}",
        Name = $"runner-{project}",
        Owner = owner,
        Project = project,
        Server = "https://agentics.dk",
        Token = "not-a-real-token",
    };

    private sealed class Harness
    {
        public TestConsole Console { get; } = new();
        public List<string> Started { get; } = [];
        public List<LocalRunnerRecord> Upserted { get; } = [];
        public HashSet<string> Alive { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FailFor { get; set; }

        private readonly AgenticsRunnerStartCommand _command;

        public Harness(params AgenticsRunnerRegistration[] registrations)
        {
            var config = new Mock<IAgenticsRunnerConfigurationService>();
            config.Setup(c => c.ListRegistrationsAsync()).ReturnsAsync(registrations.ToList());

            var store = new Mock<ILocalRunnerStore>();
            store.Setup(s => s.FindAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string owner, string project, CancellationToken _) =>
                    Alive.Contains($"{owner}/{project}")
                        ? new LocalRunnerRecord { Owner = owner, Project = project, Mode = LocalRunnerMode.Tmux, TmuxSession = "s" }
                        : null);
            store.Setup(s => s.UpsertAsync(It.IsAny<LocalRunnerRecord>(), It.IsAny<CancellationToken>()))
                .Callback((LocalRunnerRecord r, CancellationToken _) => Upserted.Add(r))
                .Returns(Task.CompletedTask);

            var supervisor = new Mock<ILocalRunnerSupervisor>();
            supervisor.Setup(s => s.IsAliveAsync(It.IsAny<LocalRunnerRecord>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((LocalRunnerRecord r, CancellationToken _) => Alive.Contains(r.OwnerProject));
            supervisor.Setup(s => s.StartDetachedAsync(It.IsAny<LocalRunnerStartRequest>(), It.IsAny<CancellationToken>()))
                .Returns((LocalRunnerStartRequest request, CancellationToken _) =>
                {
                    var key = $"{request.Owner}/{request.Project}";
                    if (key == FailFor)
                        throw new InvalidOperationException("tmux refused");

                    Started.Add(key);
                    return Task.FromResult(new LocalRunnerRecord
                    {
                        Owner = request.Owner,
                        Project = request.Project,
                        Server = request.Server,
                        Mode = LocalRunnerMode.Tmux,
                        TmuxSession = $"pks-{request.Project}",
                        WorkDir = request.WorkDir,
                    });
                });

            // Authenticated, so the preflight prints nothing and the tests read the start output.
            var github = new Mock<IGitHubAuthenticationService>();
            github.Setup(g => g.IsAuthenticatedAsync()).ReturnsAsync(true);

            _command = new AgenticsRunnerStartCommand(config.Object, store.Object, supervisor.Object, github.Object, Console);
        }

        public Task<int> RunAsync(AgenticsRunnerStartCommand.Settings settings) =>
            _command.ExecuteAsync(new CommandContext(Mock.Of<IRemainingArguments>(), "start", null), settings);
    }
}
