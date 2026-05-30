using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Commands.Writing;

public class NaturalnessApplySettings : WritingSettings
{
    [CommandArgument(0, "<file>")]
    [Description("Markdown file whose picks to apply.")]
    public string File { get; set; } = "";

    [CommandOption("--yes")]
    [Description("Skip the confirmation prompt and apply the diff immediately.")]
    public bool Yes { get; set; }

    [CommandOption("--dry-run")]
    [Description("Print the diff but don't touch the source file or pattern store.")]
    public bool DryRun { get; set; }
}

/// `pks writing naturalness apply <file>` — reads picks, generates a unified
/// diff, confirms, applies in-place. Appends accepted patterns to the global
/// learning store, flips `applied: true` for idempotency.
public class NaturalnessApplyCommand : AsyncCommand<NaturalnessApplySettings>
{
    private readonly INaturalnessPicksStore _store;
    private readonly INaturalnessApplier _applier;
    private readonly INaturalnessPatternStore _patterns;

    public NaturalnessApplyCommand(
        INaturalnessPicksStore store,
        INaturalnessApplier applier,
        INaturalnessPatternStore patterns)
    {
        _store = store;
        _applier = applier;
        _patterns = patterns;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, NaturalnessApplySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.File))
        {
            AnsiConsole.MarkupLine("[red]error:[/] file argument required.");
            return 1;
        }
        var full = System.IO.Path.GetFullPath(settings.File);
        if (!System.IO.File.Exists(full))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] not found: [cyan]{Markup.Escape(full)}[/]");
            return 1;
        }

        var candidates = await _store.LoadCandidatesAsync(full);
        var picks = await _store.LoadPicksAsync(full);
        if (candidates is null || picks is null)
        {
            AnsiConsole.MarkupLine("[yellow]nothing to apply.[/] Run `naturalness review` first.");
            return 1;
        }

        var content = await System.IO.File.ReadAllTextAsync(full);
        var plan = _applier.Plan(content, candidates, picks);

        if (plan.Edits.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]no unapplied picks; nothing to do.[/]");
            return 0;
        }

        AnsiConsole.Write(new Rule($"[bold magenta]naturalness apply[/] [grey]({plan.Edits.Count} edit(s))[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(plan.UnifiedDiff);

        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[grey]dry-run: nothing written.[/]");
            return 0;
        }

        if (!settings.Yes)
        {
            var ok = AnsiConsole.Confirm("Apply these edits?");
            if (!ok)
            {
                AnsiConsole.MarkupLine("[grey]aborted.[/]");
                return 0;
            }
        }

        // Archive the pre-apply state as <locale>.v<N>.md before overwriting,
        // and rotate the naturalness sidecars + writing report to matching
        // v<N> names. Keeps a versioned trail of what the post looked like
        // before each naturalness pass + the picks/candidates that drove it.
        // Summary is left empty — Apply has no LLM access; a later step can
        // synthesise version_summary from the archived sidecars.
        var archiveInfo = ArchivePreApplyVersion(full);
        if (archiveInfo is not null)
        {
            AnsiConsole.MarkupLine($"[grey]archived pre-apply state to[/] [cyan]{Markup.Escape(System.IO.Path.GetFileName(archiveInfo.VersionedPostPath))}[/] [grey](v{archiveInfo.Version})[/]");
            foreach (var s in archiveInfo.ArchivedSidecars)
                AnsiConsole.MarkupLine($"  [grey]+ {Markup.Escape(System.IO.Path.GetFileName(s))}[/]");
        }

        var result = _applier.Apply(content, plan);
        // Once we've archived the pre-apply snapshot (which carries the
        // user-curated version_* block describing it), the new canonical
        // should shed those fields — it's "the latest", not a numbered
        // version. Otherwise listPostVersions on the web side surfaces the
        // archived label *and* the canonical with the same label, leading to
        // a duplicate row in the version-history disclosure.
        var finalContent = archiveInfo is not null
            ? StripVersionFrontmatter(result.NewContent)
            : result.NewContent;
        await System.IO.File.WriteAllTextAsync(full, finalContent);

        // Mark applied + persist patterns
        var appliedIds = new HashSet<string>(result.Applied.Select(e => e.CandidateId));
        foreach (var pick in picks.Picks)
        {
            if (appliedIds.Contains(pick.CandidateId)) pick.Applied = true;
        }
        await _store.SavePicksAsync(full, picks);

        // Once the archive copy + new canonical are both safe on disk, the
        // canonical CANDIDATES / PICKS sidecars are stale — they reference
        // sentences that no longer exist in the rewritten body. Wipe them so
        // the next `naturalness extract` run starts from a clean slate and
        // the web review UI's counter resets from "25/25 picked" (which made
        // it look like the post still had pending work) back to "no review
        // pending". The v<N> archives still hold the full history.
        if (archiveInfo is not null)
        {
            var wiped = WipeCanonicalReviewSidecars(full);
            foreach (var p in wiped)
                AnsiConsole.MarkupLine($"  [grey]- {Markup.Escape(System.IO.Path.GetFileName(p))} (stale; reference pre-apply body)[/]");
        }

        foreach (var edit in result.Applied)
        {
            var pattern = new NaturalnessPattern
            {
                TriggerSummary = edit.TriggerSummary,
                AcceptedExample = edit.Replacement,
                RejectedExample = edit.Original,
                FirstSeenSource = $"{DateTime.UtcNow:yyyy-MM-dd} / {System.IO.Path.GetFileName(full)}:{edit.Line}",
                AcceptedCount = 1,
                AcceptedFromCritic = edit.AcceptedFromCritic,
            };
            await _patterns.UpsertAsync(pattern);
        }

        var t = new Table().Border(TableBorder.Minimal).HideHeaders();
        t.AddColumn(""); t.AddColumn(new TableColumn("").RightAligned());
        t.AddRow("Applied",   $"[green]{result.Applied.Count}[/]");
        t.AddRow("Skipped",   $"[grey]{result.Skipped.Count}[/]");
        t.AddRow("Patterns",  $"[green]{result.Applied.Count}[/] upserted");
        if (result.Warnings.Count > 0)
            t.AddRow("Warnings", $"[yellow]{result.Warnings.Count}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(t);
        foreach (var w in result.Warnings)
            AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(w)}");
        return 0;
    }

    private sealed record ArchiveInfo(int Version, string VersionedPostPath, IReadOnlyList<string> ArchivedSidecars);

    /// <summary>
    /// Archives the current pre-apply state of <paramref name="canonicalPath"/>
    /// to <c>&lt;locale&gt;.v&lt;N&gt;.md</c> with versioning frontmatter
    /// injected, and copies the matching naturalness sidecars + writing
    /// report (if present) to <c>&lt;locale&gt;.v&lt;N&gt;.&lt;suffix&gt;</c>
    /// names in the same <c>_review/</c> folder.
    /// </summary>
    /// <remarks>
    /// Versioning convention: see <c>docs/blog-versioning.md</c> in the
    /// agentic-live-www repo. Returns <c>null</c> only if archiving fails in
    /// a way that's safe to ignore (e.g. unreadable frontmatter); the apply
    /// still proceeds — we'd rather lose a versioned snapshot than block the
    /// reviewer's accepted picks from landing.
    /// </remarks>
    private static ArchiveInfo? ArchivePreApplyVersion(string canonicalPath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(canonicalPath) ?? ".";
            var locale = System.IO.Path.GetFileNameWithoutExtension(canonicalPath); // "da" / "en"
            if (string.IsNullOrEmpty(locale)) return null;

            var nextVersion = NextVersionNumber(dir, locale);
            var archivePath = System.IO.Path.Combine(dir, $"{locale}.v{nextVersion}.md");
            if (System.IO.File.Exists(archivePath)) return null; // race / unexpected; bail

            var raw = System.IO.File.ReadAllText(canonicalPath);
            var withVersionedFrontmatter = InjectVersionFrontmatter(raw, nextVersion);
            System.IO.File.WriteAllText(archivePath, withVersionedFrontmatter);

            var archivedSidecars = new List<string>();
            var reviewDir = System.IO.Path.Combine(dir, "_review");
            if (System.IO.Directory.Exists(reviewDir))
            {
                // Match every sidecar that starts with "<locale>." and isn't
                // itself already a v<N> file. Common names:
                //   da.NATURALNESS-CANDIDATES.json
                //   da.NATURALNESS-CANDIDATES.opus.json
                //   da.NATURALNESS-CANDIDATES.gpt5.json
                //   da.NATURALNESS-PICKS.json
                //   da.WRITING-REPORT.json
                //   da.WRITING-REPORT.md
                //   da.LEARN.json / da.LEARN.md
                var versionTokenRegex = new System.Text.RegularExpressions.Regex(
                    $"^{System.Text.RegularExpressions.Regex.Escape(locale)}\\.v\\d+\\.",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var p in System.IO.Directory.GetFiles(reviewDir))
                {
                    var name = System.IO.Path.GetFileName(p);
                    if (!name.StartsWith($"{locale}.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (versionTokenRegex.IsMatch(name)) continue;

                    var suffix = name.Substring(locale.Length + 1); // strip "<locale>."
                    var target = System.IO.Path.Combine(reviewDir, $"{locale}.v{nextVersion}.{suffix}");
                    if (System.IO.File.Exists(target)) continue;
                    System.IO.File.Copy(p, target);
                    archivedSidecars.Add(target);
                }
            }

            return new ArchiveInfo(nextVersion, archivePath, archivedSidecars);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the canonical (non-versioned) naturalness CANDIDATES and PICKS
    /// sidecars next to <paramref name="canonicalPath"/>. Leaves WRITING-REPORT,
    /// LEARN, NATURALNESS-PATTERNS, and any v&lt;N&gt; archives untouched.
    /// </summary>
    private static IReadOnlyList<string> WipeCanonicalReviewSidecars(string canonicalPath)
    {
        var deleted = new List<string>();
        try
        {
            var dir = System.IO.Path.GetDirectoryName(canonicalPath) ?? ".";
            var locale = System.IO.Path.GetFileNameWithoutExtension(canonicalPath);
            var reviewDir = System.IO.Path.Combine(dir, "_review");
            if (!System.IO.Directory.Exists(reviewDir)) return deleted;

            var versionTokenRegex = new System.Text.RegularExpressions.Regex(
                $"^{System.Text.RegularExpressions.Regex.Escape(locale)}\\.v\\d+\\.",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var p in System.IO.Directory.GetFiles(reviewDir))
            {
                var name = System.IO.Path.GetFileName(p);
                if (!name.StartsWith($"{locale}.", StringComparison.OrdinalIgnoreCase)) continue;
                if (versionTokenRegex.IsMatch(name)) continue; // never touch archives
                // Only the two file families the apply just made stale:
                if (name.IndexOf("NATURALNESS-CANDIDATES", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("NATURALNESS-PICKS", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    System.IO.File.Delete(p);
                    deleted.Add(p);
                }
                catch { /* keep going — best-effort cleanup */ }
            }
        }
        catch
        {
            // Best-effort; if cleanup fails the v<N> archive still preserves history.
        }
        return deleted;
    }

    private static int NextVersionNumber(string dir, string locale)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            $"^{System.Text.RegularExpressions.Regex.Escape(locale)}\\.v(\\d+)\\.md$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var max = 0;
        if (System.IO.Directory.Exists(dir))
        {
            foreach (var p in System.IO.Directory.GetFiles(dir, $"{locale}.v*.md"))
            {
                var name = System.IO.Path.GetFileName(p);
                var m = pattern.Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > max) max = n;
            }
        }
        return max + 1;
    }

    /// <summary>
    /// Injects <c>version:</c> / <c>version_label:</c> / <c>version_summary:</c>
    /// / <c>version_kind:</c> / <c>version_date:</c> into the post's
    /// frontmatter (between the opening and closing <c>---</c> markers).
    /// Leaves <c>version_summary</c> empty — a later AI pass can fill it from
    /// the archived sidecars. If the file has no frontmatter, returns the
    /// raw content unchanged.
    /// </summary>
    /// <summary>
    /// Removes <c>version:</c>, <c>version_label:</c>, <c>version_summary:</c>,
    /// <c>version_kind:</c>, <c>version_date:</c> from the frontmatter so the
    /// canonical post no longer carries metadata describing an earlier
    /// snapshot. Leaves all other frontmatter keys and the body untouched.
    /// </summary>
    private static string StripVersionFrontmatter(string raw)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal)) return raw;
        var lines = raw.Split('\n');
        var closeIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r') == "---") { closeIdx = i; break; }
        }
        if (closeIdx <= 0) return raw;

        var versionKeyPrefixes = new[]
        {
            "version:", "version_label:", "version_summary:",
            "version_kind:", "version_date:",
        };
        var kept = new List<string>(lines.Length);
        for (var i = 0; i < closeIdx; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var isVersionKey = false;
            foreach (var pfx in versionKeyPrefixes)
            {
                if (trimmed.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                {
                    isVersionKey = true;
                    break;
                }
            }
            if (!isVersionKey) kept.Add(line);
        }
        for (var i = closeIdx; i < lines.Length; i++) kept.Add(lines[i]);
        return string.Join('\n', kept);
    }

    private static string InjectVersionFrontmatter(string raw, int version)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal)) return raw;
        var lines = raw.Split('\n');
        // Find the closing --- on its own line.
        var closeIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r') == "---") { closeIdx = i; break; }
        }
        if (closeIdx <= 0) return raw;

        // If the canonical's frontmatter already carries any version_* key,
        // leave it alone — the user has likely curated `version_label` /
        // `version_summary` to describe the soon-to-be-archived snapshot,
        // and overwriting it would lose authored intent. We still want to
        // archive *something*, so we just keep the file verbatim. Only when
        // the canonical has no version metadata do we inject a stub so an
        // AI summariser later has a place to write into.
        var hasExistingVersion = false;
        for (var i = 1; i < closeIdx; i++)
        {
            if (lines[i].TrimStart().StartsWith("version:", StringComparison.OrdinalIgnoreCase))
            {
                hasExistingVersion = true;
                break;
            }
        }
        if (hasExistingVersion) return raw;

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var insert = new List<string>
        {
            $"version: {version}",
            $"version_label: \"naturalness-pre-apply-v{version}\"",
            $"version_summary: \"\"",
            $"version_kind: iteration",
            $"version_date: \"{today}\"",
        };
        var kept = new List<string>(lines.Length + insert.Count);
        for (var i = 0; i < closeIdx; i++) kept.Add(lines[i]);
        kept.AddRange(insert);
        for (var i = closeIdx; i < lines.Length; i++) kept.Add(lines[i]);
        return string.Join('\n', kept);
    }
}
