using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Register a repository for the self-hosted runner daemon to watch
/// </summary>
public class RunnerRegisterCommand : RunnerCommand<RunnerRegisterCommand.Settings>
{
    private readonly IGitHubAuthenticationService _authService;
    private readonly IGitHubActionsService _actionsService;
    private readonly IRunnerConfigurationService _configService;
    private readonly IGitHubApiClient _apiClient;

    public RunnerRegisterCommand(
        IGitHubAuthenticationService authService,
        IGitHubActionsService actionsService,
        IRunnerConfigurationService configService,
        IGitHubApiClient apiClient,
        IAnsiConsole console)
        : base(console)
    {
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _actionsService = actionsService ?? throw new ArgumentNullException(nameof(actionsService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
    }

    public class Settings : RunnerSettings
    {
        [CommandOption("--labels <LABELS>")]
        [Description("Comma-separated runner labels (default: devcontainer-runner)")]
        public string? Labels { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Register");

            // 1. Validate repository argument
            var (owner, repo) = ParseRepository(settings.Repository);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            {
                DisplayError("Repository must be specified in owner/repo format. Use --repo owner/repo.");
                return 1;
            }

            DisplayInfo($"Repository: {owner}/{repo}");
            Console.WriteLine();

            // 2. Check authentication — run login flow if needed
            var isAuthenticated = await WithSpinnerAsync("Checking authentication...", async () =>
                await _authService.IsAuthenticatedAsync());

            if (!isAuthenticated)
            {
                DisplayWarning("Not authenticated with GitHub. Starting login flow...");
                Console.WriteLine();

                // Step 1: Get device code
                var deviceCode = await _authService.InitiateDeviceCodeFlowAsync();

                Console.MarkupLine($"[yellow]Open:[/]  [link]{deviceCode.VerificationUri}[/]");
                Console.MarkupLine($"[yellow]Code:[/]  [bold cyan]{deviceCode.UserCode}[/]");
                Console.WriteLine();
                DisplayInfo("Waiting for authorization...");

                // Step 2: Poll until user authorizes
                PKS.Infrastructure.Services.Models.GitHubDeviceAuthStatus? authResult = null;
                var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);
                var pollInterval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 5));

                while (DateTime.UtcNow < expiresAt)
                {
                    await Task.Delay(pollInterval);
                    authResult = await _authService.PollForAuthenticationAsync(deviceCode.DeviceCode);

                    if (authResult.IsAuthenticated)
                        break;

                    if (authResult.Error == "authorization_pending" || authResult.Error == "slow_down")
                    {
                        if (authResult.Error == "slow_down")
                            pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5));
                        continue;
                    }

                    // Real error
                    break;
                }

                if (authResult == null || !authResult.IsAuthenticated)
                {
                    var errorDetail = authResult?.ErrorDescription ?? authResult?.Error ?? "authorization timed out";
                    DisplayError($"Authentication failed: {errorDetail}");
                    return 1;
                }

                // Step 3: Store the token
                await _authService.StoreTokenAsync(new PKS.Infrastructure.Services.Models.GitHubStoredToken
                {
                    AccessToken = authResult.AccessToken!,
                    RefreshToken = authResult.RefreshToken,
                    Scopes = authResult.Scopes,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = authResult.ExpiresAt,
                    IsValid = true,
                    LastValidated = DateTime.UtcNow
                });

