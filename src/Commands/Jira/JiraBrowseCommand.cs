using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Jira;

/// <summary>
/// Interactive TUI for browsing Jira tickets in a navigable tree structure.
/// Displays epics, stories, bugs, and tasks with parent-child hierarchy.
/// </summary>
[Description("Browse Jira issues in a tree view")]
public class JiraBrowseCommand : Command<JiraBrowseCommand.Settings>
{
    private readonly IJiraService _jiraService;
    private readonly IAnsiConsole _console;

    public JiraBrowseCommand(IJiraService jiraService, IAnsiConsole console)
    {
        _jiraService = jiraService;
        _console = console;
    }

    public class Settings : JiraSettings
    {
        [CommandOption("--project|-p")]
        [Description("Project key to browse directly")]
        public string? ProjectKey { get; set; }

        [CommandOption("--jql")]
        [Description("Custom JQL query")]
        public string? Jql { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // Enable debug output if requested
        if (settings.Debug && _jiraService is JiraService svc)
        {
            svc.DebugWriter = msg => _console.MarkupLine(msg);
        }

        // 1. Check authentication
        if (!await _jiraService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated. Run 'pks jira init' first.[/]");
            return 1;
        }

        // 2. Project selection
        string projectKey;
        if (!string.IsNullOrEmpty(settings.ProjectKey))
        {
            projectKey = settings.ProjectKey;
        }
        else
        {
            var projects = await _jiraService.GetProjectsAsync();
            if (projects.Count == 0)
            {
                _console.MarkupLine("[yellow]No projects found.[/]");
                return 0;
            }

            var selected = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a project to browse:[/]")
                    .AddChoices(projects.Select(p => $"{p.Key} - {p.Name}")));

            projectKey = selected.Split(" - ")[0];
        }

        // 3. Fetch issues
        List<JiraIssue> allIssues;
        if (!string.IsNullOrEmpty(settings.Jql))
        {
            var result = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Loading issues...[/]", async _ =>
                    await _jiraService.SearchIssuesAsync(settings.Jql));
            allIssues = result.Issues;
        }
        else
        {
            allIssues = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Loading issues...[/]", async _ =>
                    await _jiraService.GetProjectIssuesAsync(projectKey));
        }

        if (allIssues.Count == 0)
        {
            _console.MarkupLine("[yellow]No issues found.[/]");
            return 0;
        }

        // 4. Build tree
        var roots = BuildIssueTree(allIssues);

        // 5. Interactive tree browser
        return BrowseTree(roots);
    }

    /// <summary>
    /// Builds a parent-child tree from a flat list of JiraIssues.
    /// Issues whose ParentKey is null or not found in the list become root nodes.
    /// </summary>
    public static List<JiraIssue> BuildIssueTree(List<JiraIssue> flatIssues)
    {
        var lookup = flatIssues.ToDictionary(i => i.Key);
        var roots = new List<JiraIssue>();

        foreach (var issue in flatIssues)
        {
            if (!string.IsNullOrEmpty(issue.ParentKey) && lookup.TryGetValue(issue.ParentKey, out var parent))
            {
                parent.Children.Add(issue);
            }
            else
            {
                roots.Add(issue);
            }
        }

        return roots;
    }

    private int BrowseTree(List<JiraIssue> roots)
    {
        var navigationStack = new Stack<List<JiraIssue>>();
        var currentItems = roots;

        while (true)
        {
            var choices = new List<string>();

            foreach (var issue in currentItems)
            {
                choices.Add(FormatIssue(issue));
            }

            if (navigationStack.Count > 0)
            {
                choices.Add("\u2190 Back");
            }

            choices.Add("Exit");

            var selection = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Browse issues:[/]")
                    .PageSize(20)
                    .AddChoices(choices));

            if (selection == "Exit")
            {
                return 0;
            }

            if (selection == "\u2190 Back")
            {
                currentItems = navigationStack.Pop();
                continue;
            }

            // Find selected issue by matching the formatted string
            var selectedIndex = choices.IndexOf(selection);
            if (selectedIndex >= 0 && selectedIndex < currentItems.Count)
            {
                var selectedIssue = currentItems[selectedIndex];

                if (selectedIssue.Children.Count > 0)
                {
                    // Navigate into children
                    navigationStack.Push(currentItems);
                    currentItems = selectedIssue.Children;
                }
                else
                {
                    // Show detail panel for leaf issue
                    ShowIssueDetail(selectedIssue);
                }
            }
        }
    }

    private static string FormatIssue(JiraIssue issue)
    {
        var icon = issue.IssueType.ToLowerInvariant() switch
        {
            "epic" => "\U0001f4cb",
            "story" => "\U0001f4d6",
            "bug" => "\U0001f41b",
            "task" => "\u2705",
            "subtask" or "sub-task" => "  \u21b3",
            _ => "\u2022"
        };

        return $"{icon} {Markup.Escape(issue.Key)}: {Markup.Escape(issue.Summary)} [{Markup.Escape(issue.Status)}]";
    }

    private void ShowIssueDetail(JiraIssue issue)
    {
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("[bold]Type:[/]", Markup.Escape(issue.IssueType));
        table.AddRow("[bold]Status:[/]", Markup.Escape(issue.Status));
        table.AddRow("[bold]Priority:[/]", Markup.Escape(issue.Priority));
        table.AddRow("[bold]Assignee:[/]", Markup.Escape(issue.Assignee ?? "Unassigned"));

        var panel = new Panel(table)
            .Header($"[bold]{Markup.Escape(issue.Key)}: {Markup.Escape(issue.Summary)}[/]")
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);

        _console.Write(panel);
        _console.WriteLine();
    }
}
