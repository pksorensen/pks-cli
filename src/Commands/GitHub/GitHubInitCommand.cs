using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub;

/// <summary>
/// Authenticates with GitHub and ensures the Agentics Live App is installed
/// on a given repository so the runner can push to it.
///
/// Usage:
///   pks github init                                           # auth only
///   pks github init https://github.com/owner/repo            # auth + install app on repo
///   pks github init https://github.com/owner/repo --force    # re-auth + install
/// </summary>
[Description("Authenticate with GitHub and install Agentics app on a repository")]
public class GitHubInitCommand : Command<GitHubInitCommand.Settings>
{
    private readonly IGitHubAuthenticationService _authService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;
    private readonly GitHubAuthConfig _config;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public GitHubInitCommand(
        IGitHubAuthenticationService authService,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console,
        GitHubAuthConfig config)
    {
        _authService = authService;
        _httpClientFactory = httpClientFactory;
        _console = console;
        _config = config;
    }

    public class Settings : GitHubSettings
    {
        [CommandArgument(0, "[repoUrl]")]
        [Description("GitHub repository URL to install the Agentics app on (e.g. https://github.com/owner/repo)")]
        public string? RepoUrl { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }

        [CommandOption("-t|--token")]
        [Description("Store a Personal Access Token (ghp_...) directly instead of using device code flow")]
        public string? Token { get; set; }

        [CommandOption("--client-id")]
        [Description("GitHub App or OAuth App client ID to use instead of the default Agentics Live app")]
        public string? ClientId { get; set; }

        [CommandOption("--app-slug")]
        [Description("GitHub App slug (used to build the install URL). Defaults to 'agentics-live'")]
        public string? AppSlug { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // Apply per-invocation app overrides (power users can bring their own GitHub App)
        if (!string.IsNullOrWhiteSpace(settings.ClientId))
            _config.ClientId = settings.ClientId.Trim();
        if (!string.IsNullOrWhiteSpace(settings.AppSlug))
            _config.AppSlug = settings.AppSlug.Trim();

        // ── Step 1: Authenticate ──────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            // PAT path — store directly, skip device code and app-install check
            await _authService.StoreTokenAsync(new GitHubStoredToken
            {
                AccessToken = settings.Token.Trim(),
                RefreshToken = null,
                Scopes = [],
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = null,   // PATs don't expire (or user manages rotation)
                IsValid = true,
                LastValidated = DateTime.UtcNow,
            });
            _console.MarkupLine("[green]✓ Personal Access Token stored.[/]");
            _console.MarkupLine("[dim]Restart the runner to pick up the new git:push capability.[/]");
            return 0;
        }

        if (!settings.Force)
        {
            var already = await _authService.IsAuthenticatedAsync();
            if (already)
            {
                _console.MarkupLine("[green]✓ Already authenticated with GitHub.[/]");
            }
            else
            {
                if (await RunDeviceCodeFlowAsync() != 0) return 1;
            }
        }
        else
        {
            if (await RunDeviceCodeFlowAsync() != 0) return 1;
        }

        // ── Step 2: Repo install (optional) ──────────────────────────────────
        if (string.IsNullOrWhiteSpace(settings.RepoUrl))
        {
            _console.WriteLine();
            _console.MarkupLine("[dim]Tip: run 'pks github init <repo-url>' to also grant the runner push access to a specific repo.[/]");
            _console.MarkupLine("[dim]Restart the runner to pick up the new git:push capability.[/]");
            return 0;
        }

        // Parse owner/repo from URL
        var (owner, repo) = ParseRepoUrl(settings.RepoUrl);
        if (owner is null || repo is null)
        {
            _console.MarkupLine($"[red]✗ Could not parse repository URL: {settings.RepoUrl.EscapeMarkup()}[/]");
            _console.MarkupLine("[dim]Expected format: https://github.com/owner/repo[/]");
            return 1;
        }

        return await EnsureAppInstalledOnRepoAsync(owner, repo);
    }

    // ── Device code flow ─────────────────────────────────────────────────────

