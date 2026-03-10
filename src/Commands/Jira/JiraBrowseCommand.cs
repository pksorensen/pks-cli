using System.ComponentModel;
using System.Text.RegularExpressions;
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
        [CommandArgument(0, "[url]")]
        [Description("Jira URL with JQL query")]
        public string? Url { get; set; }

        [CommandOption("--project|-p")]
        [Description("Project key to browse directly")]
        public string? ProjectKey { get; set; }

        [CommandOption("--jql")]
        [Description("Custom JQL query")]
        public string? Jql { get; set; }

        [CommandOption("--save")]
        [Description("Save the JQL filter for future use")]
        public bool Save { get; set; }

        [CommandOption("--name")]
        [Description("Name for the saved filter")]
        public string? Name { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Extracts JQL from a Jira URL. Supports:
    ///   https://site.atlassian.net/issues/?jql=...
    ///   https://site.atlassian.net/browse/PROJ-123?jql=...
    /// Returns null if no JQL found in the URL.
    /// </summary>
    public static string? ExtractJqlFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var queryString = uri.Query.TrimStart('?');
            var pairs = queryString.Split('&');
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0] == "jql")
                    return Uri.UnescapeDataString(parts[1]);
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Creates a human-readable label from a JQL query.
    /// Examples:
    ///   "cf[10067] = \"D365\"" -> "Custom field 10067 = D365"
    ///   "assignee = currentUser() ORDER BY created DESC" -> "My issues (by created)"
    /// </summary>
    public static string JqlToLabel(string jql)
    {
        var label = jql;

        // Remove ORDER BY clause for the label
        var orderIdx = label.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        var orderSuffix = "";
        if (orderIdx > 0)
        {
            var orderPart = label[orderIdx..].Trim();
            // Extract the field name after ORDER BY
            var orderField = orderPart.Replace("ORDER BY", "", StringComparison.OrdinalIgnoreCase).Trim();
            orderField = orderField.Split(' ')[0]; // Just the field name
            orderSuffix = $" (by {orderField})";
            label = label[..orderIdx].Trim();
        }

        // Replace cf[NNN] with "Custom field NNN"
        label = Regex.Replace(label, @"cf\[(\d+)\]", "Custom field $1");

        // Clean up quotes
        label = label.Replace("\"", "");

        // Replace currentUser() references
        if (label.Contains("currentUser()", StringComparison.OrdinalIgnoreCase))
        {
            label = "My issues";
        }

        // Truncate if too long
        if (label.Length > 60)
            label = label[..57] + "...";

        return (label + orderSuffix).Trim();
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

        // Resolve JQL from URL argument if provided
        string? jql = settings.Jql;
        string? sourceUrl = null;

        if (!string.IsNullOrEmpty(settings.Url))
        {
            var extracted = ExtractJqlFromUrl(settings.Url);
            if (extracted != null)
            {
                jql = extracted;
                sourceUrl = settings.Url;
                _console.MarkupLine($"[dim]Extracted JQL:[/] {Markup.Escape(jql)}");
            }
            else
            {
                _console.MarkupLine($"[yellow]Could not extract JQL from URL. Using as-is.[/]");
                jql = settings.Url; // Treat as raw JQL
            }
        }

        // If no JQL and no project specified, show saved filters + project list
        if (string.IsNullOrEmpty(jql) && string.IsNullOrEmpty(settings.ProjectKey))
        {
            var savedFilters = await _jiraService.GetSavedFiltersAsync();
            if (savedFilters.Count > 0)
            {
                var choices = new List<string>();
                choices.AddRange(savedFilters.Select(f => $"\U0001f516 {f.Label}"));
                choices.Add("\U0001f50d New search (pick project)");

                var choice = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select a saved filter or start new search:[/]")
                        .PageSize(15)
                        .AddChoices(choices));

                if (choice.StartsWith("\U0001f516"))
                {
                    var filterLabel = choice[3..]; // Skip emoji + space
                    var filter = savedFilters.First(f => f.Label == filterLabel);
                    jql = filter.Jql;
                    _console.MarkupLine($"[dim]Using filter:[/] {Markup.Escape(jql)}");
                }
                // else: fall through to project selection below
            }
        }

        // If --save, save the filter
        if (settings.Save && !string.IsNullOrEmpty(jql))
        {
            var label = settings.Name ?? JqlToLabel(jql);
            await _jiraService.SaveFilterAsync(new JiraSavedFilter
            {
                Label = label,
                Jql = jql,
                SourceUrl = sourceUrl,
                SavedAt = DateTime.UtcNow
            });
            _console.MarkupLine($"[green]Filter saved as:[/] {Markup.Escape(label)}");
        }

        // 2. Project selection (only if no JQL resolved)
        string? projectKey = settings.ProjectKey;
        if (string.IsNullOrEmpty(jql) && string.IsNullOrEmpty(projectKey))
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
        if (!string.IsNullOrEmpty(jql))
        {
            var result = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Loading issues...[/]", async _ =>
                    await _jiraService.SearchIssuesAsync(jql));
            allIssues = result.Issues;
        }
        else
        {
            allIssues = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Loading issues...[/]", async _ =>
                    await _jiraService.GetProjectIssuesAsync(projectKey!));
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

        return $"{icon} {Markup.Escape(issue.Key ?? string.Empty)}: {Markup.Escape(issue.Summary ?? string.Empty)} ({Markup.Escape(issue.Status ?? string.Empty)})";
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
