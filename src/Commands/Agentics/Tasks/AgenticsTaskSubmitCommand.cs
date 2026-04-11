using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Commands.Agentics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Tasks;

/// <summary>
/// Submit a task to an Assembly Line from a CI/CD pipeline.
/// Reads the runner token from the local registration config and optionally
/// enriches the task description with GitHub Actions context (job logs, PR info).
/// </summary>
public class AgenticsTaskSubmitCommand(
    IAgenticsRunnerConfigurationService configService,
    IGitHubAuthenticationService githubAuth,
    IAnsiConsole console) : Command<AgenticsTaskSubmitCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public class Settings : AgenticsSettings
    {
        [CommandOption("--assembly-line-url <URL>")]
        [Description("Full URL to the assembly line (e.g. https://agentics.dk/p/owner/project/assembly-lines/stage-id)")]
        public string? AssemblyLineUrl { get; set; }

        [CommandOption("--title <TITLE>")]
        [Description("Task title (required)")]
        public string? Title { get; set; }

        [CommandOption("--description <TEXT>")]
        [Description("Task description or raw error output")]
        public string? Description { get; set; }

        [CommandOption("--column-id <ID>")]
        [Description("Target column ID (defaults to the first column in the stage)")]
        public string? ColumnId { get; set; }

        [CommandOption("--priority <PRIORITY>")]
        [Description("Task priority: low, medium, high (default: high)")]
        [DefaultValue("high")]
        public string Priority { get; set; } = "high";

        [CommandOption("--labels <LABELS>")]
        [Description("Comma-separated labels to apply to the task")]
        public string? Labels { get; set; }

        [CommandOption("--server <URL>")]
        [Description("Override ALP server URL (default: from runner registration)")]
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
            // 1. Validate required inputs
            if (string.IsNullOrEmpty(settings.AssemblyLineUrl))
            {
                console.MarkupLine("[red]--assembly-line-url is required.[/]");
                return 1;
            }

            if (string.IsNullOrEmpty(settings.Title))
            {
                console.MarkupLine("[red]--title is required.[/]");
                return 1;
            }

            // 2. Parse URL → owner, project, stageId
            if (!TryParseAssemblyLineUrl(settings.AssemblyLineUrl, out var owner, out var project, out var stageId))
            {
                console.MarkupLine($"[red]Could not parse assembly line URL: {settings.AssemblyLineUrl.EscapeMarkup()}[/]");
                console.MarkupLine("[dim]Expected format: https://agentics.dk/p/{{owner}}/{{project}}/assembly-lines/{{stage-id}}[/]");
                return 1;
            }

            if (settings.Verbose)
            {
                console.MarkupLine($"[dim]Owner: {owner}  Project: {project}  Stage: {stageId}[/]");
            }

            // 3. Load runner registration for this owner/project
            var registrations = await configService.LoadAsync();
            var registration = registrations.Registrations
                .Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.RegisteredAt)
                .FirstOrDefault();

            if (registration == null)
            {
                console.MarkupLine($"[red]No runner registered for {owner}/{project}.[/]");
                console.MarkupLine($"[dim]Run: pks agentics runner register {owner}/{project}[/]");
                return 1;
            }

            var serverUrl = ResolveServerUrl(settings.Server ?? registration.Server);

            if (settings.Verbose)
            {
                console.MarkupLine($"[dim]Using runner: {registration.Name}  Server: {serverUrl}[/]");
            }

            // 4. Build description — enrich with GitHub Actions context if available
            var description = await BuildDescriptionAsync(settings);

            // 5. Build label list
            var labels = string.IsNullOrWhiteSpace(settings.Labels)
                ? Array.Empty<string>()
                : settings.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // 6. POST task to ALP server
            string? taskId = null;
            string? submitError = null;

            await console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Submitting task...", async _ =>
                {
                    try
                    {
                        using var http = new HttpClient();
                        http.DefaultRequestHeaders.Authorization =
                            new AuthenticationHeaderValue("Bearer", registration.Token);

                        var requestBody = new
                        {
                            title = settings.Title,
                            description,
                            columnId = settings.ColumnId,
                            priority = settings.Priority,
                            labels,
                        };

                        var url = $"{serverUrl}/api/owners/{owner}/projects/{project}/assembly-lines/{stageId}/tasks";
                        var response = await http.PostAsJsonAsync(url, requestBody, JsonOptions);

                        if (!response.IsSuccessStatusCode)
                        {
                            var body = await response.Content.ReadAsStringAsync();
                            submitError = $"Server returned {(int)response.StatusCode}: {body}";
                            return;
                        }

                        var json = await response.Content.ReadAsStringAsync();
                        var task = JsonSerializer.Deserialize<TaskResponse>(json, JsonOptions);
                        taskId = task?.Id;
                    }
                    catch (Exception ex)
                    {
                        submitError = ex.Message;
                    }
                });

            if (submitError != null)
            {
                console.MarkupLine($"[red]Failed to submit task: {submitError.EscapeMarkup()}[/]");
                return 1;
            }

            var taskUrl = $"{serverUrl}/p/{owner}/{project}/assembly-lines/{stageId}";
            console.MarkupLine($"[green]Task submitted: {taskId}[/]");
            console.MarkupLine($"[dim]{taskUrl}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Unexpected error: {ex.Message.EscapeMarkup()}[/]");
            if (settings.Verbose)
                console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Builds an enriched task description. When running inside GitHub Actions,
    /// fetches job logs and PR context via the GitHub API using the stored token.
    /// </summary>
    private async Task<string> BuildDescriptionAsync(Settings settings)
    {
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (!isGitHubActions)
            return settings.Description ?? string.Empty;

        var repo = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "";
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "";
        var runNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "1";
        var jobName = Environment.GetEnvironmentVariable("GITHUB_JOB") ?? "";
        var workflow = Environment.GetEnvironmentVariable("GITHUB_WORKFLOW") ?? "";
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "";
        var shaShort = sha.Length > 7 ? sha[..7] : sha;
        var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME") ?? "";
        var actor = Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? "";
        var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME") ?? "";
        var serverUrl = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL") ?? "https://github.com";

        var runUrl = $"{serverUrl}/{repo}/actions/runs/{runId}";

        var sb = new StringBuilder();
        sb.AppendLine("## CI/CD Failure");
        sb.AppendLine();
        sb.AppendLine($"**Workflow**: [{workflow} #{runNumber}]({runUrl})  ");
        sb.AppendLine($"**Job**: {jobName}  ");
        sb.AppendLine($"**Commit**: `{shaShort}` on `{refName}`  ");
        sb.AppendLine($"**Triggered by**: {actor} ({eventName})  ");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(settings.Description))
        {
            sb.AppendLine("### Error Context");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(settings.Description);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Attempt to enrich with GitHub API data
        if (!string.IsNullOrEmpty(repo) && !string.IsNullOrEmpty(runId))
        {
            await TryAppendGitHubJobLogsAsync(sb, repo, runId, jobName, serverUrl);
        }

        return sb.ToString();
    }

    private async System.Threading.Tasks.Task TryAppendGitHubJobLogsAsync(
        StringBuilder sb, string repo, string runId, string jobName, string githubServerUrl)
    {
        try
        {
            var storedToken = await githubAuth.GetStoredTokenAsync();
            if (storedToken?.AccessToken == null)
            {
                sb.AppendLine("_GitHub token not available — log enrichment skipped._");
                return;
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", storedToken.AccessToken);
            http.DefaultRequestHeaders.Add("User-Agent", "PKS-CLI/1.0");
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            // Fetch jobs for this run
            var jobsRes = await http.GetAsync(
                $"https://api.github.com/repos/{repo}/actions/runs/{runId}/jobs");
            if (!jobsRes.IsSuccessStatusCode) return;

            var jobsJson = await jobsRes.Content.ReadAsStringAsync();
            var jobsData = JsonSerializer.Deserialize<GitHubJobsResponse>(jobsJson, JsonOptions);
            if (jobsData?.Jobs == null) return;

            // Find job matching GITHUB_JOB (by name, case-insensitive)
            var matchingJob = jobsData.Jobs
                .FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase))
                ?? jobsData.Jobs.FirstOrDefault(j => j.Conclusion == "failure");

            if (matchingJob == null) return;

            // Collect failed step names
            var failedSteps = matchingJob.Steps?
                .Where(s => s.Conclusion == "failure")
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList() ?? new List<string?>();

            if (failedSteps.Count > 0)
            {
                sb.AppendLine($"**Failed steps**: {string.Join(", ", failedSteps)}  ");
                sb.AppendLine();
            }

            // Fetch job logs
            var logsRes = await http.GetAsync(
                $"https://api.github.com/repos/{repo}/actions/jobs/{matchingJob.Id}/logs");
            if (!logsRes.IsSuccessStatusCode) return;

            var logs = await logsRes.Content.ReadAsStringAsync();
            var logLines = logs.Split('\n');
            var last100 = logLines.Length > 100
                ? logLines[^100..]
                : logLines;

            sb.AppendLine("### Job Log (last 100 lines)");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(string.Join('\n', last100).TrimEnd());
            sb.AppendLine("```");
        }
        catch
        {
            // Log enrichment is best-effort — never fail the submission
        }
    }

    private static bool TryParseAssemblyLineUrl(
        string url,
        out string owner,
        out string project,
        out string stageId)
    {
        owner = project = stageId = string.Empty;
        try
        {
            var uri = new Uri(url);
            // Expected path: /p/{owner}/{project}/assembly-lines/{stageId}
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // segments: ["p", owner, project, "assembly-lines", stageId]
            if (segments.Length >= 5
                && string.Equals(segments[0], "p", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[3], "assembly-lines", StringComparison.OrdinalIgnoreCase))
            {
                owner = segments[1];
                project = segments[2];
                stageId = segments[4];
                return true;
            }
        }
        catch { }
        return false;
    }

    private static string ResolveServerUrl(string? server)
    {
        if (string.IsNullOrEmpty(server))
            server = "agentics.dk";

        if (server.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return server.TrimEnd('/');
        }

        var scheme = server.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     server.StartsWith("127.0.0.1")
            ? "http"
            : "https";
        return $"{scheme}://{server}";
    }

    // --- Response models ---

    private class TaskResponse
    {
        public string? Id { get; set; }
    }

    private class GitHubJobsResponse
    {
        [JsonPropertyName("jobs")]
        public List<GitHubJob>? Jobs { get; set; }
    }

    private class GitHubJob
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("steps")]
        public List<GitHubJobStep>? Steps { get; set; }
    }

    private class GitHubJobStep
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }
    }
}
