using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Spectre.Console;

// Parse command-line arguments
string? argUrl = null, argEmail = null, argToken = null, argFrom = null, argTo = null, argProject = null;
string? argSync = null, argOutput = null;
bool csvMode = false, syncAllMode = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--url" when i + 1 < args.Length:
            argUrl = args[++i];
            break;
        case "--email" when i + 1 < args.Length:
            argEmail = args[++i];
            break;
        case "--token" when i + 1 < args.Length:
            argToken = args[++i];
            break;
        case "--from" or "-f" when i + 1 < args.Length:
            argFrom = args[++i];
            break;
        case "--to" or "-t" when i + 1 < args.Length:
            argTo = args[++i];
            break;
        case "--project" or "-p" when i + 1 < args.Length:
            argProject = args[++i];
            break;
        case "--csv":
            csvMode = true;
            break;
        case "--sync" when i + 1 < args.Length:
            argSync = args[++i];
            break;
        case "--sync-all":
            syncAllMode = true;
            break;
        case "--output" when i + 1 < args.Length:
            argOutput = args[++i];
            break;
    }
}

// Credential cache file
var cacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jiratracker");
string? cachedUrl = null, cachedEmail = null, cachedToken = null;

if (File.Exists(cacheFile))
{
    try
    {
        var cacheJson = JsonDocument.Parse(File.ReadAllText(cacheFile));
        cachedUrl = cacheJson.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        cachedEmail = cacheJson.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        cachedToken = cacheJson.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }
    catch
    {
        // Ignore corrupt cache
    }
}

// Resolve config: CLI arg > env var > cached value > interactive prompt
var jiraUrl = argUrl
    ?? Environment.GetEnvironmentVariable("JIRA_URL")
    ?? cachedUrl
    ?? AnsiConsole.Ask<string>("Enter your Jira base URL (e.g. https://company.atlassian.net):");
jiraUrl = jiraUrl.TrimEnd('/');

var jiraEmail = argEmail
    ?? Environment.GetEnvironmentVariable("JIRA_EMAIL")
    ?? cachedEmail
    ?? AnsiConsole.Ask<string>("Enter your Jira email:");

var jiraToken = argToken
    ?? Environment.GetEnvironmentVariable("JIRA_API_TOKEN")
    ?? cachedToken
    ?? AnsiConsole.Prompt(new TextPrompt<string>("Enter your API token:").Secret());

// Save credentials to cache if any were prompted (i.e. not all came from args/env)
var shouldUpdateCache = jiraUrl != cachedUrl || jiraEmail != cachedEmail || jiraToken != cachedToken;
if (shouldUpdateCache)
{
    try
    {
        var cacheData = JsonSerializer.Serialize(new { url = jiraUrl, email = jiraEmail, token = jiraToken });
        File.WriteAllText(cacheFile, cacheData);
        if (!csvMode)
            AnsiConsole.MarkupLine($"[dim]Credentials cached to {Markup.Escape(cacheFile)}[/]");
    }
    catch
    {
        // Non-fatal if cache write fails
    }
}

var fromDate = argFrom != null ? DateOnly.Parse(argFrom) : new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
var toDate = argTo != null ? DateOnly.Parse(argTo) : DateOnly.FromDateTime(DateTime.Today);

if (!csvMode && argSync == null && !syncAllMode)
{
    AnsiConsole.MarkupLine("[green]Jira Time Log Tool[/]");
    AnsiConsole.MarkupLine($"URL: [cyan]{Markup.Escape(jiraUrl)}[/]");
    AnsiConsole.MarkupLine($"Email: [cyan]{Markup.Escape(jiraEmail)}[/]");
    AnsiConsole.MarkupLine($"Period: [cyan]{fromDate}[/] to [cyan]{toDate}[/]");
    if (argProject != null)
        AnsiConsole.MarkupLine($"Project: [cyan]{Markup.Escape(argProject)}[/]");
    AnsiConsole.WriteLine();
}

// Create HTTP client with Basic auth (email:apiToken)
using var client = new HttpClient();
var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{jiraEmail}:{jiraToken}"));
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

// --sync mode: fetch issue details and write local folder
if (argSync != null)
{
    return await SyncIssue(client, jiraUrl, argSync, argOutput ?? "docs/tickets");
}

// --sync-all mode: bulk sync all tickets organized by status and hierarchy
if (syncAllMode)
{
    return await SyncAll(client, jiraUrl, argOutput ?? "docs/tickets");
}

// Step 1: Get current user's accountId
string accountId;
try
{
    var myselfResponse = await client.GetAsync($"{jiraUrl}/rest/api/3/myself");
    if (!myselfResponse.IsSuccessStatusCode)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] Failed to authenticate. Status: {myselfResponse.StatusCode}");
        var errorBody = await myselfResponse.Content.ReadAsStringAsync();
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(errorBody)}[/]");
        return 1;
    }

    var myselfJson = JsonDocument.Parse(await myselfResponse.Content.ReadAsStringAsync());
    accountId = myselfJson.RootElement.GetProperty("accountId").GetString()!;

    if (!csvMode)
        AnsiConsole.MarkupLine($"Authenticated as: [cyan]{Markup.Escape(myselfJson.RootElement.GetProperty("displayName").GetString() ?? accountId)}[/]");
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Error connecting to Jira:[/] {Markup.Escape(ex.Message)}");
    return 1;
}

// Step 2: Search for issues with worklogs in the date range
var jql = $"worklogAuthor = \"{accountId}\" AND worklogDate >= \"{fromDate:yyyy-MM-dd}\" AND worklogDate <= \"{toDate:yyyy-MM-dd}\"";
if (argProject != null)
    jql += $" AND project = \"{argProject}\"";

