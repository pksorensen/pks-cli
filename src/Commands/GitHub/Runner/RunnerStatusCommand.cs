using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Display the current status of the runner daemon and active jobs
/// </summary>
public class RunnerStatusCommand : RunnerCommand<RunnerStatusCommand.Settings>
{
    private readonly IRunnerDaemonService _daemonService;

    public RunnerStatusCommand(
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
            DisplayBanner("Status");

            var status = _daemonService.GetStatus();

            // Summary table
            var summaryTable = new Table()
                .Title("[cyan]Daemon Summary[/]")
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .AddColumn("[yellow]Property[/]")
                .AddColumn("[cyan]Value[/]");

            summaryTable.AddRow("Running", status.IsRunning ? "[green]Yes[/]" : "[red]No[/]");
            summaryTable.AddRow("Started", status.StartedAt?.ToString("yyyy-MM-dd HH:mm:ss UTC") ?? "[dim]N/A[/]");
            summaryTable.AddRow("Jobs Completed", status.TotalJobsCompleted.ToString());
            summaryTable.AddRow("Jobs Failed", status.TotalJobsFailed.ToString());

            Console.Write(summaryTable);
            Console.WriteLine();

            // Active jobs table
            if (status.ActiveJobs.Any())
            {
                var jobsTable = new Table()
                    .Title($"[cyan]Active Jobs ({status.ActiveJobs.Count})[/]")
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Green)
                    .AddColumn("[yellow]Job ID[/]")
                    .AddColumn("[cyan]Repository[/]")
                    .AddColumn("[magenta]Run ID[/]")
                    .AddColumn("[green]Status[/]")
                    .AddColumn("[blue]Started[/]");

                foreach (var job in status.ActiveJobs)
                {
                    var statusColor = job.Status switch
                    {
                        RunnerJobStatus.Cloning => "blue",
                        RunnerJobStatus.Building => "yellow",
                        RunnerJobStatus.Running => "green",
                        RunnerJobStatus.Cleaning => "dim",
                        _ => "white"
                    };

                    jobsTable.AddRow(
                        job.JobId.Length > 8 ? job.JobId[..8] + "..." : job.JobId,
                        $"{job.Registration.Owner}/{job.Registration.Repository}",
                        job.RunId.ToString(),
                        $"[{statusColor}]{job.Status}[/]",
                        job.StartedAt.ToString("HH:mm:ss"));
                }

                Console.Write(jobsTable);
            }
            else
            {
                DisplayInfo("No active jobs.");
            }

            // Last poll times
            if (settings.Verbose && status.LastPollTimes.Any())
            {
                Console.WriteLine();

                var pollTable = new Table()
                    .Title("[cyan]Last Poll Times[/]")
                    .Border(TableBorder.Rounded)
                    .BorderColor(Color.Yellow)
                    .AddColumn("[yellow]Repository[/]")
                    .AddColumn("[cyan]Last Polled[/]");

                foreach (var (repoKey, lastPoll) in status.LastPollTimes)
                {
                    pollTable.AddRow(repoKey, lastPoll.ToString("yyyy-MM-dd HH:mm:ss UTC"));
                }

                Console.Write(pollTable);
            }

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to get daemon status: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
