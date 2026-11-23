using FluentAssertions;
using PKS.Commands.Agentics.Runner;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// One `runner run` serving every registered project — the GitHub-runner shape: register the
/// projects, start once. Before this, a project-less `run` took the first saved registration and
/// silently served one of six.
///
/// Unlike <see cref="InstallationRunnerHost"/> this mode never asks the platform anything and
/// never mints a credential: the saved registrations are the whole project list, so `register`
/// and `unregister` are the only controls.
/// </summary>
public class LocalMultiProjectRunnerModeTests
{
    [Fact]
    public async Task Starts_one_runner_per_saved_registration()
    {
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"), Reg("pksorensen", "agentics-wire"));
        var launcher = new FakeLauncher();

        await RunPassesAsync(registrations, launcher, passes: 1);

        launcher.Started.Select(r => $"{r.Owner}/{r.Project}")
            .Should().BeEquivalentTo("pksorensen/pksorensen", "pksorensen/agentics-wire");
    }

    [Fact]
    public async Task Picks_up_a_project_registered_while_it_runs()
    {
        // The registrations are re-read every pass precisely so `register` in another shell does
        // not need the supervisor restarted.
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"));
        var launcher = new FakeLauncher();

        await RunPassesAsync(registrations, launcher, passes: 2, beforePass: pass =>
        {
            if (pass == 1)
                registrations.Items.Add(Reg("pksorensen", "agentics-wire"));
        });

        launcher.Started.Select(r => r.Project).Should().BeEquivalentTo("pksorensen", "agentics-wire");
    }

    [Fact]
    public async Task Stops_the_runner_for_a_project_that_is_unregistered()
    {
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"), Reg("pksorensen", "agentics-wire"));
        var launcher = new FakeLauncher();

        // Measured mid-run, not after: cancellation stops every child, so a post-hoc check
        // cannot tell the unregistered one from the rest.
        bool wireStopped = false, keptStopped = true;

        await RunPassesAsync(registrations, launcher, passes: 3, beforePass: pass =>
        {
            if (pass == 1)
            {
                registrations.Items.RemoveAll(r => r.Project == "agentics-wire");
                return;
            }

            wireStopped = Process(launcher, "agentics-wire").Stopped;
            keptStopped = Process(launcher, "pksorensen").Stopped;
        });

        wireStopped.Should().BeTrue();
        keptStopped.Should().BeFalse("only the unregistered project's runner is stopped");
    }

    [Fact]
    public async Task Restarts_a_child_that_exits()
    {
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"));
        var launcher = new FakeLauncher();

        await RunPassesAsync(registrations, launcher, passes: 2, beforePass: pass =>
        {
            if (pass == 1)
                launcher.Processes[0].Exit(1);
        });

        launcher.Started.Should().HaveCount(2, "the first crash is restarted immediately — a one-off costs nothing");
    }

    [Fact]
    public async Task Backs_off_a_child_that_keeps_exiting()
    {
        // The second crash must wait, because the usual cause is a credential this process cannot
        // repair and a 10s retry loop would bury the reason.
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"));
        var launcher = new FakeLauncher();
        var clock = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        await RunPassesAsync(registrations, launcher, passes: 3,
            now: () => clock,
            beforePass: pass =>
            {
                // Crash the newest child on every pass; the clock never advances, so the backoff
                // from the first restart is still in force.
                launcher.Processes[^1].Exit(1);
            });

        launcher.Started.Should().HaveCount(2, "the second exit is inside the backoff window, so no third child");
    }

    [Fact]
    public async Task Does_not_punish_a_child_that_was_up_a_long_time()
    {
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"));
        var launcher = new FakeLauncher();
        var clock = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        await RunPassesAsync(registrations, launcher, passes: 3,
            now: () => clock,
            beforePass: pass =>
            {
                launcher.Processes[^1].Exit(1);
                // Each child lived well past HealthyUptime, so each exit is a fresh failure and
                // the backoff never compounds.
                clock = clock.AddMinutes(10);
            });

        launcher.Started.Should().HaveCount(3);
    }

    [Fact]
    public async Task Refuses_when_nothing_is_registered()
    {
        var console = new TestConsole();
        var exit = await LocalMultiProjectRunnerMode.RunAsync(
            new InMemoryRegistrations(), console, new FakeLauncher(),
            delay: (_, _) => Task.CompletedTask);

        exit.Should().Be(1);
        console.Output.Should().Contain("runner register");
    }

    [Fact]
    public async Task Stops_every_child_on_cancellation()
    {
        var registrations = new InMemoryRegistrations(Reg("pksorensen", "pksorensen"), Reg("pksorensen", "agentics-wire"));
        var launcher = new FakeLauncher();

        var exit = await RunPassesAsync(registrations, launcher, passes: 1);

        exit.Should().Be(0);
        launcher.Processes.Should().OnlyContain(p => p.Stopped);
    }

    private static FakeProcess Process(FakeLauncher launcher, string project) =>
        launcher.Processes[launcher.Started.FindIndex(r => r.Project == project)];

    /// <summary>
    /// Drive the supervisor for a fixed number of reconcile passes, then cancel. The injected
    /// delay is the pass boundary, so no test waits on a real timer.
    /// </summary>
    private static async Task<int> RunPassesAsync(
        InMemoryRegistrations registrations,
        FakeLauncher launcher,
        int passes,
        Action<int>? beforePass = null,
        Func<DateTimeOffset>? now = null)
    {
        using var cts = new CancellationTokenSource();
        var seen = 0;

        return await LocalMultiProjectRunnerMode.RunAsync(
            registrations,
            new TestConsole(),
            launcher,
            delay: (_, _) =>
            {
                seen++;
                if (seen >= passes)
                    cts.Cancel();
                else
                    beforePass?.Invoke(seen);
                return Task.CompletedTask;
            },
            now: now,
            ct: cts.Token);
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

    private sealed class InMemoryRegistrations(params AgenticsRunnerRegistration[] initial) : IAgenticsRunnerConfigurationService
    {
        public List<AgenticsRunnerRegistration> Items { get; } = [.. initial];

        public Task<AgenticsRunnerConfiguration> LoadAsync() => Task.FromResult(new AgenticsRunnerConfiguration { Registrations = Items });
        public Task SaveAsync(AgenticsRunnerConfiguration configuration) => Task.CompletedTask;

        public Task<AgenticsRunnerRegistration> AddRegistrationAsync(AgenticsRunnerRegistration registration)
        {
            Items.RemoveAll(r => r.Owner == registration.Owner && r.Project == registration.Project);
            Items.Add(registration);
            return Task.FromResult(registration);
        }

        public Task<List<AgenticsRunnerRegistration>> ListRegistrationsAsync() => Task.FromResult(Items.ToList());
        public Task<AgenticsRunnerRegistration?> GetRegistrationAsync(string registrationId) => Task.FromResult(Items.FirstOrDefault(r => r.Id == registrationId));
    }

    private sealed class FakeLauncher : IProjectRunnerLauncher
    {
        public List<AgenticsRunnerRegistration> Started { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public IProjectRunnerProcess Start(AgenticsRunnerRegistration registration)
        {
            Started.Add(registration);
            var process = new FakeProcess();
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IProjectRunnerProcess
    {
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public bool Stopped { get; private set; }

        public void Exit(int code) { HasExited = true; ExitCode = code; }

        public Task StopAsync(TimeSpan grace)
        {
            Stopped = true;
            HasExited = true;
            ExitCode = 0;
            return Task.CompletedTask;
        }
    }
}