var allIssues = new List<JsonElement>();
const int maxResults = 50;

if (!csvMode)
    AnsiConsole.MarkupLine($"[dim]JQL: {Markup.Escape(jql)}[/]");

await AnsiConsole.Status()
    .StartAsync("Searching issues with worklogs...", async ctx =>
    {
        string? nextPageToken = null;
        var page = 0;

        while (true)
        {
            page++;
            ctx.Status($"Fetching issues (page {page})...");

            // Use the new /rest/api/3/search/jql GET endpoint (the old /rest/api/3/search is removed)
            var searchUrl = $"{jiraUrl}/rest/api/3/search/jql"
                + $"?jql={Uri.EscapeDataString(jql)}"
                + $"&fields=key,summary,parent,issuetype"
                + $"&maxResults={maxResults}";

            if (nextPageToken != null)
                searchUrl += $"&nextPageToken={Uri.EscapeDataString(nextPageToken)}";

            var searchResponse = await client.GetAsync(searchUrl);

            if (!searchResponse.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error searching issues: {searchResponse.StatusCode}[/]");
                var errorBody = await searchResponse.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(errorBody)}[/]");
                break;
            }

            var responseBody = await searchResponse.Content.ReadAsStringAsync();
            var searchJson = JsonDocument.Parse(responseBody);
            var root = searchJson.RootElement;

            if (!root.TryGetProperty("issues", out var issues))
            {
                AnsiConsole.MarkupLine("[red]Unexpected search response:[/]");
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(responseBody[..Math.Min(responseBody.Length, 500)])}[/]");
                break;
            }

            var issueCount = 0;
            foreach (var issue in issues.EnumerateArray())
            {
                allIssues.Add(issue.Clone());
                issueCount++;
            }

            // No results on this page means we're done
            if (issueCount == 0)
                break;

            // Use nextPageToken if available, otherwise we got all results
            if (root.TryGetProperty("nextPageToken", out var tokenProp)
                && tokenProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(tokenProp.GetString()))
            {
                nextPageToken = tokenProp.GetString();
            }
            else
            {
                break;
            }
        }
    });

if (!csvMode)
    AnsiConsole.MarkupLine($"Found [cyan]{allIssues.Count}[/] issues with worklogs in period");

if (allIssues.Count == 0)
{
    if (!csvMode)
        AnsiConsole.MarkupLine("[dim]No worklogs found for the specified period.[/]");
    return 0;
}

// Step 3: Fetch worklogs for each issue and filter by current user + date range
var worklogEntries = new List<WorklogEntry>();

