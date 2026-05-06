using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// Interactive Azure DevOps authentication via OAuth2 authorization code + PKCE.
/// Opens browser for user consent, exchanges code for tokens, and stores
/// credentials for use with git credential helper.
///
/// With an optional git URL argument, registers a repo in the proxy allowlist:
///   pks ado init https://Delegate@dev.azure.com/Delegate/MyProject/_git/my-repo
/// </summary>
[Description("Authenticate with Azure DevOps (or register a repo for git proxy)")]
public class AdoInitCommand : Command<AdoInitCommand.Settings>
{
    private const string GitReposKey = "ado.git.repos";

    private readonly IAzureDevOpsAuthService _authService;
    private readonly IConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public AdoInitCommand(
        IAzureDevOpsAuthService authService,
        IConfigurationService configService,
        IAnsiConsole console)
    {
        _authService = authService;
        _configService = configService;
        _console = console;
    }

    public class Settings : AdoSettings
    {
        [CommandArgument(0, "[git-url]")]
        [Description("ADO git URL to register in the proxy allowlist (e.g. https://Delegate@dev.azure.com/Org/Project/_git/Repo)")]
        public string? GitUrl { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common' — prompts for email/tenant if omitted)")]
        public string? TenantId { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // If a git URL was provided, register it in the allowlist (auth first if needed)
        if (!string.IsNullOrWhiteSpace(settings.GitUrl))
        {
            if (!await _authService.IsAuthenticatedAsync())
            {
                _console.MarkupLine("[dim]Not yet authenticated — running auth flow first...[/]");
                _console.WriteLine();
                var authResult = await RunAuthFlowAsync(settings);
                if (authResult != 0) return authResult;
            }

            return await RegisterGitRepoAsync(settings.GitUrl);
        }

        return await RunAuthFlowAsync(settings);
    }

    private async Task<int> RunAuthFlowAsync(Settings settings)
    {
        if (!settings.Force && await _authService.IsAuthenticatedAsync())
        {
            var credentials = await _authService.GetStoredCredentialsAsync();
            _console.MarkupLine($"[green]Already authenticated as [bold]{Markup.Escape(credentials!.Profile.DisplayName)}[/] ({Markup.Escape(credentials.SelectedOrg)})[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        if (settings.Force)
            await _authService.ClearStoredCredentialsAsync();

        var (tenantId, loginHint) = ResolveTenant(settings.TenantId);

        _console.MarkupLine("[cyan]Starting Azure DevOps authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        AdoAuthResult result;
        try
        {
            result = await _authService.InitiateAsync(tenantId, loginHint);
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[red]Authentication timed out.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Select organization
        AdoAccount selectedOrg;
        if (result.Accounts.Count == 0)
        {
            _console.MarkupLine("[red]No Azure DevOps organizations found for this account.[/]");
            return 1;
        }
        else if (result.Accounts.Count == 1)
        {
            selectedOrg = result.Accounts[0];
            _console.MarkupLine($"[dim]Using organization: [bold]{Markup.Escape(selectedOrg.AccountName)}[/][/]");
        }
        else
        {
            var orgName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure DevOps organization:[/]")
                    .AddChoices(result.Accounts.Select(a => a.AccountName)));

            selectedOrg = result.Accounts.First(a => a.AccountName == orgName);
        }

        await _authService.CompleteAsync(result, selectedOrg, tenantId);

        // Display success
        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Authentication Successful[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("User", Markup.Escape(result.Profile.DisplayName));
        table.AddRow("Email", Markup.Escape(result.Profile.EmailAddress));
        table.AddRow("Tenant", Markup.Escape(tenantId));
        table.AddRow("Organization", Markup.Escape(selectedOrg.AccountName));
        table.AddRow("Org URL", Markup.Escape($"https://dev.azure.com/{selectedOrg.AccountName}"));

        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine("[dim]Tip: register repos for the git proxy:[/]");
        _console.MarkupLine("[dim]  pks ado init https://dev.azure.com/Org/Project/_git/Repo[/]");

        return 0;
    }

    private async Task<int> RegisterGitRepoAsync(string rawUrl)
    {
        if (!TryParseAdoGitUrl(rawUrl, out var org, out var project, out var repo, out var cleanUrl))
        {
            _console.MarkupLine($"[red]Cannot parse ADO git URL: {Markup.Escape(rawUrl)}[/]");
            _console.MarkupLine("[dim]Expected: https://dev.azure.com/Org/Project/_git/Repo[/]");
            return 1;
        }

        // Load existing list
        var existing = new List<AdoGitRepo>();
        var raw = await _configService.GetAsync(GitReposKey);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try { existing = JsonSerializer.Deserialize<List<AdoGitRepo>>(raw) ?? []; }
            catch { existing = []; }
        }

        // Deduplicate by org/project/repo (case-insensitive)
        var key = $"{org}/{project}/{repo}".ToLowerInvariant();
        if (existing.Any(r => r.AllowKey.ToLowerInvariant() == key))
        {
            _console.MarkupLine($"[yellow]Already registered:[/] {Markup.Escape(org)} / {Markup.Escape(project)} / {Markup.Escape(repo)}");
            return 0;
        }

        existing.Add(new AdoGitRepo
        {
            Url = cleanUrl,
            Org = org,
            Project = project,
            Repo = repo,
            AddedAt = DateTime.UtcNow,
        });

        await _configService.SetAsync(GitReposKey, JsonSerializer.Serialize(existing), global: true);

        _console.MarkupLine($"[green]Registered:[/] {Markup.Escape(org)} / {Markup.Escape(project)} / {Markup.Escape(repo)}");
        _console.MarkupLine("[dim]This repo will appear in the selection list when you run [bold]pks claude[/].[/]");

        return 0;
    }

    /// <summary>
    /// Parses https://[user@]dev.azure.com/Org/Project/_git/Repo into components.
    /// Returns a clean URL without the user prefix.
    /// </summary>
    private static bool TryParseAdoGitUrl(
        string rawUrl,
        out string org, out string project, out string repo, out string cleanUrl)
    {
        org = project = repo = cleanUrl = string.Empty;
        try
        {
            var uri = new Uri(rawUrl);
            // Strip user info (e.g. "Delegate@")
            cleanUrl = $"https://dev.azure.com{uri.AbsolutePath}";

            // Path: /Org/Project/_git/Repo[/...]
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            var gitIdx = Array.FindIndex(parts, p =>
                string.Equals(p, "_git", StringComparison.OrdinalIgnoreCase));
            if (gitIdx < 2 || gitIdx + 1 >= parts.Length) return false;

            org = Uri.UnescapeDataString(parts[0]);
            project = Uri.UnescapeDataString(string.Join("/", parts[1..gitIdx]));
            repo = Uri.UnescapeDataString(parts[gitIdx + 1]);

            return !string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(repo);
        }
        catch
        {
            return false;
        }
    }

    private (string tenantId, string? loginHint) ResolveTenant(string? tenantIdOverride)
    {
        if (!string.IsNullOrWhiteSpace(tenantIdOverride))
            return (tenantIdOverride.Trim(), null);

        var input = _console.Prompt(
            new TextPrompt<string>("[cyan]Enter your email or tenant ID[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(input))
            return ("common", null);

        var trimmed = input.Trim();
        if (Guid.TryParse(trimmed, out _))
        {
            _console.MarkupLine($"[dim]Tenant: [bold]{Markup.Escape(trimmed)}[/][/]");
            return (trimmed, null);
        }

        // Treat as email — Entra will route to the right tenant via login_hint + select_account.
        return ("common", trimmed);
    }
}
