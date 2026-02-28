using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Request a graceful shutdown of the runner daemon
/// </summary>
public class RunnerStopCommand : RunnerCommand<RunnerStopCommand.Settings>
{
    private readonly IRunnerDaemonService _daemonService;

    public RunnerStopCommand(
        IRunnerDaemonService daemonService,
        IAnsiConsole console)
        : base(console)
    {
        _daemonService = daemonService ?? throw new ArgumentNullException(nameof(daemonService));
    }

    public class Settings : RunnerSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Stop");

            var status = _daemonService.GetStatus();

            if (!status.IsRunning)
            {
                DisplayWarning("Runner daemon is not currently running.");
                return 0;
            }

            DisplayInfo("Requesting graceful shutdown...");

            if (status.ActiveJobs.Any())
            {
                DisplayWarning($"{status.ActiveJobs.Count} active job(s) will be allowed to finish before shutdown.");
            }

            _daemonService.RequestShutdown();

            DisplaySuccess("Shutdown requested. The daemon will stop after active jobs complete.");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to stop daemon: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
