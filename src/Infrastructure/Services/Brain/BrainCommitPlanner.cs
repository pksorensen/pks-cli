using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Implements FT-12 (Brain) commit-plan command — group uncommitted files by
/// shared session origin to enable focused commits.
///
/// Two data paths:
///   1. **Firehose graph** (primary, fast — ~10 ms for 50 files):
///      reads <c>~/.pks-cli/brain/files.jsonl</c> and <c>prompts.jsonl</c>
///      via <see cref="IFirehoseReader"/>. Optionally runs <c>brain ingest</c>
///      first so the firehose is fresh.
///   2. **Per-file scanner fallback** (legacy, ~200 ms/file): used when the
///      firehose is absent or when <c>--force-scan</c> is supplied. Re-parses
///      every <c>~/.claude/projects/*.jsonl</c> from scratch.
///
/// See .pks/brain/feature-specs/FT-012-*.md.
/// </summary>
public sealed class BrainCommitPlanner : IBrainCommitPlanner
{
    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "NotebookEdit",
    };

    // FileOpRow.Op values that represent a write (not a read).
    private static readonly HashSet<string> EditOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "multi-edit", "notebook-edit",
    };

    private readonly IBrainSessionScanner _scanner;
    private readonly IFirehoseReader? _firehose;
    private readonly IBrainPathResolver? _paths;
    private readonly IBrainIngestPipeline? _ingest;

    /// <summary>
    /// Legacy single-arg constructor — used by tests that drive the scanner-only
    /// path directly. The firehose-aware overload is preferred in production.
    /// </summary>
    public BrainCommitPlanner(IBrainSessionScanner scanner)
    {
        _scanner = scanner;
    }

    public BrainCommitPlanner(
        IBrainSessionScanner scanner,
        IFirehoseReader firehose,
        IBrainPathResolver paths,
        IBrainIngestPipeline ingest)
    {
        _scanner = scanner;
        _firehose = firehose;
        _paths = paths;
        _ingest = ingest;
    }

    public async Task<BrainCommitPlanResult> PlanAsync(BrainCommitPlanOptions options, CancellationToken ct = default)
    {
        var inputFiles = options.Files.Distinct(StringComparer.Ordinal).ToList();

        // Prefer the firehose unless explicitly disabled or unavailable.
        if (!options.ForceScan && _firehose is not null && _paths is not null)
        {
            var firehosePath = _paths.GlobalFirehose(BrainFirehose.Files);
            if (File.Exists(firehosePath))
            {
                if (options.AutoRefresh && _ingest is not null)
                {
                    await _ingest.RunAsync(
                        new IngestOptions
                        {
                            Force = false,
                            MaxParallelism = Environment.ProcessorCount,
                        },
                        NullIngestProgress.Instance,
                        ct);
                }

                return await PlanFromFirehoseAsync(inputFiles, options, ct);
            }
        }

        return await PlanFromScannerAsync(inputFiles, options, ct);
    }

    // ── Firehose path ──────────────────────────────────────────────────────────

    private async Task<BrainCommitPlanResult> PlanFromFirehoseAsync(
        List<string> inputFiles, BrainCommitPlanOptions options, CancellationToken ct)
    {
        var inputSet = inputFiles.ToHashSet(StringComparer.Ordinal);

        var sessionFileTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        var sessionFileEditTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        var sessions = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var row in _firehose!.ReadAsync<FileOpRow>(BrainFirehose.Files, sessionId: null, ct))
        {
            if (!inputSet.Contains(row.FilePath)) continue;
            if (options.SinceUtc is { } since && row.TimestampUtc < since) continue;

            sessions.Add(row.SessionId);

            if (!sessionFileTs.TryGetValue(row.SessionId, out var fileMap))
            {
                fileMap = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                sessionFileTs[row.SessionId] = fileMap;
            }
            if (!fileMap.TryGetValue(row.FilePath, out var existing) || row.TimestampUtc > existing)
                fileMap[row.FilePath] = row.TimestampUtc;

            if (EditOps.Contains(row.Op))
            {
                if (!sessionFileEditTs.TryGetValue(row.SessionId, out var editMap))
                {
                    editMap = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                    sessionFileEditTs[row.SessionId] = editMap;
                }
                if (!editMap.TryGetValue(row.FilePath, out var existingEdit) || row.TimestampUtc > existingEdit)
                    editMap[row.FilePath] = row.TimestampUtc;
            }
        }

        var (groups, assigned) = AssembleGroups(sessionFileTs, sessionFileEditTs, options);

        // Contributing sessions (other sessions touching ≥2 files of this group).
        AttachContributingSessions(groups, sessionFileTs);

        if (options.IncludePrompts)
        {
            foreach (var group in groups)
            {
                var groupFiles = group.Files.ToHashSet(StringComparer.Ordinal);
                var prompts = await ExtractPromptsFromFirehoseAsync(
                    group.PrimarySession,
                    groupFiles,
                    sessionFileEditTs.GetValueOrDefault(group.PrimarySession),
                    ct);
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
            ScannedSessions = sessions.Count,
        };
    }

    private async Task<List<BrainCommitGroupPrompt>> ExtractPromptsFromFirehoseAsync(
        string sessionId,
        HashSet<string> targetFiles,
        Dictionary<string, DateTime>? editMap,
        CancellationToken ct)
    {
        var collected = new List<BrainCommitGroupPrompt>();
        if (editMap is null) return collected;

        // Collect edit timestamps for the target files in this session.
        var editTs = editMap
            .Where(kv => targetFiles.Contains(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        if (editTs.Count == 0) return collected;

        // Stream all prompts for this session; materialise sorted by timestamp.
        var prompts = new List<PromptRow>();
        await foreach (var p in _firehose!.ReadAsync<PromptRow>(BrainFirehose.Prompts, sessionId, ct))
            prompts.Add(p);
        if (prompts.Count == 0) return collected;

        prompts.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        var promptTs = prompts.Select(p => p.TimestampUtc).ToArray();

        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ts in editTs.OrderBy(t => t))
        {
            // Find latest prompt with prompts[i].Ts <= ts.
            var idx = BinarySearchLatestLeq(promptTs, ts);
            if (idx < 0) continue;
            var p = prompts[idx];
            if (string.IsNullOrEmpty(p.Text)) continue;
            if (!seenTexts.Add(p.Text)) continue;
            collected.Add(new BrainCommitGroupPrompt
            {
                TimestampUtc = p.TimestampUtc,
                Text = Truncate(p.Text, 500),
            });
        }

        if (collected.Count > 10)
        {
            collected = collected
                .OrderByDescending(p => p.TimestampUtc)
                .Take(10)
                .OrderBy(p => p.TimestampUtc)
                .ToList();
        }
        else
        {
            collected = collected.OrderBy(p => p.TimestampUtc).ToList();
        }
        return collected;
    }

    private static int BinarySearchLatestLeq(DateTime[] sorted, DateTime needle)
    {
        int lo = 0, hi = sorted.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid] <= needle) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    // ── Scanner fallback path (unchanged behaviour) ────────────────────────────

    private async Task<BrainCommitPlanResult> PlanFromScannerAsync(
        List<string> inputFiles, BrainCommitPlanOptions options, CancellationToken ct)
    {
        var sessionFileTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
        var sessionFileEditTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
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

        var (groups, assigned) = AssembleGroups(sessionFileTs, sessionFileEditTs, options);
        AttachContributingSessions(groups, sessionFileTs);

        if (options.IncludePrompts)
        {
            foreach (var group in groups)
            {
                if (!sessionJsonl.TryGetValue(group.PrimarySession, out var jsonl)) continue;
                var prompts = await ExtractPromptsFromJsonlAsync(jsonl, group.Files.ToHashSet(StringComparer.Ordinal), ct);
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

    // ── Shared group-selection algorithm ───────────────────────────────────────

    private static (List<BrainCommitGroup> Groups, Dictionary<string, int> Assigned) AssembleGroups(
        Dictionary<string, Dictionary<string, DateTime>> sessionFileTs,
        Dictionary<string, Dictionary<string, DateTime>> sessionFileEditTs,
        BrainCommitPlanOptions options)
    {
        var minFiles = Math.Max(1, options.MinFiles);

        var lastEditor = new Dictionary<string, (string SessionId, DateTime Ts)>(StringComparer.Ordinal);
        foreach (var (sessionId, editMap) in sessionFileEditTs)
        {
            foreach (var (file, ts) in editMap)
            {
                if (!lastEditor.TryGetValue(file, out var cur) || ts > cur.Ts)
                    lastEditor[file] = (sessionId, ts);
            }
        }

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

        var assigned = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<BrainCommitGroup>();
        int nextGroupId = 1;

        foreach (var sess in qualified)
        {
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
                groupFiles = sess.Files.OrderBy(f => f, StringComparer.Ordinal).ToList();
                shared.Clear();
            }
            else
            {
                if (unassigned.Count < minFiles) continue;
                groupFiles = unassigned.OrderBy(f => f, StringComparer.Ordinal).ToList();
            }

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

        return (groups, assigned);
    }

    private static void AttachContributingSessions(
        List<BrainCommitGroup> groups,
        Dictionary<string, Dictionary<string, DateTime>> sessionFileTs)
    {
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
    }

    // ── Scanner-mode prompt extraction (raw JSONL) ─────────────────────────────

    private static async Task<List<BrainCommitGroupPrompt>> ExtractPromptsFromJsonlAsync(
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
