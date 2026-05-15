namespace PKS.Infrastructure.Services.Brain;

/// Path resolver for the brain layout.
///
/// Two-layer model decided in /home/node/.claude/plans/atomic-mixing-allen.md:
///   - Global raw layer:       ~/.pks-cli/brain/
///   - Per-project synth layer: ./.pks/brain/ (under a project's git root)
///
/// Also owns the mapping between a real working directory and Claude Code's
/// "encoded slug" convention used as the project-dir name under ~/.claude/projects/
/// (e.g. /workspaces/agentic-live-www ↔ -workspaces-agentic-live-www).
public interface IBrainPathResolver
{
    /// Root of Claude Code's per-project session storage on this machine,
    /// honoring the ~/.config/claude/projects/ fallback used by ClaudeUsageCommand.
    string ClaudeProjectsRoot { get; }

    /// Root of Claude Code's plan files (~/.claude/plans/).
    string ClaudePlansRoot { get; }

    /// Global raw layer root (~/.pks-cli/brain/).
    string GlobalRoot { get; }

    /// Directory for a specific project's raw extracts under the global root
    /// (~/.pks-cli/brain/projects/&lt;slug&gt;/).
    string GlobalProjectDir(string slug);

    /// Per-session extract file under the global project dir
    /// (.../sessions/&lt;sessionId&gt;.json).
    string GlobalSessionFile(string slug, string sessionId);

    /// Path of a firehose JSONL under the global root.
    string GlobalFirehose(BrainFirehose firehose);

    /// Path of the master index (~/.pks-cli/brain/index.json).
    string GlobalIndexPath { get; }

    /// Path of the ingest-runs log (~/.pks-cli/brain/meta/ingest-runs.json).
    string GlobalIngestRunsPath { get; }

    /// Path of the plans cross-reference (~/.pks-cli/brain/plans.json).
    string GlobalPlansIndexPath { get; }

    /// Per-project synth root if cwd is inside (or is) a git working tree —
    /// e.g. /workspaces/agentic-live-www/.pks/brain/. Null when not in a repo.
    string? ResolveProjectRoot(string cwd);

    /// Convert a real path to Claude Code's encoded slug.
    /// Example: /workspaces/agentic-live-www → -workspaces-agentic-live-www.
    string EncodeSlug(string realPath);

    /// Best-effort reverse: given an encoded slug, return the candidate real path
    /// (replace '-' with '/' and prepend '/'). Verify by walking the candidate
    /// path on disk before trusting it.
    string DecodeSlug(string slug);

    /// Normalize a path: resolve symlinks if possible, fall back to GetFullPath.
    /// Returns null only when the input is null/empty.
    string? Normalize(string? path);
}

public enum BrainFirehose
{
    Prompts,
    Tools,
    Files,
    Errors,
}
