using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Start the runner daemon to poll for and execute jobs
/// </summary>
public class AgenticsRunnerStartCommand : Command<AgenticsRunnerStartCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGitHubAuthenticationService _githubAuth;
    private readonly IAnsiConsole _console;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public class Settings : AgenticsRunnerSettings
    {
        [CommandOption("--polling-interval <SECONDS>")]
        [Description("Polling interval in seconds (default: 10)")]
        [DefaultValue(10)]
        public int PollingInterval { get; set; } = 10;
    }

    public AgenticsRunnerStartCommand(
        IAgenticsRunnerConfigurationService configService,
        IDevcontainerSpawnerService spawnerService,
        IHttpClientFactory httpClientFactory,
        IGitHubAuthenticationService githubAuth,
        IAnsiConsole console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _githubAuth = githubAuth ?? throw new ArgumentNullException(nameof(githubAuth));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner();

            // Load first/default registration
            var registrations = await _configService.ListRegistrationsAsync();
            if (registrations.Count == 0)
            {
                DisplayError("No runner registrations found. Run 'pks agentics runner register <owner/project>' first.");
                return 1;
            }

            var registration = registrations[0];

            if (settings.Verbose)
            {
                DisplayInfo($"Using registration: {registration.Name} ({registration.Id})");
                DisplayInfo($"Owner: {registration.Owner}/{registration.Project}");
                DisplayInfo($"Server: {registration.Server}");
                DisplayInfo($"Polling interval: {settings.PollingInterval}s");
            }

            _console.WriteLine();
            DisplayInfo($"Starting runner daemon for [cyan]{registration.Owner}/{registration.Project}[/]");
            DisplayInfo("Press Ctrl+C to stop.");
            _console.WriteLine();

            // Start credential server (serves locally stored device-code OAuth token)
            await using var credentialServer = new GitCredentialServer(_githubAuth, registration.Id);
            await credentialServer.StartAsync();

            if (settings.Verbose)
            {
                DisplayInfo($"Credential server started at: {credentialServer.SocketPath}");
            }

            // Set up cancellation
            using var cts = new CancellationTokenSource();
            System.Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                DisplayInfo("Shutdown requested...");
                cts.Cancel();
            };

            // Polling loop
            var jobsProcessed = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var job = await PollForJobAsync(registration, cts.Token);
                    if (job != null)
                    {
                        _console.MarkupLine($"[green]Job received:[/] {job.Id}");

                        var spawnOptions = BuildSpawnOptions(job, credentialServer.SocketPath);
                        var spawnResult = await _spawnerService.SpawnLocalAsync(spawnOptions, msg =>
                        {
                            if (settings.Verbose)
                                _console.MarkupLine($"[dim]{msg.EscapeMarkup()}[/]");
                        });

                        if (spawnResult.Success)
                        {
                            jobsProcessed++;
                            _console.MarkupLine($"[green]Job completed successfully.[/] Container: {spawnResult.ContainerId}");
                        }
                        else
                        {
                            _console.MarkupLine($"[red]Job failed:[/] {spawnResult.Message.EscapeMarkup()}");
                            foreach (var err in spawnResult.Errors)
                                _console.MarkupLine($"  [red]- {err.EscapeMarkup()}[/]");
                        }
                    }
                    else
                    {
                        if (settings.Verbose)
                            _console.MarkupLine($"[dim]{DateTime.UtcNow:HH:mm:ss} No jobs available, waiting {settings.PollingInterval}s...[/]");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Polling error:[/] {ex.Message.EscapeMarkup()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollingInterval), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _console.WriteLine();
            DisplaySuccess($"Runner daemon stopped. Jobs processed: {jobsProcessed}");
            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Runner daemon failed: {ex.Message}");
            if (settings.Verbose)
                _console.WriteException(ex);
            return 1;
        }
    }

    private async Task<RunnerJob?> PollForJobAsync(AgenticsRunnerRegistration registration, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", registration.Token);

        var response = await client.PostAsJsonAsync(
            $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs",
            new { },
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
            response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        return JsonSerializer.Deserialize<RunnerJob>(json, JsonOptions);
    }

    private static DevcontainerSpawnOptions BuildSpawnOptions(RunnerJob job, string credentialSocketPath)
    {
        return new DevcontainerSpawnOptions
        {
            ProjectName = job.ProjectName ?? job.Id,
            ProjectPath = job.ProjectPath ?? "/tmp",
            DevcontainerPath = job.DevcontainerPath ?? string.Empty,
            LaunchVsCode = false,
            ReuseExisting = false,
            UseBootstrapContainer = true,
            CredentialSocketPath = credentialSocketPath
        };
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Agentics Runner Start[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();
    }

    private void DisplaySuccess(string message) =>
        _console.MarkupLine($"[green]{message}[/]");

    private void DisplayError(string message) =>
        _console.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

    private void DisplayInfo(string message) =>
        _console.MarkupLine($"[cyan]{message}[/]");

    /// <summary>Minimal job model returned by the server's /runners/jobs endpoint.</summary>
    private class RunnerJob
    {
        public string Id { get; set; } = "";
        public string? ProjectName { get; set; }
        public string? ProjectPath { get; set; }
        public string? DevcontainerPath { get; set; }
    }
}