    private async Task<int> RunDeviceCodeFlowAsync()
    {
        _console.MarkupLine("[cyan]Starting GitHub device code authentication...[/]");
        _console.WriteLine();

        GitHubDeviceCodeResponse deviceCode;
        try
        {
            deviceCode = await _authService.InitiateDeviceCodeFlowAsync();
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to start device code flow: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        _console.Write(new Panel($"""
            [bold cyan]Open this URL in your browser:[/]
            [link]{deviceCode.VerificationUri}[/]

            [yellow]Enter code:[/] [bold white on blue] {deviceCode.UserCode} [/]

            [dim]Waiting for authorization...[/]
            """)
            .Header("[blue] GitHub Authentication [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue));
        _console.WriteLine();

        var expiresAt = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(deviceCode.Interval, 5));
        GitHubDeviceAuthStatus? authResult = null;

        while (DateTime.UtcNow < expiresAt)
        {
            await Task.Delay(pollInterval);
            authResult = await _authService.PollForAuthenticationAsync(deviceCode.DeviceCode);

            if (authResult.IsAuthenticated) break;
            if (authResult.Error == "slow_down")
                pollInterval = pollInterval.Add(TimeSpan.FromSeconds(5));
            else if (authResult.Error != "authorization_pending")
                break;
        }

        if (authResult?.IsAuthenticated != true)
        {
            var detail = authResult?.ErrorDescription ?? authResult?.Error ?? "authorization timed out";
            _console.MarkupLine($"[red]✗ Authentication failed: {detail.EscapeMarkup()}[/]");
            return 1;
        }

        await _authService.StoreTokenAsync(new GitHubStoredToken
        {
            AccessToken = authResult.AccessToken!,
            RefreshToken = authResult.RefreshToken,
            Scopes = authResult.Scopes,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = authResult.ExpiresAt,
            IsValid = true,
            LastValidated = DateTime.UtcNow,
        });

        _console.MarkupLine("[green]✓ GitHub authentication successful.[/]");
        return 0;
    }

    // ── App installation on repo ──────────────────────────────────────────────

    private async Task<int> EnsureAppInstalledOnRepoAsync(string owner, string repo)
    {
        _console.WriteLine();
        _console.MarkupLine($"[cyan]Checking Agentics app installation on [bold]{owner}/{repo}[/]...[/]");

        var stored = await _authService.GetStoredTokenAsync();
        if (stored is null)
        {
            _console.MarkupLine("[red]✗ No stored token — authenticate first.[/]");
            return 1;
        }

        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
        client.DefaultRequestHeaders.Add("User-Agent", _config.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // 1. Get the repo's numeric ID
        long repoId;
        try
        {
            var repoResp = await client.GetAsync($"{_config.ApiBaseUrl}/repos/{owner}/{repo}");
            if (!repoResp.IsSuccessStatusCode)
            {
                var body = await repoResp.Content.ReadAsStringAsync();
                _console.MarkupLine($"[red]✗ Could not fetch repo info ({(int)repoResp.StatusCode}): {body.EscapeMarkup()}[/]");
                return 1;
            }
            var repoJson = await repoResp.Content.ReadAsStringAsync();
            var repoData = JsonSerializer.Deserialize<GitHubRepoInfo>(repoJson, JsonOpts)!;
            repoId = repoData.Id;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]✗ Failed to fetch repo: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        // 2. List user's installations of this app (gho_ token → only shows THIS app's installations)
        List<GitHubInstallation> installations;
        try
        {
            var instResp = await client.GetAsync($"{_config.ApiBaseUrl}/user/installations?per_page=100");
            if (!instResp.IsSuccessStatusCode)
            {
                _console.MarkupLine($"[red]✗ Could not list installations ({(int)instResp.StatusCode})[/]");
                return 1;
            }
            var instJson = await instResp.Content.ReadAsStringAsync();
            var instData = JsonSerializer.Deserialize<GitHubInstallationsResponse>(instJson, JsonOpts)!;
            installations = instData.Installations;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]✗ Failed to list installations: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        // 3. Find installation that covers this owner
        var installation = installations.FirstOrDefault(i =>
            string.Equals(i.Account?.Login, owner, StringComparison.OrdinalIgnoreCase));

        // Also accept any user-level installation if owner == authenticated user
        installation ??= installations.FirstOrDefault();

        if (installation is null)
        {
            // No installation at all — guide user to install the app
            var installUrl = $"https://github.com/apps/{_config.AppSlug}/installations/new";
            _console.MarkupLine("[yellow]The Agentics Live app is not installed on your GitHub account.[/]");
            _console.WriteLine();
            _console.MarkupLine("[bold]Install it here:[/]");
            _console.MarkupLine($"  [link]{installUrl}[/]");
            _console.WriteLine();

            if (AnsiConsole.Confirm("Open browser and wait for installation?", defaultValue: true))
            {
                OpenBrowser(installUrl);
                _console.MarkupLine("[dim]Waiting for you to install the app... (press Enter when done)[/]");
                Console.ReadLine();

                // Retry
                return await EnsureAppInstalledOnRepoAsync(owner, repo);
            }

            return 1;
        }

        // 4. If the installation covers all repos, we're done
        if (string.Equals(installation.RepositorySelection, "all", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine($"[green]✓ Agentics app has access to all repositories (including {owner}/{repo}).[/]");
            _console.MarkupLine("[dim]Restart the runner to pick up the new git:push capability.[/]");
            return 0;
        }

        // 5. Check if the target repo is already in this installation
        bool repoAlreadyAdded;
        try
        {
            var repoListResp = await client.GetAsync(
                $"{_config.ApiBaseUrl}/user/installations/{installation.Id}/repositories?per_page=100");
            if (!repoListResp.IsSuccessStatusCode)
            {
                _console.MarkupLine($"[yellow]Could not list installation repos ({(int)repoListResp.StatusCode}) — attempting to add anyway.[/]");
                repoAlreadyAdded = false;
            }
            else
            {
                var repoListJson = await repoListResp.Content.ReadAsStringAsync();
                var repoList = JsonSerializer.Deserialize<GitHubRepoListResponse>(repoListJson, JsonOpts)!;
                repoAlreadyAdded = repoList.Repositories.Any(r => r.Id == repoId);
            }
        }
        catch
        {
            repoAlreadyAdded = false;
        }

        if (repoAlreadyAdded)
        {
            _console.MarkupLine($"[green]✓ Agentics app already has access to {owner}/{repo}.[/]");
            _console.MarkupLine("[dim]Restart the runner to pick up the new git:push capability.[/]");
            return 0;
        }

        // 5. Guide user to add the repo via the GitHub settings UI
        //    (the API endpoint requires an elevated app token we don't have client-side)
        var manageUrl = $"https://github.com/settings/installations/{installation.Id}";
        _console.WriteLine();
        _console.MarkupLine($"[yellow]The Agentics app is installed but doesn't have access to [bold]{owner}/{repo}[/] yet.[/]");
        _console.WriteLine();
        _console.Write(new Panel($"""
            [bold]1.[/] Open: [link]{manageUrl}[/]
            [bold]2.[/] Under [cyan]Repository access[/], add [cyan]{owner}/{repo}[/]
            [bold]3.[/] Click [cyan]Save[/]
            """)
            .Header("[blue] Add Repository Access [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue));
        _console.WriteLine();

        OpenBrowser(manageUrl);

        _console.MarkupLine("[dim]Waiting for you to grant access... press Enter when done.[/]");
        Console.ReadLine();

        // Verify access was granted
        return await EnsureAppInstalledOnRepoAsync(owner, repo);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string? owner, string? repo) ParseRepoUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];

        // https://github.com/owner/repo  OR  github.com/owner/repo
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "github.com/" })
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = url[prefix.Length..].Split('/');
                return path.Length >= 2 ? (path[0], path[1]) : (null, null);
            }
        }

        // git@github.com:owner/repo
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            var path = url["git@github.com:".Length..].Split('/');
            return path.Length >= 2 ? (path[0], path[1]) : (null, null);
        }

        return (null, null);
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                System.Diagnostics.Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    // ── JSON models ───────────────────────────────────────────────────────────

    private class GitHubInstallationsResponse
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("installations")] public List<GitHubInstallation> Installations { get; set; } = new();
    }

    private class GitHubInstallation
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("app_slug")] public string? AppSlug { get; set; }
        [JsonPropertyName("account")] public GitHubAccount? Account { get; set; }
        [JsonPropertyName("repository_selection")] public string? RepositorySelection { get; set; }
    }

    private class GitHubAccount
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private class GitHubRepoInfo
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
    }

    private class GitHubRepoListResponse
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("repositories")] public List<GitHubRepoInfo> Repositories { get; set; } = new();
    }
}
