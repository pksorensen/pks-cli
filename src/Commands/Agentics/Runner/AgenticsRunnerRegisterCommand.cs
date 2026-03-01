using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Register an agentics runner for an owner/project
/// </summary>
public class AgenticsRunnerRegisterCommand : Command<AgenticsRunnerRegisterCommand.Settings>
{
    private readonly IAgenticsRunnerConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public AgenticsRunnerRegisterCommand(
        IAgenticsRunnerConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public class Settings : AgenticsRunnerSettings
    {
        [CommandArgument(0, "<OWNER_PROJECT>")]
        [Description("Owner/project in owner/project format")]
        public string OwnerProject { get; set; } = "";

        [CommandOption("--name <NAME>")]
        [Description("Runner name (defaults to hostname)")]
        public string? Name { get; set; }

        [CommandOption("--server <SERVER>")]
        [Description("Agentics server URL (falls back to AGENTIC_SERVER env, then agentics.dk)")]
        public string? Server { get; set; }
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

            // 1. Parse owner/project from positional argument
            var (owner, project) = ParseOwnerProject(settings.OwnerProject);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(project))
            {
                DisplayError("Owner/project must be specified in owner/project format.");
                return 1;
            }

            // 2. Resolve server URL
            var serverHost = settings.Server
                ?? Environment.GetEnvironmentVariable("AGENTIC_SERVER")
                ?? "agentics.dk";

            var scheme = serverHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                         serverHost.StartsWith("127.0.0.1")
                ? "http"
                : "https";

            var serverUrl = $"{scheme}://{serverHost}";

            // 3. Resolve runner name
            var runnerName = settings.Name ?? System.Net.Dns.GetHostName();

            if (settings.Verbose)
            {
                DisplayInfo($"Owner: {owner}");
                DisplayInfo($"Project: {project}");
                DisplayInfo($"Server: {serverUrl}");
                DisplayInfo($"Runner name: {runnerName}");
            }

            _console.WriteLine();

            // 4. POST to register endpoint
            RegisterRunnerResponse? response = null;
            string? registerError = null;

            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Registering runner...", async _ =>
                {
                    try
                    {
                        using var httpClient = new HttpClient();
                        var requestBody = new { name = runnerName, labels = Array.Empty<string>() };
                        var httpResponse = await httpClient.PostAsJsonAsync(
                            $"{serverUrl}/api/owners/{owner}/projects/{project}/runners",
                            requestBody);

                        if (!httpResponse.IsSuccessStatusCode)
                        {
                            var errorBody = await httpResponse.Content.ReadAsStringAsync();
                            registerError = $"Server returned {(int)httpResponse.StatusCode}: {errorBody}";
                            return;
                        }

                        var json = await httpResponse.Content.ReadAsStringAsync();
                        response = JsonSerializer.Deserialize<RegisterRunnerResponse>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    }
                    catch (Exception ex)
                    {
                        registerError = ex.Message;
                    }
                });

            if (registerError != null)
            {
                DisplayError($"Failed to register runner: {registerError}");
                return 1;
            }

            if (response == null)
            {
                DisplayError("Failed to parse server response.");
                return 1;
            }

            // 5. Save to config
            var registration = new AgenticsRunnerRegistration
            {
                Id = response.Id ?? Guid.NewGuid().ToString(),
                Name = response.Name ?? runnerName,
                Token = response.Token ?? "",
                Owner = owner,
                Project = project,
                Server = serverUrl,
                RegisteredAt = DateTime.UtcNow
            };

            await _configService.AddRegistrationAsync(registration);

            _console.WriteLine();

            // 6. Display success table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[yellow]Property[/]")
                .AddColumn("[cyan]Value[/]");

            table.AddRow("ID", registration.Id);
            table.AddRow("Name", registration.Name);
            table.AddRow("Token", registration.Token);
            table.AddRow("Server", registration.Server);
            table.AddRow("Project", $"{registration.Owner}/{registration.Project}");

            _console.Write(table);
            _console.WriteLine();
            DisplaySuccess($"Runner '{registration.Name}' registered for {owner}/{project}.");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to register runner: {ex.Message}");
            if (settings.Verbose)
            {
                _console.WriteException(ex);
            }
            return 1;
        }
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Agentics Runner Register[/]")
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

    private static (string Owner, string Project) ParseOwnerProject(string? ownerProject)
    {
        if (string.IsNullOrEmpty(ownerProject))
            return (string.Empty, string.Empty);

        var parts = ownerProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private class RegisterRunnerResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
