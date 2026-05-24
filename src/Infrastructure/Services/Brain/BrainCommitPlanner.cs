using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IBrainSessionScanner _scanner;

    public BrainCommitPlanner(IBrainSessionScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<BrainCommitPlanResult> PlanAsync(BrainCommitPlanOptions options, CancellationToken ct = default)
    {
        var inputFiles = options.Files.Distinct(StringComparer.Ordinal).ToList();

        // session_id -> file -> latest_timestamp (of any edge from that session touching that file)
        var sessionFileTs = new Dictionary<string, Dictionary<string, DateTime>>(StringComparer.Ordinal);
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

                sessionScannedJsonls.Add(edge.SessionId);
            }
        }

        var minFiles = Math.Max(1, options.MinFiles);

        // Filter sessions whose set size in input set >= minFiles
        var qualified = sessionFileTs
            .Where(kv => kv.Value.Count >= minFiles)
            .Select(kv => new
            {
                SessionId = kv.Key,
                Files = kv.Value.Keys.ToHashSet(StringComparer.Ordinal),
                Latest = kv.Value.Values.Max(),
            })
            .OrderByDescending(s => s.Files.Count)
            .ThenByDescending(s => s.Latest)
            .ToList();

        var assigned = new Dictionary<string, int>(StringComparer.Ordinal); // file -> group_id
        var groups = new List<BrainCommitGroup>();
        int nextGroupId = 1;

        foreach (var sess in qualified)
        {
            var unassigned = sess.Files.Where(f => !assigned.ContainsKey(f)).ToList();
            var shared = sess.Files.Where(f => assigned.ContainsKey(f))
                .ToDictionary(f => f, f => assigned[f], StringComparer.Ordinal);

            bool isFirst = groups.Count == 0;
            List<string> groupFiles;
            if (isFirst)
            {
                // First group: claim ALL files of the primary session.
                groupFiles = sess.Files.OrderBy(f => f, StringComparer.Ordinal).ToList();
                shared.Clear();
            }
            else
            {
                if (unassigned.Count < minFiles) continue;
                groupFiles = unassigned.OrderBy(f => f, StringComparer.Ordinal).ToList();
            }

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
}
