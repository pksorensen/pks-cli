namespace PKS.Infrastructure.Services.Brain;

/// Installs the daily `ingest → export → push` job.
///
/// Daily is not a rounded-up guess: opencode deletes spilled tool output older
/// than a hardcoded 7 days (`RETENTION = Duration.days(7)` in its
/// `tool-output-cleanup` job, no config and no env override). A daily run leaves
/// six days of slack for a laptop that was closed.
///
/// Spec: docs/specs/asf/05-blob-backup.md.
public interface IBrainDaemonService
{
    /// What `install` would write, without writing it.
    DaemonPlan Plan(DaemonOptions options);

    Task<DaemonResult> InstallAsync(DaemonOptions options, CancellationToken ct = default);

    Task<DaemonStatus> StatusAsync(CancellationToken ct = default);

    Task<DaemonResult> UninstallAsync(CancellationToken ct = default);
}

public sealed class DaemonOptions
{
    public string Level { get; init; } = Asf.AsfLevel.Metrics;
    public string Endpoint { get; init; } = PushOptions.DefaultEndpoint;

    /// Local time of day for the run. Default 03:30 — quiet, and long before a
    /// working day adds another 24 hours of sessions.
    public TimeOnly At { get; init; } = new(3, 30);

    /// Refresh the local brain (firehoses, index) before exporting. Off makes
    /// the job a pure backup.
    public bool IncludeIngest { get; init; } = true;

    /// Path to the pks executable the job should call. Defaults to this process.
    public string? ExecutablePath { get; init; }

    public bool Force { get; init; }
}

/// The scheduler this platform gets. Chosen by capability, not by OS name: a
/// container with no systemd falls back to cron rather than failing.
public enum DaemonScheduler
{
    None,
    Systemd,
    Launchd,
    SchTasks,
    Cron,
}

public sealed class DaemonPlan
{
    public DaemonScheduler Scheduler { get; init; }
    public string ScriptPath { get; init; } = "";
    public string ScriptBody { get; init; } = "";
    public string LogPath { get; init; } = "";

    /// Unit / plist / crontab line — whatever this scheduler is configured by.
    public List<(string Path, string Body)> Units { get; init; } = new();

    /// The commands run after the files are written.
    public List<string> Activation { get; init; } = new();
}

public sealed class DaemonResult
{
    public bool Ok { get; set; }
    public DaemonScheduler Scheduler { get; set; }
    public List<string> Wrote { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Ran { get; } = new();

    /// Set when the files are in place but activation failed — the user can
    /// finish by hand, and saying so is more useful than rolling back.
    public string? ManualStep { get; set; }

    public List<string> Problems { get; } = new();
}

public sealed class DaemonStatus
{
    public DaemonScheduler Scheduler { get; set; }
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
    public string? NextRun { get; set; }
    public string? LastRun { get; set; }
    public string ScriptPath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string? LogTail { get; set; }

    // Push state, read from the export manifest — the part that actually says
    // whether the backup is working.
    public string? Endpoint { get; set; }
    public int ChunksPending { get; set; }
    public int ChunksUploaded { get; set; }
    public int BlobsPending { get; set; }
    public DateTimeOffset? LastUpload { get; set; }
}