await AnsiConsole.Progress()
    .AutoClear(true)
    .StartAsync(async ctx =>
    {
        var task = ctx.AddTask("[yellow]Fetching worklogs[/]", maxValue: allIssues.Count);

        foreach (var issue in allIssues)
        {
            var issueKey = issue.GetProperty("key").GetString()!;
            var fields = issue.GetProperty("fields");
            var summary = fields.GetProperty("summary").GetString() ?? "";
            var issueType = fields.TryGetProperty("issuetype", out var it)
                ? it.GetProperty("name").GetString() ?? ""
                : "";

            string? parentKey = null;
            string? parentSummary = null;
            if (fields.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
            {
                parentKey = parent.GetProperty("key").GetString();
                parentSummary = parent.TryGetProperty("fields", out var pf)
                    ? pf.GetProperty("summary").GetString()
                    : null;
            }

            // Fetch worklogs for this issue
            var worklogStartAt = 0;
            while (true)
            {
                var worklogUrl = $"{jiraUrl}/rest/api/3/issue/{issueKey}/worklog?startAt={worklogStartAt}&maxResults=1000";
                var worklogResponse = await client.GetAsync(worklogUrl);

                if (!worklogResponse.IsSuccessStatusCode)
                {
                    if (!csvMode)
                        AnsiConsole.MarkupLine($"[yellow]Warning: Could not fetch worklogs for {issueKey}: {worklogResponse.StatusCode}[/]");
                    break;
                }

                var worklogJson = JsonDocument.Parse(await worklogResponse.Content.ReadAsStringAsync());
                var worklogs = worklogJson.RootElement.GetProperty("worklogs");

                foreach (var wl in worklogs.EnumerateArray())
                {
                    // Filter by author
                    var authorId = wl.TryGetProperty("author", out var author2)
                        ? author2.GetProperty("accountId").GetString()
                        : null;
                    if (authorId != accountId)
                        continue;

                    // Filter by date range
                    var started = wl.GetProperty("started").GetString()!;
                    var worklogDate = DateOnly.FromDateTime(DateTime.Parse(started));
                    if (worklogDate < fromDate || worklogDate > toDate)
                        continue;

                    var timeSpentSeconds = wl.GetProperty("timeSpentSeconds").GetInt32();
                    var timeSpent = wl.TryGetProperty("timeSpent", out var ts) ? ts.GetString() ?? "" : "";

                    worklogEntries.Add(new WorklogEntry
                    {
                        IssueKey = issueKey,
                        Summary = summary,
                        IssueType = issueType,
                        ParentKey = parentKey,
                        ParentSummary = parentSummary,
                        Date = worklogDate,
                        TimeSpent = timeSpent,
                        TimeSpentSeconds = timeSpentSeconds
                    });
                }

                var worklogTotal = worklogJson.RootElement.GetProperty("total").GetInt32();
                worklogStartAt += worklogs.GetArrayLength();
                if (worklogStartAt >= worklogTotal)
                    break;
            }

            task.Increment(1);
        }
    });

if (worklogEntries.Count == 0)
{
    if (!csvMode)
        AnsiConsole.MarkupLine("[dim]No matching worklogs found after filtering.[/]");
    return 0;
}

// Step 4: Sort and group
worklogEntries = worklogEntries
    .OrderBy(w => w.ParentKey ?? w.IssueKey)
    .ThenBy(w => w.IssueKey)
    .ThenBy(w => w.Date)
    .ToList();

// Step 5: Output
if (csvMode)
{
    Console.WriteLine("IssueKey,ParentKey,Summary,Date,TimeSpent,Hours");
    foreach (var entry in worklogEntries)
    {
        var hours = Math.Round(entry.TimeSpentSeconds / 3600.0, 2);
        var escapedSummary = entry.Summary.Contains(',') || entry.Summary.Contains('"')
            ? $"\"{entry.Summary.Replace("\"", "\"\"")}\""
            : entry.Summary;
        Console.WriteLine($"{entry.IssueKey},{entry.ParentKey ?? ""},{escapedSummary},{entry.Date:yyyy-MM-dd},{entry.TimeSpent},{hours}");
    }
}
else
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[bold]Jira Time Log: {fromDate} to {toDate}[/]");
    AnsiConsole.WriteLine();

    var table = new Table().Border(TableBorder.Rounded);
    table.AddColumn(new TableColumn("[bold]Issue Key[/]"));
    table.AddColumn(new TableColumn("[bold]Summary[/]"));
    table.AddColumn(new TableColumn("[bold]Date[/]"));
    table.AddColumn(new TableColumn("[bold]Time Spent[/]").RightAligned());
    table.AddColumn(new TableColumn("[bold]Hours[/]").RightAligned());

    var renderedParents = new HashSet<string>();
    var totalSeconds = 0;

    foreach (var entry in worklogEntries)
    {
        // Render parent row if this is a subtask and we haven't shown the parent yet
        if (entry.ParentKey != null && renderedParents.Add(entry.ParentKey))
        {
            table.AddRow(
                $"[bold]{Markup.Escape(entry.ParentKey)}[/]",
                $"[bold]{Markup.Escape(entry.ParentSummary ?? "")}[/]",
                "",
                "",
                ""
            );
        }

        var hours = Math.Round(entry.TimeSpentSeconds / 3600.0, 2);
        totalSeconds += entry.TimeSpentSeconds;

        var indent = entry.ParentKey != null ? "  " : "";
        table.AddRow(
            $"{indent}[cyan]{Markup.Escape(entry.IssueKey)}[/]",
            $"{indent}{Markup.Escape(entry.Summary)}",
            $"{entry.Date:yyyy-MM-dd}",
            entry.TimeSpent,
            $"{hours:F2}"
        );
    }

    // Total row
    var totalHours = Math.Round(totalSeconds / 3600.0, 2);
    var totalTimeSpent = FormatTimeSpent(totalSeconds);
    table.AddEmptyRow();
    table.AddRow("", "[bold]TOTAL[/]", "", $"[bold]{totalTimeSpent}[/]", $"[bold]{totalHours:F2}[/]");

    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[dim]{worklogEntries.Count} worklog entries across {worklogEntries.Select(w => w.IssueKey).Distinct().Count()} issues[/]");
}

return 0;

static string FormatTimeSpent(int totalSeconds)
{
    var hours = totalSeconds / 3600;
    var minutes = (totalSeconds % 3600) / 60;
    return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
}