                DisplaySuccess("Authenticated with GitHub");
            }
            else
            {
                DisplaySuccess("Authenticated with GitHub");
            }

            // 3. Set auth token on API client so subsequent API calls work
            var storedToken = await _authService.GetStoredTokenAsync();
            if (storedToken == null || string.IsNullOrEmpty(storedToken.AccessToken))
            {
                DisplayError("Failed to retrieve stored authentication token.");
                return 1;
            }
            _apiClient.SetAuthenticationToken(storedToken.AccessToken);

            // 4. Check repository access and admin permission
            bool hasAdmin = false;
            string? accessError = null;

            try
            {
                var repoResponse = await WithSpinnerAsync("Checking repository access...", async () =>
                    await _apiClient.GetAsync<PKS.Infrastructure.Services.Models.GitHubRepositoryResponse>(
                        $"repos/{owner}/{repo}"));

                if (repoResponse != null)
                {
                    hasAdmin = repoResponse.Permissions?.Admin ?? false;
                    if (settings.Verbose)
                    {
                        DisplayInfo($"API returned repo: {repoResponse.FullName}");
                        DisplayInfo($"Permissions - admin: {repoResponse.Permissions?.Admin}, push: {repoResponse.Permissions?.Push}, pull: {repoResponse.Permissions?.Pull}");
                    }
                }
                else
                {
                    accessError = "API returned null response";
                }
            }
            catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                accessError = "not_found";
            }
            catch (GitHubApiException ex)
            {
                accessError = $"API error {(int)ex.StatusCode}: {ex.Message}";
            }
            catch (Exception ex)
            {
                accessError = ex.Message;
            }

            if (accessError != null)
            {
                if (accessError == "not_found")
                {
                    Console.WriteLine();
                    DisplayWarning("Cannot access this repository. The GitHub App may not be installed.");
                    Console.WriteLine();
                    Console.MarkupLine("[yellow]Install the app:[/]  [link]https://github.com/apps/agentics-live/installations/select_target[/]");
                    Console.WriteLine();
                    DisplayInfo($"Select the account that owns [bold]{owner}/{repo}[/] and grant access to the repository.");
                    Console.WriteLine();
                    DisplayInfo("Waiting for app installation... (press Ctrl+C to cancel)");

                    // Poll until the app is installed
                    var installTimeout = DateTime.UtcNow.AddMinutes(5);
                    while (DateTime.UtcNow < installTimeout)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));
                        try
                        {
                            var checkResponse = await _apiClient.GetAsync<PKS.Infrastructure.Services.Models.GitHubRepositoryResponse>(
                                $"repos/{owner}/{repo}");
                            if (checkResponse != null)
                            {
                                hasAdmin = checkResponse.Permissions?.Admin ?? false;
                                accessError = null;
                                break;
                            }
                        }
                        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // Still not installed, keep polling
                        }
                    }

                    if (accessError != null)
                    {
                        DisplayError("Timed out waiting for GitHub App installation.");
                        return 1;
                    }

                    DisplaySuccess("GitHub App installed");
                }
                else
                {
                    DisplayError($"Failed to check repository access: {accessError}");
                    return 1;
                }
            }
            else
            {
                DisplaySuccess("Repository accessible");
            }

            if (!hasAdmin)
            {
                DisplayError($"Admin permission not available on {owner}/{repo}.");
                Console.WriteLine();
                DisplayWarning("JIT runner tokens require admin-level access. Check that:");
                DisplayWarning("  1. You have admin access to the repository");
                DisplayWarning("  2. The Agentics Live GitHub App has 'Administration: Read & Write' permission");
                DisplayWarning("  3. The app is installed with access to this specific repository");
                Console.WriteLine();
                Console.MarkupLine("[yellow]App settings:[/]  [link]https://github.com/apps/agentics-live/installations/select_target[/]");
                return 1;
            }

            DisplaySuccess("Admin permission verified");

            // 5. Add registration
            var labels = settings.Labels ?? "devcontainer-runner";
            var registration = await WithSpinnerAsync("Adding registration...", async () =>
                await _configService.AddRegistrationAsync(owner, repo, labels));

            Console.WriteLine();

            // 6. Display success
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[yellow]Property[/]")
                .AddColumn("[cyan]Value[/]");

            table.AddRow("ID", registration.Id);
            table.AddRow("Repository", $"{registration.Owner}/{registration.Repository}");
            table.AddRow("Labels", registration.Labels);
            table.AddRow("Registered", registration.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
            table.AddRow("Enabled", registration.Enabled ? "[green]Yes[/]" : "[red]No[/]");

            Console.Write(table);
            Console.WriteLine();
            DisplaySuccess($"Runner registered for {owner}/{repo}. Use 'pks github runner start' to begin processing jobs.");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to register runner: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
