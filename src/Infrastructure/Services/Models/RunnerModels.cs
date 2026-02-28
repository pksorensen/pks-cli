namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// A registered repository to watch for queued workflow runs
/// </summary>
public class RunnerRegistration
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Owner { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Labels { get; set; } = "devcontainer-runner";
    public DateTime RegisteredAt { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Top-level configuration stored in ~/.pks-cli/runners.json
/// </summary>
public class RunnerConfiguration
{
    public List<RunnerRegistration> Registrations { get; set; } = new();
    public int PollingIntervalSeconds { get; set; } = 30;
    public int MaxConcurrentJobs { get; set; } = 1;
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Response from the GitHub JIT runner configuration API
/// </summary>
public class GitHubJitRunnerConfig
{
    public int RunnerId { get; set; }
    public string EncodedJitConfig { get; set; } = string.Empty;
}

/// <summary>
/// Tracks the state of an active runner job
/// </summary>
public class RunnerJobState
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public RunnerRegistration Registration { get; set; } = new();
    public long RunId { get; set; }
    public string Branch { get; set; } = string.Empty;
    public long? WorkflowJobId { get; set; }
    public string? ContainerName { get; set; }
    public string ContainerId { get; set; } = string.Empty;
    public string ClonePath { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public RunnerJobStatus Status { get; set; }
}

/// <summary>
/// Status of a runner job through its lifecycle
/// </summary>
public enum RunnerJobStatus
{
    Cloning,
    Building,
    Running,
    Completed,
    Failed,
    Cleaning
}

/// <summary>
/// Current state of the runner daemon
/// </summary>
public class RunnerDaemonStatus
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public List<RunnerJobState> ActiveJobs { get; set; } = new();
    public Dictionary<string, DateTime> LastPollTimes { get; set; } = new();
    public int TotalJobsCompleted { get; set; }
    public int TotalJobsFailed { get; set; }
    public List<NamedContainerEntry> NamedContainers { get; set; } = new();
}

/// <summary>
/// A queued workflow run retrieved from the GitHub API
/// </summary>
public class QueuedWorkflowRun
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string HeadBranch { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<string> Labels { get; set; } = new();
}

/// <summary>
/// GitHub API response wrapper for workflow runs
/// </summary>
public class WorkflowRunsResponse
{
    public int TotalCount { get; set; }
    public List<QueuedWorkflowRun> WorkflowRuns { get; set; } = new();
}

/// <summary>
/// GitHub API response for the JIT runner configuration endpoint.
/// POST /repos/{owner}/{repo}/actions/runners/generate-jitconfig
/// </summary>
public class GitHubJitRunnerConfigResponse
{
    public GitHubJitRunnerInfo Runner { get; set; } = new();
    public string EncodedJitConfig { get; set; } = string.Empty;
}

/// <summary>
/// Runner info nested inside the JIT config response
/// </summary>
public class GitHubJitRunnerInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// GitHub API response for a repository, used to check permissions
/// </summary>
public class GitHubRepositoryResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public GitHubRepositoryPermissions? Permissions { get; set; }
}

/// <summary>
/// Permission flags returned by the GitHub repository API
/// </summary>
public class GitHubRepositoryPermissions
{
    public bool Admin { get; set; }
    public bool Maintain { get; set; }
    public bool Push { get; set; }
    public bool Triage { get; set; }
    public bool Pull { get; set; }
}

/// <summary>
/// A job within a workflow run, fetched from GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs
/// </summary>
public class WorkflowJob
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Conclusion { get; set; }
    public List<string> Labels { get; set; } = new();
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// GitHub API response wrapper for workflow run jobs
/// </summary>
public class WorkflowJobsResponse
{
    public int TotalCount { get; set; }
    public List<WorkflowJob> Jobs { get; set; } = new();
}

/// <summary>
/// Tracks a named container that persists across jobs
/// </summary>
public class NamedContainerEntry
{
    public string Name { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public string ClonePath { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public bool InUse { get; set; }
}

/// <summary>
/// Describes how a job should be dispatched — ephemeral or named container
/// </summary>
public class JobDispatchInfo
{
    public WorkflowJob Job { get; set; } = new();
    public QueuedWorkflowRun Run { get; set; } = new();
    public RunnerRegistration Registration { get; set; } = new();
    /// <summary>
    /// Null means ephemeral (current behavior). Non-null means reuse/create a named container.
    /// </summary>
    public string? ContainerName { get; set; }
}