static async Task<int> SyncIssue(HttpClient client, string jiraUrl, string issueKey, string outputRoot)
{
    AnsiConsole.MarkupLine($"[green]Syncing issue:[/] [cyan]{Markup.Escape(issueKey)}[/]");

    var fields = "summary,description,status,issuetype,priority,assignee,reporter,created,updated,comment,attachment,subtasks";
    var url = $"{jiraUrl}/rest/api/3/issue/{Uri.EscapeDataString(issueKey)}?fields={fields}";

    var response = await client.GetAsync(url);
    if (!response.IsSuccessStatusCode)
    {
        AnsiConsole.MarkupLine($"[red]Error fetching issue {issueKey}: {response.StatusCode}[/]");
        var errorBody = await response.Content.ReadAsStringAsync();
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(errorBody)}[/]");
        return 1;
    }

    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = json.RootElement;
    var f = root.GetProperty("fields");

    var summary = GetString(f, "summary") ?? "(no summary)";
    var status = GetNested(f, "status", "name") ?? "Unknown";
    var issueType = GetNested(f, "issuetype", "name") ?? "Unknown";
    var priority = GetNested(f, "priority", "name") ?? "None";
    var assignee = GetNested(f, "assignee", "displayName") ?? "Unassigned";
    var reporter = GetNested(f, "reporter", "displayName") ?? "Unknown";
    var created = GetString(f, "created") ?? "";
    var updated = GetString(f, "updated") ?? "";

    // Convert ADF description to markdown
    var descriptionMd = "(no description)";
    if (f.TryGetProperty("description", out var descNode) && descNode.ValueKind == JsonValueKind.Object)
    {
        descriptionMd = AdfToMarkdown(descNode);
    }

    // Subtasks
    var subtasks = new List<(string Key, string Summary, string Status)>();
    if (f.TryGetProperty("subtasks", out var subtasksNode) && subtasksNode.ValueKind == JsonValueKind.Array)
    {
        foreach (var st in subtasksNode.EnumerateArray())
        {
            var stKey = GetString(st, "key") ?? "";
            var stSummary = GetNested(st, "fields", "summary") ?? "";
            var stStatus = "";
            if (st.TryGetProperty("fields", out var stFields)
                && stFields.TryGetProperty("status", out var stStatusNode))
            {
                stStatus = GetString(stStatusNode, "name") ?? "";
            }
            subtasks.Add((stKey, stSummary, stStatus));
        }
    }

    // Comments
    var comments = new List<(string Author, string Date, string BodyMd)>();
    if (f.TryGetProperty("comment", out var commentNode)
        && commentNode.TryGetProperty("comments", out var commentsArray)
        && commentsArray.ValueKind == JsonValueKind.Array)
    {
        foreach (var c in commentsArray.EnumerateArray())
        {
            var author = GetNested(c, "author", "displayName") ?? "Unknown";
            var date = GetString(c, "created") ?? "";
            var bodyMd = "";
            if (c.TryGetProperty("body", out var bodyNode) && bodyNode.ValueKind == JsonValueKind.Object)
            {
                bodyMd = AdfToMarkdown(bodyNode);
            }
            comments.Add((author, date, bodyMd));
        }
    }

    // Attachments
    var attachments = new List<(string Filename, string Url, long Size)>();
    if (f.TryGetProperty("attachment", out var attachNode) && attachNode.ValueKind == JsonValueKind.Array)
    {
        foreach (var a in attachNode.EnumerateArray())
        {
            var filename = GetString(a, "filename") ?? "unknown";
            var contentUrl = GetString(a, "content") ?? "";
            var size = a.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
            if (!string.IsNullOrEmpty(contentUrl))
                attachments.Add((filename, contentUrl, size));
        }
    }

    // Create output directory
    var issueDir = Path.Combine(outputRoot, issueKey);
    Directory.CreateDirectory(issueDir);

    // Download attachments
    if (attachments.Count > 0)
    {
        var attachDir = Path.Combine(issueDir, "attachments");
        Directory.CreateDirectory(attachDir);

        await AnsiConsole.Progress()
            .AutoClear(true)
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Downloading attachments[/]", maxValue: attachments.Count);
                foreach (var (filename, contentUrl, _) in attachments)
                {
                    try
                    {
                        var fileBytes = await client.GetByteArrayAsync(contentUrl);
                        await File.WriteAllBytesAsync(Path.Combine(attachDir, filename), fileBytes);
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Warning: Failed to download {Markup.Escape(filename)}: {Markup.Escape(ex.Message)}[/]");
                    }
                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine($"Downloaded [cyan]{attachments.Count}[/] attachment(s)");
    }

    // Build README.md
    var sb = new StringBuilder();
    sb.AppendLine($"# {issueKey}: {summary}");
    sb.AppendLine();
    sb.AppendLine("| Field | Value |");
    sb.AppendLine("|-------|-------|");
    sb.AppendLine($"| Status | {status} |");
    sb.AppendLine($"| Type | {issueType} |");
    sb.AppendLine($"| Priority | {priority} |");
    sb.AppendLine($"| Assignee | {assignee} |");
    sb.AppendLine($"| Reporter | {reporter} |");
    sb.AppendLine($"| Created | {created} |");
    sb.AppendLine($"| Updated | {updated} |");
    sb.AppendLine();
    sb.AppendLine("## Description");
    sb.AppendLine();
    sb.AppendLine(descriptionMd);
    sb.AppendLine();

    if (subtasks.Count > 0)
    {
        sb.AppendLine("## Subtasks");
        sb.AppendLine();
        foreach (var (stKey, stSummary, stStatus) in subtasks)
        {
            sb.AppendLine($"- [ ] {stKey}: {stSummary} ({stStatus})");
        }
        sb.AppendLine();
    }

    if (comments.Count > 0)
    {
        sb.AppendLine("## Comments");
        sb.AppendLine();
        foreach (var (author, date, bodyMd) in comments)
        {
            sb.AppendLine($"### {author} — {date}");
            sb.AppendLine();
            sb.AppendLine(bodyMd);
            sb.AppendLine();
        }
    }

    if (attachments.Count > 0)
    {
        sb.AppendLine("## Attachments");
        sb.AppendLine();
        foreach (var (filename, _, size) in attachments)
        {
            var sizeStr = size >= 1_048_576 ? $"{size / 1_048_576.0:F1} MB"
                : size >= 1024 ? $"{size / 1024.0:F1} KB"
                : $"{size} bytes";
            sb.AppendLine($"- [{filename}](attachments/{filename}) ({sizeStr})");
        }
        sb.AppendLine();
    }

    var readmePath = Path.Combine(issueDir, "README.md");
    await File.WriteAllTextAsync(readmePath, sb.ToString());

    AnsiConsole.MarkupLine($"[green]Synced to:[/] [cyan]{Markup.Escape(issueDir)}[/]");
    AnsiConsole.MarkupLine($"  README.md with {comments.Count} comment(s), {subtasks.Count} subtask(s)");
    if (attachments.Count > 0)
        AnsiConsole.MarkupLine($"  {attachments.Count} attachment(s) in attachments/");

    return 0;
}

