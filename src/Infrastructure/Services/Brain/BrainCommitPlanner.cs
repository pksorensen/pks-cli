using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Implements FT-12 (Brain) commit-plan command — group uncommitted files by
/// shared session origin to enable focused commits.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public sealed class BrainCommitPlanner : IBrainCommitPlanner
{
    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    private readonly IBrainSessionScanner _scanner;

    public BrainCommitPlanner(IBrainSessionScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<BrainCommitPlanResult> PlanAsync(BrainCommitPlanOptions options, CancellationToken ct = default)
    {
        var inputFiles = options.Files.Distinct(StringComparer.Ordinal).ToList();

        // session_id -> file -> latest_timestamp of any edge (used for "all touched" + contributing/shared semantic)
        var sessionFileTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        // session_id -> file -> latest_timestamp of EDIT-class tool_use only (drives primary-session heuristic)
        var sessionFileEditTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        // session_id -> path to its JSONL (any edge will do — same per session)
        var sessionJsonl = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessionScannedJsonls = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in inputFiles)
        {
            ct.ThrowIfCancellationRequested();
            var scanRes = await _scanner.ScanAsync(new BrainScanOptions
            {
                TargetPath = file,
                ProjectsDir = options.ProjectsDir,
                IncludeBash = options.IncludeBash,
                SinceUtc = options.SinceUtc,
                TargetIsDirectory = false,
            }, ct);

            foreach (var edge in scanRes.Edges)
            {
                if (!sessionFileTs.TryGetValue(edge.SessionId, out var fileMap))
                {
                    fileMap = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    sessionFileTs[edge.SessionId] = fileMap;
                }
                if (!fileMap.TryGetValue(edge.FilePath, out var existing) || edge.TimestampUtc > existing)
                    fileMap[edge.FilePath] = edge.TimestampUtc;

                if (EditTools.Contains(edge.ToolName))
                {
                    if (!sessionFileEditTs.TryGetValue(edge.SessionId, out var editMap))
                    {
                        editMap = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                        sessionFileEditTs[edge.SessionId] = editMap;
                    }
                    if (!editMap.TryGetValue(edge.FilePath, out var existingEdit) || edge.TimestampUtc > existingEdit)
                        editMap[edge.FilePath] = edge.TimestampUtc;
                }

                if (!sessionJsonl.ContainsKey(edge.SessionId))
                    sessionJsonl[edge.SessionId] = edge.JsonlPath;

                sessionScannedJsonls.Add(edge.SessionId);
            }
        }

        var minFiles = Math.Max(1, options.MinFiles);

        // For each file, the session that most-recently edited it = "last-edit-author".
        var lastEditor = new Dictionary<string, (string SessionId, DateTime Ts)>(StringComparer.Ordinal);
        foreach (var (sessionId, editMap) in sessionFileEditTs)
        {
            foreach (var (file, ts) in editMap)
            {
                if (!lastEditor.TryGetValue(file, out var cur) || ts > cur.Ts)
                    lastEditor[file] = (sessionId, ts);
            }
        }

        // Count for each session: how many files it is last-editor of.
        var sessionLastEditFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (file, owner) in lastEditor)
        {
            if (!sessionLastEditFiles.TryGetValue(owner.SessionId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                sessionLastEditFiles[owner.SessionId] = set;
            }
            set.Add(file);
        }

        var qualified = sessionLastEditFiles
            .Where(kv => kv.Value.Count >= minFiles)
            .Select(kv => new
            {
                SessionId = kv.Key,
                Files = kv.Value,
                Latest = kv.Value.Max(f => lastEditor[f].Ts),
            })
            .OrderByDescending(s => s.Files.Count)
            .ThenByDescending(s => s.Latest)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();

        var assigned = new Dictionary<string, int>(StringComparer.Ordinal); // file -> group_id
        var groups = new List<BrainCommitGroup>();
        int nextGroupId = 1;

        foreach (var sess in qualified)
        {
            // "All touched" file set across edit + read used for shared-files semantic.
            var allTouched = sessionFileTs.TryGetValue(sess.SessionId, out var ft)
                ? ft.Keys
                : Enumerable.Empty<string>();

            var shared = allTouched.Where(f => assigned.ContainsKey(f))
                .ToDictionary(f => f, f => assigned[f], StringComparer.Ordinal);

            var unassigned = sess.Files.Where(f => !assigned.ContainsKey(f)).ToList();

            bool isFirst = groups.Count == 0;
            List<string> groupFiles;
            if (isFirst)
            {
                // First group: claim ALL last-edit files of the primary session.
                groupFiles = sess.Files.OrderBy(f => f, StringComparer.Ordinal).ToList();
                shared.Clear();
            }
            else
            {
                if (unassigned.Count < minFiles) continue;
                groupFiles = unassigned.OrderBy(f => f, StringComparer.Ordinal).ToList();
            }

            // Files we are about to claim shouldn't appear in "shared".
            foreach (var f in groupFiles) shared.Remove(f);

            int groupId = nextGroupId++;
            foreach (var f in groupFiles)
                assigned[f] = groupId;

            groups.Add(new BrainCommitGroup
            {
                GroupId = groupId,
                Files = groupFiles,
                PrimarySession = sess.SessionId,
                LatestTimestampUtc = sess.Latest,
                SharedFiles = shared,
            });
        }

        // Contributing sessions: for each group, find OTHER sessions touching >= 2
        // of the group's files (regardless of assignment).
        foreach (var group in groups)
        {
            var groupFileSet = group.Files.ToHashSet(StringComparer.Ordinal);
            var contribs = new List<BrainCommitContributingSession>();
            foreach (var (sessionId, fileMap) in sessionFileTs)
            {
                if (sessionId == group.PrimarySession) continue;
                int count = fileMap.Keys.Count(f => groupFileSet.Contains(f));
                if (count >= 2)
                    contribs.Add(new BrainCommitContributingSession { SessionId = sessionId, FileCount = count });
            }
            contribs.Sort((a, b) =>
            {
                int byCount = b.FileCount.CompareTo(a.FileCount);
                return byCount != 0 ? byCount : string.CompareOrdinal(a.SessionId, b.SessionId);
            });
            group.ContributingSessions.AddRange(contribs);
        }

        if (options.IncludePrompts)
        {
            foreach (var group in groups)
            {
                if (!sessionJsonl.TryGetValue(group.PrimarySession, out var jsonl)) continue;
                var prompts = await ExtractPromptsAsync(jsonl, group.Files.ToHashSet(StringComparer.Ordinal), ct);
                group.Prompts.AddRange(prompts);
            }
        }

        var ungrouped = inputFiles
            .Where(f => !assigned.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new BrainCommitPlanResult
        {
            Groups = groups,
            Ungrouped = ungrouped,
            InputFiles = inputFiles.Count,
            ScannedSessions = sessionScannedJsonls.Count,
        };
    }

    private static async Task<List<BrainCommitGroupPrompt>> ExtractPromptsAsync(
        string jsonlPath, HashSet<string> targetFiles, CancellationToken ct)
    {
        var collected = new List<BrainCommitGroupPrompt>();
        var seenTexts = new HashSet<string>(StringComparer.Ordinal);

        if (!File.Exists(jsonlPath)) return collected;

        (DateTime Ts, string Text)? lastUserPrompt = null;

        await using var stream = new FileStream(jsonlPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? raw;
        while ((raw = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(raw); }
            catch (JsonException) { continue; }

            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
            var type = typeEl.GetString();
            var ts = TryDate(root, "timestamp") ?? DateTime.UtcNow;

            if (type == "user")
            {
                if (TryExtractUserText(root, out var text))
                    lastUserPrompt = (ts, text);
            }
            else if (type == "assistant")
            {
                if (!root.TryGetProperty("message", out var msg)) continue;
                if (msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind != JsonValueKind.Array) continue;

                bool matched = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    if (!block.TryGetProperty("type", out var bt) || bt.ValueKind != JsonValueKind.String) continue;
                    if (bt.GetString() != "tool_use") continue;
                    if (!block.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) continue;
                    var name = nameEl.GetString();
                    if (name is null || !EditTools.Contains(name)) continue;
                    if (!block.TryGetProperty("input", out var input)) continue;
                    if (input.ValueKind != JsonValueKind.Object) continue;
                    if (!input.TryGetProperty("file_path", out var fpEl) || fpEl.ValueKind != JsonValueKind.String) continue;
                    var fp = fpEl.GetString();
                    if (fp is not null && targetFiles.Contains(fp))
                    {
                        matched = true;
                        break;
                    }
                }

                if (matched && lastUserPrompt is { } lp && seenTexts.Add(lp.Text))
                {
                    collected.Add(new BrainCommitGroupPrompt
                    {
                        TimestampUtc = lp.Ts,
                        Text = Truncate(lp.Text, 500),
                    });
                }
            }
        }

        if (collected.Count > 10)
        {
            // Most recent first when truncating, but return back in chronological order.
            collected = collected
                .OrderByDescending(p => p.TimestampUtc)
                .Take(10)
                .OrderBy(p => p.TimestampUtc)
                .ToList();
        }
        return collected;
    }

    private static bool TryExtractUserText(JsonElement root, out string text)
    {
        text = string.Empty;
        if (!root.TryGetProperty("message", out var msg)) return false;
        if (msg.ValueKind != JsonValueKind.Object) return false;
        if (!msg.TryGetProperty("content", out var content)) return false;

        if (content.ValueKind == JsonValueKind.String)
        {
            var s = content.GetString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            text = s!;
            return true;
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var b in content.EnumerateArray())
            {
                if (b.ValueKind != JsonValueKind.Object) continue;
                if (!b.TryGetProperty("type", out var bt) || bt.ValueKind != JsonValueKind.String) continue;
                if (bt.GetString() != "text") continue;
                if (!b.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var s = t.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(s);
            }
            if (sb.Length == 0) return false;
            text = sb.ToString();
            return true;
        }
        return false;
    }

    private static DateTime? TryDate(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind != JsonValueKind.String) return null;
        if (DateTime.TryParse(p.GetString(), null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var dt)) return dt;
        return null;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }
}
