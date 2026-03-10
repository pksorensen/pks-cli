using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Jira;

/// <summary>
/// Interactive TUI for browsing Jira tickets in a navigable tree structure.
/// Displays epics, stories, bugs, and tasks with parent-child hierarchy.
/// Supports multi-select and markdown export.
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

        [CommandOption("--output|-o")]
        [Description("Output directory for exported markdown files (default: .jira)")]
        public string OutputDir { get; set; } = ".jira";
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

        // 5. Interactive tree browser with multi-select
        var selectedIssues = SelectIssues(roots, allIssues);

        if (selectedIssues.Count == 0)
        {
            _console.MarkupLine("[yellow]No issues selected.[/]");
            return 0;
        }

        // 6. Export selected issues to markdown
        return await ExportToMarkdown(selectedIssues, settings.OutputDir);
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

    private List<JiraIssue> SelectIssues(List<JiraIssue> roots, List<JiraIssue> allIssues)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]Select issues to export[/] [dim](space=select, enter=confirm)[/]")
            .PageSize(25)
            .MoreChoicesText("[grey]Move up/down to see more issues[/]")
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to export selected)[/]");

        AddIssuesToPrompt(prompt, roots);

        var selected = _console.Prompt(prompt);

        // Map selected labels back to JiraIssue objects
        var selectedIssues = new List<JiraIssue>();
        foreach (var label in selected)
        {
            // Extract the key from the label (format: "icon KEY: summary (status)")
            var match = Regex.Match(label, @"([A-Z]+-\d+):");
            if (match.Success)
            {
                var key = match.Groups[1].Value;
                var issue = allIssues.FirstOrDefault(i => i.Key == key);
                if (issue != null)
                    selectedIssues.Add(issue);
            }
        }

        return selectedIssues;
    }

    private void AddIssuesToPrompt(MultiSelectionPrompt<string> prompt, List<JiraIssue> roots)
    {
        foreach (var root in roots)
        {
            var rootLabel = FormatIssue(root);
            if (root.Children.Count > 0)
            {
                var childLabels = new List<string>();
                CollectLabels(root.Children, childLabels);
                prompt.AddChoiceGroup(rootLabel, childLabels);
            }
            else
            {
                prompt.AddChoice(rootLabel);
            }
        }
    }

    private void CollectLabels(List<JiraIssue> issues, List<string> labels)
    {
        foreach (var issue in issues)
        {
            labels.Add(FormatIssue(issue));
            // Note: deeper nesting flattened under the parent group
            CollectLabels(issue.Children, labels);
        }
    }

    private async Task<int> ExportToMarkdown(List<JiraIssue> issues, string outputDir)
    {
        if (issues.Count == 0)
        {
            _console.MarkupLine("[yellow]No issues selected.[/]");
            return 0;
        }

        // Fetch enriched data for all selected issues
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"[cyan]Fetching details for {issues.Count} issues...[/]", async ctx =>
            {
                foreach (var issue in issues)
                {
                    ctx.Status($"[cyan]Fetching {Markup.Escape(issue.Key)}...[/]");
                    try { issue.Comments = await _jiraService.GetCommentsAsync(issue.Key); } catch { /* skip */ }
                    try { issue.Worklogs = await _jiraService.GetWorklogsAsync(issue.Key); } catch { /* skip */ }
                    try { issue.Attachments = await _jiraService.GetAttachmentsAsync(issue.Key); } catch { /* skip */ }
                }
            });

        var created = 0;
        var updated = 0;
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

        foreach (var issue in issues)
        {
            var projectDir = Path.Combine(outputDir, issue.ProjectKey);
            Directory.CreateDirectory(projectDir);

            // Write markdown
            var mdPath = Path.Combine(projectDir, $"{issue.Key}.md");
            var isUpdate = File.Exists(mdPath);
            File.WriteAllText(mdPath, GenerateMarkdown(issue));
            if (isUpdate) updated++; else created++;

            // Write JSON dump
            var jsonPath = Path.Combine(projectDir, $"{issue.Key}.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(issue, jsonOptions));

            // Download attachments
            if (issue.Attachments.Count > 0)
            {
                var attachDir = Path.Combine(projectDir, "attachments", issue.Key);
                Directory.CreateDirectory(attachDir);
                foreach (var att in issue.Attachments)
                {
                    try
                    {
                        var attPath = Path.Combine(attachDir, att.Filename);
                        if (!File.Exists(attPath)) // Don't re-download
                        {
                            var data = await _jiraService.DownloadAttachmentAsync(att.ContentUrl);
                            await File.WriteAllBytesAsync(attPath, data);
                        }
                    }
                    catch { /* Skip failed downloads */ }
                }
            }
        }

        // Show summary
        _console.MarkupLine($"[green]Exported {issues.Count} issues to {Markup.Escape(outputDir)}/[/]");
        if (created > 0) _console.MarkupLine($"  [green]Created:[/] {created} files");
        if (updated > 0) _console.MarkupLine($"  [yellow]Updated:[/] {updated} files");

        // Show tree of exported files
        var tree = new Tree($"[dim]{Markup.Escape(outputDir)}[/]");
        foreach (var group in issues.GroupBy(i => i.ProjectKey))
        {
            var projectNode = tree.AddNode($"[cyan]{Markup.Escape(group.Key)}[/]");
            foreach (var issue in group)
            {
                projectNode.AddNode($"[dim]{Markup.Escape(issue.Key)}.md[/]");
                projectNode.AddNode($"[dim]{Markup.Escape(issue.Key)}.json[/]");
                if (issue.Attachments.Count > 0)
                {
                    var attNode = projectNode.AddNode($"[dim]attachments/{Markup.Escape(issue.Key)}/[/]");
                    foreach (var att in issue.Attachments)
                    {
                        attNode.AddNode($"[dim]{Markup.Escape(att.Filename)}[/]");
                    }
                }
            }
        }
        _console.Write(tree);

        return 0;
    }

    /// <summary>
    /// Generates markdown content with YAML frontmatter for a Jira issue.
    /// Includes time log, description, conversation (comments), and attachments.
    /// </summary>
    public static string GenerateMarkdown(JiraIssue issue)
    {
        var sb = new StringBuilder();

        // === FRONTMATTER ===
        sb.AppendLine("---");
        sb.AppendLine($"key: {issue.Key}");
        sb.AppendLine($"summary: \"{EscapeYaml(issue.Summary)}\"");
        sb.AppendLine($"type: {issue.IssueType}");
        sb.AppendLine($"status: \"{EscapeYaml(issue.Status)}\"");
        sb.AppendLine($"priority: {issue.Priority}");

        if (!string.IsNullOrEmpty(issue.Assignee))
            sb.AppendLine($"assignee: \"{EscapeYaml(issue.Assignee)}\"");
        if (!string.IsNullOrEmpty(issue.Reporter))
            sb.AppendLine($"reporter: \"{EscapeYaml(issue.Reporter)}\"");
        if (!string.IsNullOrEmpty(issue.Resolution))
            sb.AppendLine($"resolution: \"{EscapeYaml(issue.Resolution)}\"");

        if (!string.IsNullOrEmpty(issue.ParentKey))
            sb.AppendLine($"parent: {issue.ParentKey}");

        sb.AppendLine($"project: {issue.ProjectKey}");

        if (issue.Labels.Count > 0)
            sb.AppendLine($"labels: [{string.Join(", ", issue.Labels.Select(l => $"\"{EscapeYaml(l)}\""))}]");
        if (issue.Components.Count > 0)
            sb.AppendLine($"components: [{string.Join(", ", issue.Components.Select(c => $"\"{EscapeYaml(c)}\""))}]");

        if (!string.IsNullOrEmpty(issue.OriginalEstimate))
            sb.AppendLine($"estimate: \"{issue.OriginalEstimate}\"");
        if (issue.OriginalEstimateSeconds.HasValue)
            sb.AppendLine($"estimate_seconds: {issue.OriginalEstimateSeconds}");

        if (!string.IsNullOrEmpty(issue.TimeSpent))
            sb.AppendLine($"time_spent: \"{issue.TimeSpent}\"");
        if (issue.TimeSpentSeconds.HasValue)
            sb.AppendLine($"time_spent_seconds: {issue.TimeSpentSeconds}");

        if (issue.StoryPoints.HasValue)
            sb.AppendLine($"story_points: {issue.StoryPoints}");

        if (issue.Created.HasValue)
            sb.AppendLine($"created: {issue.Created.Value:yyyy-MM-dd}");
        if (issue.Updated.HasValue)
            sb.AppendLine($"updated: {issue.Updated.Value:yyyy-MM-dd}");

        sb.AppendLine("---");
        sb.AppendLine();

        // === TITLE ===
        sb.AppendLine($"# {issue.Key}: {issue.Summary}");
        sb.AppendLine();

        // === TIME LOG ===
        if (issue.Worklogs.Count > 0)
        {
            sb.AppendLine("## Time Log");
            sb.AppendLine();
            sb.AppendLine("| Date | Author | Time | Comment |");
            sb.AppendLine("|------|--------|------|---------|");
            var totalSeconds = 0;
            foreach (var wl in issue.Worklogs.OrderBy(w => w.Started))
            {
                totalSeconds += wl.TimeSpentSeconds;
                var comment = (wl.Comment ?? "").Replace("|", "\\|").Replace("\n", " ").Trim();
                if (comment.Length > 80) comment = comment[..77] + "...";
                sb.AppendLine($"| {wl.Started:yyyy-MM-dd} | {wl.Author} | {wl.TimeSpent} | {comment} |");
            }
            // Total row
            sb.AppendLine($"| **Total** | | **{FormatSeconds(totalSeconds)}** | |");
            sb.AppendLine();
        }

        // === DESCRIPTION ===
        sb.AppendLine("## Description");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(issue.Description))
        {
            sb.AppendLine(issue.Description);
        }
        else
        {
            sb.AppendLine("*No description provided.*");
        }
        sb.AppendLine();

        // === CONVERSATION ===
        if (issue.Comments.Count > 0)
        {
            sb.AppendLine("## Conversation");
            sb.AppendLine();
            foreach (var comment in issue.Comments.OrderBy(c => c.Created))
            {
                sb.AppendLine($"### {comment.Author} \u2014 {comment.Created:yyyy-MM-dd HH:mm}");
                sb.AppendLine();
                sb.AppendLine(comment.Body);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        // === ATTACHMENTS ===
        if (issue.Attachments.Count > 0)
        {
            sb.AppendLine("## Attachments");
            sb.AppendLine();
            sb.AppendLine("| File | Author | Size | Date |");
            sb.AppendLine("|------|--------|------|------|");
            foreach (var att in issue.Attachments.OrderBy(a => a.Created))
            {
                var size = FormatFileSize(att.Size);
                var relativePath = $"attachments/{issue.Key}/{att.Filename}";
                sb.AppendLine($"| [{att.Filename}]({relativePath}) | {att.Author} | {size} | {att.Created:yyyy-MM-dd} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>
    /// Formats seconds into a human-readable duration string (e.g. "1d 2h 30m").
    /// Uses 8-hour work days.
    /// </summary>
    internal static string FormatSeconds(int seconds)
    {
        var days = seconds / 28800;
        var hours = (seconds % 28800) / 3600;
        var minutes = (seconds % 3600) / 60;
        var parts = new List<string>();
        if (days > 0) parts.Add($"{days}d");
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0) parts.Add($"{minutes}m");
        return parts.Count > 0 ? string.Join(" ", parts) : "0m";
    }

    /// <summary>
    /// Formats a file size in bytes to a human-readable string.
    /// </summary>
    internal static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
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
}