static string? GetString(JsonElement el, string prop)
{
    return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

static string? GetNested(JsonElement el, string prop1, string prop2)
{
    if (el.TryGetProperty(prop1, out var v1) && v1.ValueKind == JsonValueKind.Object)
        return GetString(v1, prop2);
    return null;
}

static string AdfToMarkdown(JsonElement node)
{
    if (node.ValueKind != JsonValueKind.Object)
        return "";

    var type = GetString(node, "type") ?? "";

    return type switch
    {
        "doc" => JoinChildren(node, "\n\n"),
        "paragraph" => JoinChildren(node, ""),
        "heading" => HeadingToMd(node),
        "bulletList" => ListToMd(node, ordered: false),
        "orderedList" => ListToMd(node, ordered: true),
        "listItem" => JoinChildren(node, "\n"),
        "blockquote" => BlockquoteToMd(node),
        "codeBlock" => CodeBlockToMd(node),
        "table" => TableToMd(node),
        "tableRow" => "", // handled by TableToMd
        "tableHeader" => "", // handled by TableToMd
        "tableCell" => "", // handled by TableToMd
        "rule" or "hardBreak" => type == "rule" ? "\n---\n" : "\n",
        "text" => TextToMd(node),
        "mention" => MentionToMd(node),
        "inlineCard" => InlineCardToMd(node),
        "emoji" => EmojiToMd(node),
        "mediaSingle" or "media" or "mediaGroup" => MediaToMd(node),
        _ => JoinChildren(node, type == "panel" ? "\n\n" : "")
    };
}

static string JoinChildren(JsonElement node, string separator)
{
    if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        return "";

    var parts = new List<string>();
    foreach (var child in content.EnumerateArray())
    {
        var md = AdfToMarkdown(child);
        if (!string.IsNullOrEmpty(md))
            parts.Add(md);
    }
    return string.Join(separator, parts);
}

static string HeadingToMd(JsonElement node)
{
    var level = node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("level", out var lv)
        ? lv.GetInt32() : 1;
    var prefix = new string('#', Math.Clamp(level, 1, 6));
    return $"{prefix} {JoinChildren(node, "")}";
}

static string ListToMd(JsonElement node, bool ordered)
{
    if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        return "";

    var lines = new List<string>();
    var index = 1;
    foreach (var item in content.EnumerateArray())
    {
        var itemText = JoinChildren(item, "\n  ");
        var prefix = ordered ? $"{index}. " : "- ";
        lines.Add($"{prefix}{itemText}");
        index++;
    }
    return string.Join("\n", lines);
}

static string BlockquoteToMd(JsonElement node)
{
    var inner = JoinChildren(node, "\n\n");
    return string.Join("\n", inner.Split('\n').Select(line => $"> {line}"));
}

static string CodeBlockToMd(JsonElement node)
{
    var lang = "";
    if (node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("language", out var langNode))
        lang = langNode.GetString() ?? "";
    var code = JoinChildren(node, "");
    return $"```{lang}\n{code}\n```";
}

static string TableToMd(JsonElement node)
{
    if (!node.TryGetProperty("content", out var rows) || rows.ValueKind != JsonValueKind.Array)
        return "";

    var tableRows = new List<List<string>>();
    var isHeaderRow = new List<bool>();

    foreach (var row in rows.EnumerateArray())
    {
        if (!row.TryGetProperty("content", out var cells) || cells.ValueKind != JsonValueKind.Array)
            continue;

        var rowCells = new List<string>();
        var hasHeader = false;
        foreach (var cell in cells.EnumerateArray())
        {
            var cellType = GetString(cell, "type") ?? "";
            if (cellType == "tableHeader") hasHeader = true;
            rowCells.Add(JoinChildren(cell, " ").Replace("|", "\\|"));
        }
        tableRows.Add(rowCells);
        isHeaderRow.Add(hasHeader);
    }

    if (tableRows.Count == 0) return "";

    var colCount = tableRows.Max(r => r.Count);
    var sb = new StringBuilder();

    for (int i = 0; i < tableRows.Count; i++)
    {
        var row = tableRows[i];
        while (row.Count < colCount) row.Add("");
        sb.AppendLine("| " + string.Join(" | ", row) + " |");

        // Add separator after header row
        if (isHeaderRow[i])
        {
            sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |");
        }
    }

    // If no header row was found, add separator after first row
    if (!isHeaderRow.Any(h => h) && tableRows.Count > 0)
    {
        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (lines.Count >= 1)
        {
            var sep = "| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |";
            lines.Insert(1, sep);
            return string.Join("\n", lines);
        }
    }

    return sb.ToString().TrimEnd();
}

static string TextToMd(JsonElement node)
{
    var text = GetString(node, "text") ?? "";
    if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array)
        return text;

    foreach (var mark in marks.EnumerateArray())
    {
        var markType = GetString(mark, "type") ?? "";
        text = markType switch
        {
            "strong" => $"**{text}**",
            "em" => $"*{text}*",
            "code" => $"`{text}`",
            "strike" => $"~~{text}~~",
            "link" => LinkMarkToMd(mark, text),
            "subsup" => text, // best-effort: leave as-is
            "underline" => $"<u>{text}</u>",
            _ => text
        };
    }
    return text;
}

static string LinkMarkToMd(JsonElement mark, string text)
{
    if (mark.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("href", out var href))
        return $"[{text}]({href.GetString()})";
    return text;
}

static string MentionToMd(JsonElement node)
{
    if (node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("text", out var text))
        return text.GetString() ?? "@unknown";
    return "@unknown";
}

static string InlineCardToMd(JsonElement node)
{
    if (node.TryGetProperty("attrs", out var attrs) && attrs.TryGetProperty("url", out var urlNode))
    {
        var cardUrl = urlNode.GetString() ?? "";
        return $"[{cardUrl}]({cardUrl})";
    }
    return "";
}

static string EmojiToMd(JsonElement node)
{
    if (node.TryGetProperty("attrs", out var attrs))
    {
        if (attrs.TryGetProperty("text", out var text))
            return text.GetString() ?? "";
        if (attrs.TryGetProperty("shortName", out var shortName))
            return shortName.GetString() ?? "";
    }
    return "";
}

static string MediaToMd(JsonElement node)
{
    // Media nodes reference attachments by ID; best-effort: show as placeholder
    if (node.TryGetProperty("attrs", out var attrs))
    {
        var alt = attrs.TryGetProperty("alt", out var altNode) ? altNode.GetString() : null;
        return alt != null ? $"[{alt}]" : "[media]";
    }
    return JoinChildren(node, "");
}

