using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.Commands;

/// <summary>
/// Command for GitHub repository and issue management
/// </summary>
[Description("Manage GitHub repositories and issues")]
public class GitHubCommand : Command<GitHubCommand.Settings>
{
    private readonly IGitHubService _gitHubService;
    private readonly IGitHubIssuesService _issuesService;
    private readonly IGitHubAuthenticationService _authService;

    public GitHubCommand(
        IGitHubService gitHubService,
        IGitHubIssuesService issuesService,
        IGitHubAuthenticationService authService)
    {
        _gitHubService = gitHubService;
        _issuesService = issuesService;
        _authService = authService;
    }

    public class Settings : CommandSettings
    {
        [Description("GitHub operation to perform")]
        [CommandArgument(0, "[operation]")]
        public string? Operation { get; set; }

        [Description("Repository name (owner/repo format)")]
        [CommandOption("-r|--repo")]
        public string? Repository { get; set; }

        [Description("Issue title (for issue operations)")]
        [CommandOption("-t|--title")]
        public string? Title { get; set; }

        [Description("Issue body/description")]
        [CommandOption("-b|--body")]
        public string? Body { get; set; }

        [Description("Labels to apply (comma-separated)")]
        [CommandOption("-l|--labels")]
        public string? Labels { get; set; }

        [Description("Assignees (comma-separated usernames)")]
        [CommandOption("-a|--assignees")]
        public string? Assignees { get; set; }

        [Description("Issue number (for specific issue operations)")]
        [CommandOption("-n|--number")]
        public int? IssueNumber { get; set; }

        [Description("Repository description")]
        [CommandOption("-d|--description")]
        public string? Description { get; set; }

        [Description("Make repository private")]
        [CommandOption("-p|--private")]
        public bool Private { get; set; }

        [Description("Search query")]
        [CommandOption("-q|--query")]
        public string? Query { get; set; }

        [Description("State filter (open/closed/all)")]
        [CommandOption("-s|--state")]
        public string? State { get; set; }

        [Description("Show detailed output")]
        [CommandOption("-v|--verbose")]
        public bool Verbose { get; set; }

        [Description("Number of items to display")]
        [CommandOption("--limit")]
        public int? Limit { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Check authentication
            var isAuthenticated = await _authService.IsAuthenticatedAsync();
            if (!isAuthenticated)
            {
                AnsiConsole.MarkupLine("[red]Error: Not authenticated with GitHub.[/]");
                AnsiConsole.MarkupLine("[dim]Run 'pks auth login' to authenticate.[/]");
                return 1;
            }

            var operation = settings.Operation?.ToLowerInvariant();

            return operation switch
            {
                "create-repo" or "repo" => await HandleCreateRepositoryAsync(settings),
                "create-issue" or "issue" => await HandleCreateIssueAsync(settings),
                "list-issues" or "issues" => await HandleListIssuesAsync(settings),
                "get-issue" => await HandleGetIssueAsync(settings),
                "close-issue" => await HandleCloseIssueAsync(settings),
                "reopen-issue" => await HandleReopenIssueAsync(settings),
                "search" => await HandleSearchIssuesAsync(settings),
                "repo-info" => await HandleRepositoryInfoAsync(settings),
                "stats" => await HandleRepositoryStatsAsync(settings),
                "activity" => await HandleRepositoryActivityAsync(settings),
                _ => await ShowHelpAsync()
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            if (settings.Verbose)
            {
                AnsiConsole.WriteException(ex);
            }
            return 1;
        }
    }

    private async Task<int> HandleCreateRepositoryAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Repository))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository name is required. Use --repo option.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Creating repository '{settings.Repository}'...[/]");

        var repository = await _gitHubService.CreateRepositoryAsync(
            settings.Repository,
            settings.Description,
            settings.Private);

        AnsiConsole.MarkupLine($"[green]✓ Repository created successfully![/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Name", repository.Name);
        table.AddRow("Full Name", repository.FullName);
        table.AddRow("Description", repository.Description ?? "[dim]No description[/]");
        table.AddRow("Private", repository.IsPrivate ? "[red]Yes[/]" : "[green]No[/]");
        table.AddRow("Clone URL", $"[link]{repository.CloneUrl}[/]");
        table.AddRow("Web URL", $"[link]{repository.HtmlUrl}[/]");
        table.AddRow("Created", repository.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        AnsiConsole.Write(table);

        return 0;
    }

    private async Task<int> HandleCreateIssueAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        if (string.IsNullOrEmpty(settings.Title))
        {
            AnsiConsole.MarkupLine("[red]Error: Issue title is required. Use --title option.[/]");
            return 1;
        }

        var request = new CreateIssueRequest
        {
            Title = settings.Title,
            Body = settings.Body ?? string.Empty,
            Labels = ParseCommaSeparated(settings.Labels),
            Assignees = ParseCommaSeparated(settings.Assignees)
        };

        AnsiConsole.MarkupLine($"[yellow]Creating issue in {settings.Repository}...[/]");

        var issue = await _issuesService.CreateIssueAsync(owner, repo, request);

        AnsiConsole.MarkupLine($"[green]✓ Issue #{issue.Number} created successfully![/]");
        AnsiConsole.WriteLine();

        DisplayIssue(issue, settings.Verbose);

        return 0;
    }

    private async Task<int> HandleListIssuesAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        var filter = new GitHubIssueFilter
        {
            State = settings.State ?? "open",
            PerPage = settings.Limit ?? 10
        };

        if (!string.IsNullOrEmpty(settings.Labels))
        {
            filter.Labels = ParseCommaSeparated(settings.Labels);
        }

        AnsiConsole.MarkupLine($"[yellow]Fetching issues from {settings.Repository}...[/]");

        var issues = await _issuesService.ListIssuesAsync(owner, repo, filter);

        if (!issues.Any())
        {
            AnsiConsole.MarkupLine("[dim]No issues found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]Found {issues.Count} issue(s):[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[yellow]#[/]")
            .AddColumn("[cyan]Title[/]")
            .AddColumn("[magenta]State[/]")
            .AddColumn("[green]Labels[/]")
            .AddColumn("[blue]Created[/]");

        foreach (var issue in issues)
        {
            var stateColor = issue.State == "open" ? "green" : "red";
            var labelsText = issue.Labels.Any() ? string.Join(", ", issue.Labels) : "[dim]none[/]";

            table.AddRow(
                issue.Number.ToString(),
                issue.Title.Length > 50 ? issue.Title[..47] + "..." : issue.Title,
                $"[{stateColor}]{issue.State}[/]",
                labelsText,
                issue.CreatedAt.ToString("MMM dd"));
        }

        AnsiConsole.Write(table);

        if (settings.Verbose)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Use 'pks github get-issue --repo owner/repo --number X' for details.[/]");
        }

        return 0;
    }

    private async Task<int> HandleGetIssueAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        if (!settings.IssueNumber.HasValue)
        {
            AnsiConsole.MarkupLine("[red]Error: Issue number is required. Use --number option.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Fetching issue #{settings.IssueNumber} from {settings.Repository}...[/]");

        var issue = await _issuesService.GetIssueAsync(owner, repo, settings.IssueNumber.Value);

        if (issue == null)
        {
            AnsiConsole.MarkupLine($"[red]Issue #{settings.IssueNumber} not found.[/]");
            return 1;
        }

        DisplayIssue(issue, true);

        return 0;
    }

    private async Task<int> HandleCloseIssueAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        if (!settings.IssueNumber.HasValue)
        {
            AnsiConsole.MarkupLine("[red]Error: Issue number is required. Use --number option.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Closing issue #{settings.IssueNumber}...[/]");

        var issue = await _issuesService.CloseIssueAsync(owner, repo, settings.IssueNumber.Value);

        AnsiConsole.MarkupLine($"[green]✓ Issue #{issue.Number} closed successfully![/]");

        return 0;
    }

    private async Task<int> HandleReopenIssueAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        if (!settings.IssueNumber.HasValue)
        {
            AnsiConsole.MarkupLine("[red]Error: Issue number is required. Use --number option.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Reopening issue #{settings.IssueNumber}...[/]");

        var issue = await _issuesService.ReopenIssueAsync(owner, repo, settings.IssueNumber.Value);

        AnsiConsole.MarkupLine($"[green]✓ Issue #{issue.Number} reopened successfully![/]");

        return 0;
    }

    private async Task<int> HandleSearchIssuesAsync(Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Query))
        {
            AnsiConsole.MarkupLine("[red]Error: Search query is required. Use --query option.[/]");
            return 1;
        }

        var searchQuery = new GitHubIssueSearchQuery
        {
            Keywords = settings.Query,
            Repository = settings.Repository,
            State = settings.State ?? "all",
            PerPage = settings.Limit ?? 10
        };

        if (!string.IsNullOrEmpty(settings.Labels))
        {
            searchQuery.Labels = ParseCommaSeparated(settings.Labels);
        }

        AnsiConsole.MarkupLine($"[yellow]Searching for issues: '{settings.Query}'...[/]");

        var result = await _issuesService.SearchIssuesAsync(searchQuery);

        if (result.TotalCount == 0)
        {
            AnsiConsole.MarkupLine("[dim]No issues found matching the search criteria.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]Found {result.TotalCount} issue(s) (showing {result.Issues.Count}):[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[yellow]Repository[/]")
            .AddColumn("[yellow]#[/]")
            .AddColumn("[cyan]Title[/]")
            .AddColumn("[magenta]State[/]")
            .AddColumn("[blue]Created[/]");

        foreach (var issue in result.Issues)
        {
            var stateColor = issue.State == "open" ? "green" : "red";
            var repoName = ExtractRepositoryFromUrl(issue.RepositoryUrl);

            table.AddRow(
                repoName,
                issue.Number.ToString(),
                issue.Title.Length > 40 ? issue.Title[..37] + "..." : issue.Title,
                $"[{stateColor}]{issue.State}[/]",
                issue.CreatedAt.ToString("MMM dd"));
        }

        AnsiConsole.Write(table);

        return 0;
    }

    private async Task<int> HandleRepositoryInfoAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Fetching repository information for {settings.Repository}...[/]");

        var repoInfo = await _gitHubService.GetRepositoryInfoAsync(owner, repo);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Name", repoInfo.Name);
        table.AddRow("Full Name", repoInfo.FullName);
        table.AddRow("Description", repoInfo.Description ?? "[dim]No description[/]");
        table.AddRow("Owner", repoInfo.Owner);
        table.AddRow("Private", repoInfo.IsPrivate ? "[red]Yes[/]" : "[green]No[/]");
        table.AddRow("Language", repoInfo.Language);
        table.AddRow("Stars", repoInfo.StarCount.ToString());
        table.AddRow("Forks", repoInfo.ForkCount.ToString());
        table.AddRow("Created", repoInfo.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Updated", repoInfo.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Clone URL", $"[link]{repoInfo.CloneUrl}[/]");
        table.AddRow("Web URL", $"[link]{repoInfo.HtmlUrl}[/]");

        if (repoInfo.Topics.Any())
        {
            table.AddRow("Topics", string.Join(", ", repoInfo.Topics));
        }

        AnsiConsole.Write(table);

        return 0;
    }

    private async Task<int> HandleRepositoryStatsAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[yellow]Fetching repository statistics for {settings.Repository}...[/]");

        var stats = await _issuesService.GetIssueStatisticsAsync(owner, repo);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Metric[/]")
            .AddColumn("[cyan]Count[/]");

        table.AddRow("Open Issues", stats.OpenCount.ToString());
        table.AddRow("Closed Issues", stats.ClosedCount.ToString());
        table.AddRow("Total Issues", stats.TotalCount.ToString());

        AnsiConsole.Write(table);

        if (settings.Verbose && stats.LabelDistribution.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Label Distribution:[/]");

            var labelTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Yellow)
                .AddColumn("[cyan]Label[/]")
                .AddColumn("[green]Count[/]");

            foreach (var label in stats.LabelDistribution.OrderByDescending(kv => kv.Value).Take(10))
            {
                labelTable.AddRow(label.Key, label.Value.ToString());
            }

            AnsiConsole.Write(labelTable);
        }

        return 0;
    }

    private async Task<int> HandleRepositoryActivityAsync(Settings settings)
    {
        var (owner, repo) = ParseRepository(settings.Repository);
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            AnsiConsole.MarkupLine("[red]Error: Repository must be in 'owner/repo' format.[/]");
            return 1;
        }

        var days = settings.Limit ?? 30;
        AnsiConsole.MarkupLine($"[yellow]Fetching repository activity for {settings.Repository} (last {days} days)...[/]");

        var activity = await _gitHubService.GetRepositoryActivityAsync(owner, repo, days);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[yellow]Activity[/]")
            .AddColumn("[cyan]Count[/]");

        table.AddRow("Commits", activity.CommitCount.ToString());
        table.AddRow("Issues", activity.IssueCount.ToString());
        table.AddRow("Pull Requests", activity.PullRequestCount.ToString());
        table.AddRow("Active Branches", activity.ActiveBranches.Count.ToString());
        table.AddRow("Last Activity", activity.LastActivity.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        AnsiConsole.Write(table);

        if (settings.Verbose && activity.RecentCommits.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Recent Commits:[/]");

            var commitTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[cyan]Message[/]")
                .AddColumn("[green]Author[/]")
                .AddColumn("[blue]Date[/]");

            foreach (var commit in activity.RecentCommits.Take(5))
            {
                var message = commit.Commit.Message.Length > 50
                    ? commit.Commit.Message[..47] + "..."
                    : commit.Commit.Message;

                commitTable.AddRow(
                    message,
                    commit.Commit.Author.Name,
                    commit.Commit.Author.Date.ToString("MMM dd HH:mm"));
            }

            AnsiConsole.Write(commitTable);
        }

        return 0;
    }

    private async Task<int> ShowHelpAsync()
    {
        var panel = new Panel("""
            [bold cyan]PKS CLI GitHub Commands[/]
            
            [yellow]Repository Commands:[/]
            [dim]pks github create-repo --repo name --description "desc"[/]  Create repository
            [dim]pks github repo-info --repo owner/repo[/]                  Get repository info
            [dim]pks github stats --repo owner/repo[/]                      Repository statistics
            [dim]pks github activity --repo owner/repo[/]                   Repository activity
            
            [yellow]Issue Commands:[/]
            [dim]pks github create-issue --repo owner/repo --title "Title"[/]  Create issue
            [dim]pks github list-issues --repo owner/repo[/]                   List issues
            [dim]pks github get-issue --repo owner/repo --number 123[/]        Get specific issue
            [dim]pks github close-issue --repo owner/repo --number 123[/]      Close issue
            [dim]pks github reopen-issue --repo owner/repo --number 123[/]     Reopen issue
            [dim]pks github search --query "bug fix" --repo owner/repo[/]      Search issues
            
            [yellow]Common Options:[/]
            [dim]--repo owner/repo[/]     Repository in owner/repo format
            [dim]--title "text"[/]        Issue title
            [dim]--body "text"[/]         Issue description
            [dim]--labels bug,urgent[/]   Comma-separated labels
            [dim]--assignees user1,user2[/] Comma-separated assignees
            [dim]--state open|closed|all[/] Filter by state
            [dim]--verbose[/]             Show detailed output
            [dim]--limit N[/]             Limit number of results
            """)
            .Header("[blue] GitHub Commands Help [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue);

        AnsiConsole.Write(panel);
        return 0;
    }

    private static void DisplayIssue(GitHubIssueDetailed issue, bool verbose)
    {
        var panel = new Panel($"""
            [bold cyan]#{issue.Number}: {issue.Title}[/]
            
            [yellow]State:[/] {(issue.State == "open" ? "[green]Open[/]" : "[red]Closed[/]")}
            [yellow]Created:[/] {issue.CreatedAt:yyyy-MM-dd HH:mm:ss UTC}
            {(issue.UpdatedAt.HasValue ? $"[yellow]Updated:[/] {issue.UpdatedAt.Value:yyyy-MM-dd HH:mm:ss UTC}" : "")}
            {(issue.ClosedAt.HasValue ? $"[yellow]Closed:[/] {issue.ClosedAt.Value:yyyy-MM-dd HH:mm:ss UTC}" : "")}
            
            {(issue.Labels.Any() ? $"[yellow]Labels:[/] {string.Join(", ", issue.Labels)}" : "")}
            {(issue.Assignees.Any() ? $"[yellow]Assignees:[/] {string.Join(", ", issue.Assignees.Select(a => a.Login))}" : "")}
            
            [yellow]URL:[/] [link]{issue.HtmlUrl}[/]
            """)
            .Border(BoxBorder.Rounded)
            .BorderColor(issue.State == "open" ? Color.Green : Color.Red);

        AnsiConsole.Write(panel);

        if (verbose && !string.IsNullOrEmpty(issue.Body))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Description:[/]");
            AnsiConsole.WriteLine(issue.Body);
        }
    }

    private static (string owner, string repo) ParseRepository(string? repository)
    {
        if (string.IsNullOrEmpty(repository))
            return (string.Empty, string.Empty);

        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : (string.Empty, string.Empty);
    }

    private static List<string> ParseCommaSeparated(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return new List<string>();

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static string ExtractRepositoryFromUrl(string repositoryUrl)
    {
        try
        {
            var uri = new Uri(repositoryUrl);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');
            return pathParts.Length >= 2 ? $"{pathParts[0]}/{pathParts[1]}" : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}