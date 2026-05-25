namespace PKS.Infrastructure.Services.Writing;

/// Path resolver for the `pks writing` layout.
///
/// Two-layer model mirroring [[IBrainPathResolver]] (see
/// /home/node/.claude/plans/lazy-dazzling-neumann.md):
///   - Global portable layer: ~/.pks-cli/writing/  (the writer profile asset)
///   - Per-project layer:     ./.pks/writing/      (overrides + report cache)
///
/// Lint/score reports are written *next to the source file* as
/// `WRITING-REPORT.md` + `.json`, not under the project layer.
public interface IWritingPathResolver
{
    // ── global (portable profile) ──────────────────────────────────────────────
    string GlobalRoot { get; }
    string GlobalProfilePath { get; }
    string GlobalAnglicismsPath { get; }
    string GlobalAllowlistPath { get; }
    string GlobalChannelsDir { get; }
    string GlobalChannelRubricPath(string channel);
    string GlobalValeDir { get; }
    string GlobalValeBinDir { get; }
    string GlobalValeConfigPath { get; }
    string GlobalValeStylesDir { get; }

    /// Reference corpus root (~/.pks-cli/writing/reference/).
    /// Sentences/posts here become few-shot examples for the LLM critic.
    string GlobalReferenceDir { get; }
    string GlobalReferenceChannelDir(string channel);

    /// Append-only log of lessons learned via `pks writing learn`.
    string GlobalLessonsPath { get; }

    /// Persistent cowork prompt seeded at init — used by `pks writing profile author`.
    string GlobalAuthoringPromptPath { get; }

    // ── per-project (overrides + cache) ────────────────────────────────────────
    /// Returns <repo>/.pks/writing/ if cwd is inside a git repo, else null.
    string? ResolveProjectRoot(string cwd);
    string ProjectChannelConfigPath(string projectRoot);
    string ProjectOverridesAnglicismsPath(string projectRoot);
    string ProjectReportsDir(string projectRoot);
    string ProjectReportCachePath(string projectRoot, string sourceFilePath);

    // ── per-source sidecars ────────────────────────────────────────────────────
    /// Review folder next to the source file (e.g. <dir>/_review/).
    /// All tool output for this source goes here so nothing in the source
    /// dir is touched (avoids clobbering blog/CMS scanners that read *.md).
    string ReviewDir(string sourceFilePath);

    string ReportSidecarMarkdownPath(string sourceFilePath);
    string ReportSidecarJsonPath(string sourceFilePath);

    string LearnSidecarMarkdownPath(string sourceFilePath);
    string LearnSidecarJsonPath(string sourceFilePath);
}