static async Task<int> SyncAll(HttpClient client, string jiraUrl, string outputRoot)
{
    AnsiConsole.MarkupLine("[green]Bulk Sync Mode[/]");

    // Step 1: Fetch available projects
    AnsiConsole.MarkupLine("[dim]Fetching projects...[/]");
    var projects = new List<(string Key, string Name)>();
    var projResponse = await client.GetAsync($"{jiraUrl}/rest/api/3/project/search?maxResults=100");
    if (!projResponse.IsSuccessStatusCode)
    {
        AnsiConsole.MarkupLine($"[red]Error fetching projects: {projResponse.StatusCode}[/]");
        return 1;
    }

    var projJson = JsonDocument.Parse(await projResponse.Content.ReadAsStringAsync());
    foreach (var p in projJson.RootElement.GetProperty("values").EnumerateArray())
    {
        var key = GetString(p, "key") ?? "";
        var name = GetString(p, "name") ?? key;
        if (!string.IsNullOrEmpty(key))
            projects.Add((key, name));
    }

    if (projects.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No projects found.[/]");
        return 1;
    }

    // Step 2: Let user select projects
    var selectedProjects = AnsiConsole.Prompt(
        new MultiSelectionPrompt<string>()
            .Title("Select [green]projects[/] to sync:")
            .PageSize(20)
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
            .AddChoices(projects.Select(p => $"{p.Key} - {p.Name}")));

    var projectKeys = selectedProjects.Select(s => s.Split(" - ")[0]).ToList();
    AnsiConsole.MarkupLine($"Selected projects: [cyan]{string.Join(", ", projectKeys)}[/]");

    // Step 3: Fetch all issues for selected projects
    var jql = $"project in ({string.Join(",", projectKeys)}) ORDER BY key ASC";
    var fields = "key,summary,description,status,issuetype,priority,assignee,reporter,created,updated,comment,attachment,subtasks,parent";
    var allIssues = new Dictionary<string, JsonElement>();

    await AnsiConsole.Status().StartAsync("Fetching issues...", async ctx =>
    {
        string? nextPageToken = null;
        var page = 0;

        while (true)
        {
            page++;
            ctx.Status($"Fetching issues (page {page}, {allIssues.Count} so far)...");

            var searchUrl = $"{jiraUrl}/rest/api/3/search/jql"
                + $"?jql={Uri.EscapeDataString(jql)}"
                + $"&fields={fields}"
                + $"&maxResults=100";

            if (nextPageToken != null)
                searchUrl += $"&nextPageToken={Uri.EscapeDataString(nextPageToken)}";

            var response = await client.GetAsync(searchUrl);
            if (!response.IsSuccessStatusCode)
            {
                AnsiConsole.MarkupLine($"[red]Error searching issues: {response.StatusCode}[/]");
                var errorBody = await response.Content.ReadAsStringAsync();
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(errorBody)}[/]");
                break;
            }

            var searchJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = searchJson.RootElement;

            if (!root.TryGetProperty("issues", out var issues))
                break;

            var count = 0;
            foreach (var issue in issues.EnumerateArray())
            {
                var key = issue.GetProperty("key").GetString()!;
                allIssues[key] = issue.Clone();
                count++;
            }

            if (count == 0)
                break;

            if (root.TryGetProperty("nextPageToken", out var tokenProp)
                && tokenProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(tokenProp.GetString()))
            {
                nextPageToken = tokenProp.GetString();
            }
            else
            {
                break;
            }
        }
    });

    AnsiConsole.MarkupLine($"Fetched [cyan]{allIssues.Count}[/] issues");

    if (allIssues.Count == 0)
        return 0;

    // Step 4: Build parent/child hierarchy
    var topLevel = new List<string>();
    var children = new Dictionary<string, List<string>>(); // parentKey -> list of child keys

    foreach (var (key, issue) in allIssues)
    {
        var f = issue.GetProperty("fields");
        string? parentKey = null;
        if (f.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
            parentKey = GetString(parent, "key");

        if (parentKey != null && allIssues.ContainsKey(parentKey))
        {
            if (!children.ContainsKey(parentKey))
                children[parentKey] = new List<string>();
            children[parentKey].Add(key);
        }
        else if (parentKey != null && !allIssues.ContainsKey(parentKey))
        {
            // Orphan child - parent not in selection, treat as top-level
            topLevel.Add(key);
        }
        else
        {
            topLevel.Add(key);
        }
    }

    // Step 5: Let user select which top-level issues to include
    var topLevelChoices = topLevel.Select(k =>
    {
        var f = allIssues[k].GetProperty("fields");
        var summary = GetString(f, "summary") ?? "";
        var childCount = children.ContainsKey(k) ? children[k].Count : 0;
        var childSuffix = childCount > 0 ? $" [{childCount} children]" : "";
        return $"{k}: {summary}{childSuffix}";
    }).ToList();

    var prompt = new MultiSelectionPrompt<string>()
        .Title("Select [green]issues[/] to sync:")
        .PageSize(20)
        .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
        .AddChoices(topLevelChoices);

    foreach (var choice in topLevelChoices)
        prompt.Select(choice);

    var selectedTopLevel = AnsiConsole.Prompt(prompt);

    var selectedKeys = new HashSet<string>(selectedTopLevel.Select(s => s.Split(':')[0]));

    // Also include children of selected parents
    var allSelectedKeys = new HashSet<string>(selectedKeys);
    foreach (var parentKey in selectedKeys)
    {
        if (children.ContainsKey(parentKey))
        {
            foreach (var childKey in children[parentKey])
                allSelectedKeys.Add(childKey);
        }
    }

    AnsiConsole.MarkupLine($"Syncing [cyan]{allSelectedKeys.Count}[/] issues ({selectedKeys.Count} top-level)");

    // Step 6: Clean output directory
    if (Directory.Exists(outputRoot))
    {
        Directory.Delete(outputRoot, recursive: true);
        AnsiConsole.MarkupLine($"[dim]Cleaned {Markup.Escape(outputRoot)}[/]");
    }
    Directory.CreateDirectory(outputRoot);

    // Step 7: Generate folder structure
    var statusFolders = new Dictionary<string, List<string>>(); // statusSlug -> list of issue keys

    await AnsiConsole.Progress().AutoClear(true).StartAsync(async ctx =>
    {
        var task = ctx.AddTask("[yellow]Writing markdown files[/]", maxValue: allSelectedKeys.Count);

        foreach (var key in allSelectedKeys)
        {
            if (!selectedKeys.Contains(key) && !allSelectedKeys.Contains(key))
            {
                task.Increment(1);
                continue;
            }

            var issue = allIssues[key];
            var f = issue.GetProperty("fields");
            var status = GetNested(f, "status", "name") ?? "Unknown";
            var statusSlug = SanitizeStatus(status);

            var isTopLevel = selectedKeys.Contains(key);

            if (isTopLevel)
            {
                // Top-level issue gets its own folder
                var issueDir = Path.Combine(outputRoot, statusSlug, key);
                Directory.CreateDirectory(issueDir);
                var md = BuildBulkSyncMarkdown(allIssues, key, children, isReadme: true);
                await File.WriteAllTextAsync(Path.Combine(issueDir, "README.md"), md);

                if (!statusFolders.ContainsKey(statusSlug))
                    statusFolders[statusSlug] = new List<string>();
                statusFolders[statusSlug].Add(key);
            }
            else
            {
                // Child issue - find its parent in selected keys
                string? parentKey = null;
                if (f.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
                    parentKey = GetString(parent, "key");

                if (parentKey != null && selectedKeys.Contains(parentKey))
                {
                    var parentStatus = GetNested(allIssues[parentKey].GetProperty("fields"), "status", "name") ?? "Unknown";
                    var parentStatusSlug = SanitizeStatus(parentStatus);
                    var parentDir = Path.Combine(outputRoot, parentStatusSlug, parentKey);
                    Directory.CreateDirectory(parentDir);
                    var md = BuildBulkSyncMarkdown(allIssues, key, children, isReadme: false, parentKey: parentKey);
                    await File.WriteAllTextAsync(Path.Combine(parentDir, $"{key}.md"), md);
                }
                else
                {
                    // Orphan child - give it its own folder
                    var issueDir = Path.Combine(outputRoot, statusSlug, key);
                    Directory.CreateDirectory(issueDir);
                    var md = BuildBulkSyncMarkdown(allIssues, key, children, isReadme: true);
                    await File.WriteAllTextAsync(Path.Combine(issueDir, "README.md"), md);

                    if (!statusFolders.ContainsKey(statusSlug))
                        statusFolders[statusSlug] = new List<string>();
                    statusFolders[statusSlug].Add(key);
                }
            }

            task.Increment(1);
        }
    });

    // Step 8: Generate index.md
    var indexSb = new StringBuilder();
    indexSb.AppendLine("# Jira Tickets Index");
    indexSb.AppendLine();
    indexSb.AppendLine($"*Synced: {DateTime.Now:yyyy-MM-dd HH:mm}*");
    indexSb.AppendLine();

    foreach (var (statusSlug, keys) in statusFolders.OrderBy(kv => kv.Key))
    {
        indexSb.AppendLine($"## {statusSlug}");
        indexSb.AppendLine();

        foreach (var key in keys.OrderBy(k => k))
        {
            var f = allIssues[key].GetProperty("fields");
            var summary = GetString(f, "summary") ?? "";
            var childCount = children.ContainsKey(key) ? children[key].Count : 0;
            var childSuffix = childCount > 0 ? $" ({childCount} subtasks)" : "";
            indexSb.AppendLine($"- [{key}: {summary}]({statusSlug}/{key}/README.md){childSuffix}");
        }

        indexSb.AppendLine();
    }

    await File.WriteAllTextAsync(Path.Combine(outputRoot, "index.md"), indexSb.ToString());

    AnsiConsole.MarkupLine($"[green]Sync complete![/] Output: [cyan]{Markup.Escape(outputRoot)}[/]");
    AnsiConsole.MarkupLine($"  {allSelectedKeys.Count} issues across {statusFolders.Count} status categories");
    AnsiConsole.MarkupLine($"  Index: [cyan]{Markup.Escape(Path.Combine(outputRoot, "index.md"))}[/]");

    return 0;
}

static string SanitizeStatus(string status)
{
    return status.ToLowerInvariant()
        .Replace(" ", "-")
        .Replace("/", "-")
        .Replace("\\", "-")
        .Replace(":", "-")
        .Replace(".", "-");
}

static string BuildBulkSyncMarkdown(
    Dictionary<string, JsonElement> allIssues,
    string issueKey,
    Dictionary<string, List<string>> children,
    bool isReadme,
    string? parentKey = null)
{
    var issue = allIssues[issueKey];
    var f = issue.GetProperty("fields");

    var summary = GetString(f, "summary") ?? "(no summary)";
    var status = GetNested(f, "status", "name") ?? "Unknown";
    var issueType = GetNested(f, "issuetype", "name") ?? "Unknown";
    var priority = GetNested(f, "priority", "name") ?? "None";
    var assignee = GetNested(f, "assignee", "displayName") ?? "Unassigned";
    var reporter = GetNested(f, "reporter", "displayName") ?? "Unknown";
    var created = GetString(f, "created") ?? "";
    var updated = GetString(f, "updated") ?? "";

    var descriptionMd = "(no description)";
    if (f.TryGetProperty("description", out var descNode) && descNode.ValueKind == JsonValueKind.Object)
        descriptionMd = AdfToMarkdown(descNode);

    var sb = new StringBuilder();

    // Back-link to parent
    if (parentKey != null)
    {
        sb.AppendLine($"*Parent: [{parentKey}](README.md)*");
        sb.AppendLine();
    }

    sb.AppendLine($"# {issueKey}: {summary}");
    sb.AppendLine();
    sb.AppendLine("| Field | Value |");
    sb.AppendLine("|-------|-------|");
    sb.AppendLine($"| Status | {status} |");
    sb.AppendLine($"| Type | {issueType} |");
    sb.AppendLine($"| Priority | {priority} |");
    sb.AppendLine($"| Assignee | {assignee} |");
    sb.AppendLine($"| Reporter | {reporter} |");
    sb.AppendLine($"| Created | {created} |");
    sb.AppendLine($"| Updated | {updated} |");
    sb.AppendLine();
    sb.AppendLine("## Description");
    sb.AppendLine();
    sb.AppendLine(descriptionMd);
    sb.AppendLine();

    // Subtasks with links to sibling .md files
    if (children.ContainsKey(issueKey) && children[issueKey].Count > 0)
    {
        sb.AppendLine("## Subtasks");
        sb.AppendLine();
        foreach (var childKey in children[issueKey])
        {
            if (allIssues.TryGetValue(childKey, out var childIssue))
            {
                var childFields = childIssue.GetProperty("fields");
                var childSummary = GetString(childFields, "summary") ?? "";
                var childStatus = GetNested(childFields, "status", "name") ?? "";
                sb.AppendLine($"- [{childKey}: {childSummary}]({childKey}.md) ({childStatus})");
            }
        }
        sb.AppendLine();
    }
    else if (f.TryGetProperty("subtasks", out var subtasksNode) && subtasksNode.ValueKind == JsonValueKind.Array)
    {
        var subtaskList = new List<(string Key, string Summary, string Status)>();
        foreach (var st in subtasksNode.EnumerateArray())
        {
            var stKey = GetString(st, "key") ?? "";
            var stSummary = GetNested(st, "fields", "summary") ?? "";
            var stStatus = "";
            if (st.TryGetProperty("fields", out var stFields)
                && stFields.TryGetProperty("status", out var stStatusNode))
                stStatus = GetString(stStatusNode, "name") ?? "";
            subtaskList.Add((stKey, stSummary, stStatus));
        }

        if (subtaskList.Count > 0)
        {
            sb.AppendLine("## Subtasks");
            sb.AppendLine();
            foreach (var (stKey, stSummary, stStatus) in subtaskList)
            {
                sb.AppendLine($"- {stKey}: {stSummary} ({stStatus})");
            }
            sb.AppendLine();
        }
    }

    // Comments
    if (f.TryGetProperty("comment", out var commentNode)
        && commentNode.TryGetProperty("comments", out var commentsArray)
        && commentsArray.ValueKind == JsonValueKind.Array)
    {
        var commentList = new List<(string Author, string Date, string BodyMd)>();
        foreach (var c in commentsArray.EnumerateArray())
        {
            var author = GetNested(c, "author", "displayName") ?? "Unknown";
            var date = GetString(c, "created") ?? "";
            var bodyMd = "";
            if (c.TryGetProperty("body", out var bodyNode) && bodyNode.ValueKind == JsonValueKind.Object)
                bodyMd = AdfToMarkdown(bodyNode);
            commentList.Add((author, date, bodyMd));
        }

        if (commentList.Count > 0)
        {
            sb.AppendLine("## Comments");
            sb.AppendLine();
            foreach (var (author, date, bodyMd) in commentList)
            {
                sb.AppendLine($"### {author} — {date}");
                sb.AppendLine();
                sb.AppendLine(bodyMd);
                sb.AppendLine();
            }
        }
    }

    // Attachments as table (no download)
    if (f.TryGetProperty("attachment", out var attachNode) && attachNode.ValueKind == JsonValueKind.Array)
    {
        var attachments = new List<(string Filename, string Url, long Size)>();
        foreach (var a in attachNode.EnumerateArray())
        {
            var filename = GetString(a, "filename") ?? "unknown";
            var contentUrl = GetString(a, "content") ?? "";
            var size = a.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0;
            if (!string.IsNullOrEmpty(contentUrl))
                attachments.Add((filename, contentUrl, size));
        }

        if (attachments.Count > 0)
        {
            sb.AppendLine("## Attachments");
            sb.AppendLine();
            sb.AppendLine("| Filename | Size | Link |");
            sb.AppendLine("|----------|------|------|");
            foreach (var (filename, url, size) in attachments)
            {
                var sizeStr = size >= 1_048_576 ? $"{size / 1_048_576.0:F1} MB"
                    : size >= 1024 ? $"{size / 1024.0:F1} KB"
                    : $"{size} bytes";
                sb.AppendLine($"| {filename} | {sizeStr} | [Download]({url}) |");
            }
            sb.AppendLine();
        }
    }

    return sb.ToString();
}

class WorklogEntry
{
    public required string IssueKey { get; init; }
    public required string Summary { get; init; }
    public required string IssueType { get; init; }
    public string? ParentKey { get; init; }
    public string? ParentSummary { get; init; }
    public DateOnly Date { get; init; }
    public required string TimeSpent { get; init; }
    public int TimeSpentSeconds { get; init; }
}
