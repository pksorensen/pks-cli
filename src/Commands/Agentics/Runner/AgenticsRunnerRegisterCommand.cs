using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Register an agentics runner for an owner/project
/// </summary>
public class AgenticsRunnerRegisterCommand(
    IAgenticsRunnerConfigurationService configService,
    IGitHubAuthenticationService githubAuth,
    GitHubAuthConfig authConfig,
    IAnsiConsole console) : Command<AgenticsRunnerRegisterCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
                ?? Environment.GetEnvironmentVariable("AGENTICS_SERVER")
                ?? Environment.GetEnvironmentVariable("AGENTIC_SERVER")
                ?? "agentics.dk";

            string serverUrl;
            if (serverHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                serverHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                serverUrl = serverHost.TrimEnd('/');
            }
            else
            {
                var scheme = serverHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                             serverHost.StartsWith("127.0.0.1")
                    ? "http"
                    : "https";
                serverUrl = $"{scheme}://{serverHost}";
            }

            // 3. Resolve runner name
            var runnerName = settings.Name ?? System.Net.Dns.GetHostName();

            if (settings.Verbose)
            {
                DisplayInfo($"Owner: {owner}");
                DisplayInfo($"Project: {project}");
                DisplayInfo($"Server: {serverUrl}");
                DisplayInfo($"Runner name: {runnerName}");
            }

            console.WriteLine();

            // 4. POST to register endpoint
            RegisterRunnerResponse? response = null;
            string? registerError = null;

            await console.Status()
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
                        response = JsonSerializer.Deserialize<RegisterRunnerResponse>(json, JsonOptions);
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

            // 5. Fetch project info to get gitUrl
            string? gitUrl = null;
            string? fetchProjectError = null;

            await console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching project info...", async _ =>
                {
                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {response.Token}");
                        var projectRes = await httpClient.GetAsync(
                            $"{serverUrl}/api/owners/{owner}/projects/{project}");
                        if (projectRes.IsSuccessStatusCode)
                        {
                            var json = await projectRes.Content.ReadAsStringAsync();
                            var projectData = JsonSerializer.Deserialize<ProjectInfoResponse>(json, JsonOptions);
                            gitUrl = projectData?.GitUrl;
                        }
                        else
                        {
                            fetchProjectError = $"Could not fetch project info ({(int)projectRes.StatusCode})";
                        }
                    }
                    catch (Exception ex)
                    {
                        fetchProjectError = ex.Message;
                    }
                });

            if (fetchProjectError != null)
                DisplayInfo($"[yellow]Note: {fetchProjectError}[/]");

            // 6. Save to config
            var registration = new AgenticsRunnerRegistration
            {
                Id = response.Id ?? Guid.NewGuid().ToString(),
                Name = response.Name ?? runnerName,
                Token = response.Token ?? "",
                Owner = owner,
                Project = project,
                Server = serverUrl,
                GitUrl = gitUrl,
                RegisteredAt = DateTime.UtcNow
            };

            await configService.AddRegistrationAsync(registration);

            console.WriteLine();

            // 7. Display success table
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

            console.Write(table);
            console.WriteLine();
            DisplaySuccess($"Runner '{registration.Name}' registered for {owner}/{project}.");

            // 8. Set up GitHub access if the project repo is on github.com
            if (!string.IsNullOrEmpty(gitUrl) && gitUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                console.WriteLine();
                var isAuthenticated = await githubAuth.IsAuthenticatedAsync();
                var authSucceeded = false;

                if (isAuthenticated)
                {
                    console.MarkupLine("[green]GitHub access already configured ✓[/]");
                    authSucceeded = true;
                }
                else
                {
                    var panel = new Panel(
                        $"[cyan]The project repo is on GitHub:[/] {gitUrl}\n\n" +
                        "Authorize this runner to clone the repo without prompting for credentials.")
                        .BorderStyle(Style.Parse("cyan"))
                        .Header("[bold]GitHub Access Setup[/]");
                    console.Write(panel);
                    console.WriteLine();

                    GitHubDeviceAuthStatus? authResult = null;
                    string? authError = null;

                    // Use a synchronous IProgress<T> so the URL/code prints immediately
                    // on the same thread — Progress<T> posts to ThreadPool which can delay
                    // or reorder output relative to the polling loop.
                    var codeShown = false;
                    var progress = new SyncProgress<GitHubAuthProgress>(p =>
                    {
                        if (p.CurrentStep == GitHubAuthStep.WaitingForUserAuthorization
                            && !string.IsNullOrEmpty(p.UserCode) && !codeShown)
                        {
                            codeShown = true;
                            var url = !string.IsNullOrEmpty(p.VerificationUrl)
                                ? p.VerificationUrl
                                : "https://github.com/login/device";
                            console.MarkupLine($"[yellow]1.[/] Open:  [cyan]{url}[/]");
                            console.MarkupLine($"[yellow]2.[/] Enter: [bold white]{p.UserCode}[/]");
                            console.WriteLine();
                            console.MarkupLine("[dim]Waiting for you to authorize in the browser...[/]");
                        }
                        else if (!string.IsNullOrEmpty(p.StatusMessage) && settings.Verbose)
                        {
                            console.MarkupLine($"[dim]{p.StatusMessage.EscapeMarkup()}[/]");
                        }
                    });

                    try
                    {
                        authResult = await githubAuth.AuthenticateAsync(
                            scopes: null,
                            progressCallback: progress);
                    }
                    catch (Exception ex)
                    {
                        authError = ex.Message;
                    }

                    if (authError != null || authResult?.IsAuthenticated != true)
                    {
                        var reason = authError ?? authResult?.ErrorDescription ?? "Authorization not completed";
                        console.MarkupLine($"[yellow]GitHub auth skipped ({reason.EscapeMarkup()}).[/]");
                        console.MarkupLine("[dim]Re-run 'pks agentics runner register' to retry.[/]");
                    }
                    else
                    {
                        console.MarkupLine("[green]GitHub access configured — runner will clone repos without prompting ✓[/]");
                        authSucceeded = true;
                    }
                }

                // Verify the token actually has access to this specific repo
                if (authSucceeded)
                {
                    await VerifyRepoAccessAsync(gitUrl);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to register runner: {ex.Message}");
            if (settings.Verbose)
            {
                console.WriteException(ex);
            }
            return 1;
        }
    }

    private async Task VerifyRepoAccessAsync(string gitUrl)
    {
        var parsed = ParseGitHubOwnerRepo(gitUrl);
        if (parsed == null) return;
        var (repoOwner, repoName) = parsed.Value;

        var storedToken = await githubAuth.GetStoredTokenAsync();
        if (storedToken?.AccessToken == null) return;

        console.WriteLine();

        bool hasAccess;
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {storedToken.AccessToken}");
            http.DefaultRequestHeaders.Add("User-Agent", "PKS-CLI/1.0");
            var res = await http.GetAsync($"https://api.github.com/repos/{repoOwner}/{repoName}");
            hasAccess = res.IsSuccessStatusCode;
        }
        catch
        {
            // Network issue — skip silently
            return;
        }

        if (hasAccess)
        {
            console.MarkupLine($"[green]✓ Repo access confirmed: {repoOwner}/{repoName}[/]");
        }
        else
        {
            console.WriteLine();
            var installUrl = !string.IsNullOrEmpty(authConfig.AppSlug)
                ? $"https://github.com/apps/{authConfig.AppSlug}/installations/new"
                : "https://github.com/settings/installations";

            var panel = new Panel(
                $"[yellow]The authenticated user does not have access to [bold]{repoOwner}/{repoName}[/].[/]\n\n" +
                $"This usually means the [bold]GitHub App[/] is not installed for the [bold]{repoOwner}[/] organization or repository.\n\n" +
                $"[cyan]Install the app here:[/]\n  {installUrl}\n\n" +
                "[dim]After installing, re-run 'pks agentics runner register' to verify access.[/]")
                .BorderStyle(Style.Parse("yellow"))
                .Header("[bold yellow]GitHub App Access Required[/]");
            console.Write(panel);
        }
    }

    private static (string Owner, string Repo)? ParseGitHubOwnerRepo(string gitUrl)
    {
        // Handles: https://github.com/owner/repo, https://github.com/owner/repo.git, git@github.com:owner/repo.git
        try
        {
            string path;
            if (gitUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            {
                path = gitUrl["git@github.com:".Length..];
            }
            else
            {
                var uri = new Uri(gitUrl);
                path = uri.AbsolutePath.TrimStart('/');
            }

            path = path.TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return (parts[0], parts[1]);
        }
        catch { }
        return null;
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Agentics Runner Register[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        console.Write(panel);
        console.WriteLine();
    }

    private void DisplaySuccess(string message) =>
        console.MarkupLine($"[green]{message}[/]");

    private void DisplayError(string message) =>
        console.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

    private void DisplayInfo(string message) =>
        console.MarkupLine($"[cyan]{message}[/]");

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

    private class ProjectInfoResponse
    {
        public string? GitUrl { get; set; }
    }

    /// <summary>Calls the handler synchronously on Report() — unlike Progress&lt;T&gt; which posts to the thread pool.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
