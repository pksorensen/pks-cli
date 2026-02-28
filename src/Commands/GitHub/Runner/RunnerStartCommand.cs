using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Start the runner daemon to poll for and execute queued workflow runs
/// </summary>
public class RunnerStartCommand : RunnerCommand<RunnerStartCommand.Settings>
{
    private readonly IRunnerDaemonService _daemonService;
    private readonly IRunnerContainerService _containerService;
    private readonly IRunnerConfigurationService _configService;
    private readonly IGitHubAuthenticationService _authService;

    public RunnerStartCommand(
        IRunnerDaemonService daemonService,
        IRunnerContainerService containerService,
        IRunnerConfigurationService configService,
        IGitHubAuthenticationService authService,
        IAnsiConsole console)
        : base(console)
    {
        _daemonService = daemonService ?? throw new ArgumentNullException(nameof(daemonService));
        _containerService = containerService ?? throw new ArgumentNullException(nameof(containerService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    public class Settings : RunnerSettings
    {
        [CommandOption("--max-jobs <MAX_JOBS>")]
        [Description("Maximum number of concurrent jobs (default: 1)")]
        public int? MaxJobs { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Start");

            // 1. Pre-flight: authentication
            DisplayInfo("Running pre-flight checks...");

            var isAuthenticated = await WithSpinnerAsync("Checking authentication...", async () =>
                await _authService.IsAuthenticatedAsync());

            if (!isAuthenticated)
            {
                DisplayError("Not authenticated with GitHub.");
                DisplayWarning("Run 'pks github runner register --repo owner/repo' to authenticate and register a repository.");
                return 1;
            }

            DisplaySuccess("Authenticated with GitHub");

            // 2. Pre-flight: Docker and devcontainer CLI
            var (dockerOk, devcontainerCliOk, prereqError) = await WithSpinnerAsync(
                "Checking Docker and devcontainer CLI...", async () =>
                    await _containerService.CheckPrerequisitesAsync());

            if (!dockerOk)
            {
                DisplayError("Docker is not available.");
                if (!string.IsNullOrEmpty(prereqError))
                    DisplayWarning(prereqError);
                return 1;
            }

            if (!devcontainerCliOk)
            {
                DisplayError("devcontainer CLI is not installed.");
                DisplayWarning("Install it with: npm install -g @devcontainers/cli");
                return 1;
            }

            DisplaySuccess("Docker and devcontainer CLI available");

            // 3. Load registrations (optionally filter by --repo)
            var registrations = await WithSpinnerAsync("Loading registrations...", async () =>
                await _configService.ListRegistrationsAsync());

            if (!string.IsNullOrEmpty(settings.Repository))
            {
                var (owner, repo) = ParseRepository(settings.Repository);
                registrations = registrations
                    .Where(r =>
                        string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Repository, repo, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            var enabledRegistrations = registrations.Where(r => r.Enabled).ToList();

            if (!enabledRegistrations.Any())
            {
                DisplayError("No enabled runner registrations found.");
                DisplayWarning("Use 'pks github runner register --repo owner/repo' to add one.");
                return 1;
            }

            // Apply --max-jobs override if specified
            if (settings.MaxJobs.HasValue && settings.MaxJobs.Value > 0)
            {
                var config = await _configService.LoadAsync();
                config.MaxConcurrentJobs = settings.MaxJobs.Value;
                await _configService.SaveAsync(config);
                DisplaySuccess($"Found {enabledRegistrations.Count} enabled registration(s) (max {settings.MaxJobs.Value} concurrent jobs)");
            }
            else
            {
                DisplaySuccess($"Found {enabledRegistrations.Count} enabled registration(s)");
            }
            Console.WriteLine();

            // 4. Set up log file
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pks-cli");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "runner.log");
            DisplayInfo($"Detailed logs: {logPath}");
            DisplayInfo("Starting daemon... Press Ctrl+C to stop.");
            Console.WriteLine();

            // Ring buffer of recent events for the live table
            var recentEvents = new List<(DateTime Time, string Message, string Color)>();
            var maxEvents = 12;
            var eventsLock = new object();

            void AddEvent(string message, string color = "dim")
            {
                lock (eventsLock)
                {
                    recentEvents.Add((DateTime.UtcNow, message, color));
                    if (recentEvents.Count > maxEvents)
                        recentEvents.RemoveAt(0);
                }

                // Append to log file
                try
                {
                    File.AppendAllText(logPath,
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}\n");
                }
                catch
                {
                    // Don't let log writing crash the daemon
                }
            }

            // 5. Wire up daemon events
            _daemonService.JobStarted += (_, job) =>
            {
                AddEvent(
                    $"Job started: {job.Registration.Owner}/{job.Registration.Repository} run #{job.RunId}",
                    "green");
            };

            _daemonService.JobCompleted += (_, job) =>
            {
                var color = job.Status == RunnerJobStatus.Completed ? "green" : "red";
                AddEvent(
                    $"Job {job.Status}: {job.Registration.Owner}/{job.Registration.Repository} run #{job.RunId}",
                    color);
            };

            _daemonService.StatusChanged += (_, message) =>
            {
                // Skip noisy polling messages in the event log, keep them in the file only
                if (message.StartsWith("Polled "))
                {
                    try
                    {
                        File.AppendAllText(logPath,
                            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}\n");
                    }
                    catch { }
                    return;
                }

                AddEvent(message);
            };

            // 6. Start daemon with clean live display
            using var cts = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                AddEvent("Shutdown requested, finishing active jobs...", "yellow");
                _daemonService.RequestShutdown();
                cts.Cancel();
            };

            await Console.Live(BuildStatusTable(_daemonService.GetStatus(), recentEvents, eventsLock))
                .AutoClear(true)
                .StartAsync(async ctx =>
                {
                    // Refresh the live display periodically
                    var refreshTask = Task.Run(async () =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(1000, cts.Token);
                                ctx.UpdateTarget(BuildStatusTable(_daemonService.GetStatus(), recentEvents, eventsLock));
                                ctx.Refresh();
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }, cts.Token);

                    try
                    {
                        await _daemonService.RunAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on shutdown
                    }

                    // Wait for refresh to finish
                    try { await refreshTask; } catch (OperationCanceledException) { }
                });

            Console.WriteLine();
            DisplaySuccess("Runner daemon stopped.");

            // Display final summary
            var finalStatus = _daemonService.GetStatus();
            DisplayInfo($"Total jobs completed: {finalStatus.TotalJobsCompleted}");
            DisplayInfo($"Total jobs failed: {finalStatus.TotalJobsFailed}");
            DisplayInfo($"Full log: {logPath}");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Runner daemon failed: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }

    private static Table BuildStatusTable(
        RunnerDaemonStatus status,
        List<(DateTime Time, string Message, string Color)> recentEvents,
        object eventsLock)
    {
        var table = new Table()
            .Title("[cyan bold]Runner Daemon[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .Expand();

        table.AddColumn(new TableColumn("[yellow]Property[/]").Width(20));
        table.AddColumn(new TableColumn("[cyan]Value[/]"));

        var runningText = status.IsRunning ? "[green]Running[/]" : "[red]Stopped[/]";
        var uptime = status.StartedAt.HasValue
            ? (DateTime.UtcNow - status.StartedAt.Value).ToString(@"hh\:mm\:ss")
            : "-";

        table.AddRow("Status", runningText);
        table.AddRow("Uptime", uptime);
        table.AddRow("Active / Done / Failed",
            $"[bold]{status.ActiveJobs.Count}[/] / [green]{status.TotalJobsCompleted}[/] / [red]{status.TotalJobsFailed}[/]");

        // Active jobs
        if (status.ActiveJobs.Any())
        {
            table.AddEmptyRow();
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

                table.AddRow(
                    $"[{statusColor}]{job.Status}[/]",
                    $"{job.Registration.Owner}/{job.Registration.Repository} #{job.RunId}");
            }
        }

        // Recent events
        List<(DateTime Time, string Message, string Color)> events;
        lock (eventsLock)
        {
            events = new List<(DateTime, string, string)>(recentEvents);
        }

        if (events.Any())
        {
            table.AddEmptyRow();
            table.AddRow("[bold yellow]Recent Activity[/]", "");

            foreach (var evt in events)
            {
                var time = evt.Time.ToString("HH:mm:ss");
                table.AddRow(
                    $"[dim]{time}[/]",
                    $"[{evt.Color}]{evt.Message.EscapeMarkup()}[/]");
            }
        }

        return table;
    }
}
