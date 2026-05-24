using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Implements FT-12 (Brain) scan command — Session→ToolCall→File edge discovery.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public sealed class BrainSessionScanner : IBrainSessionScanner
{
    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal)
    {
        "Edit", "Write", "MultiEdit", "Read", "NotebookEdit",
    };

    public async Task<BrainScanResult> ScanAsync(BrainScanOptions options, CancellationToken ct = default)
    {
        var result = new BrainScanResult();
        if (!Directory.Exists(options.ProjectsDir)) return result;

        var candidates = await ResolveCandidateJsonlsAsync(options, ct);

        var edges = new List<BrainScanEdge>();
        var sessionsWithMatches = new HashSet<string>(StringComparer.Ordinal);
        int scannedJsonls = 0;

        foreach (var jsonl in candidates)
        {
            ct.ThrowIfCancellationRequested();
            scannedJsonls++;

            var fileInfo = new FileInfo(jsonl);
            if (!fileInfo.Exists) continue;

            var sessionId = Path.GetFileNameWithoutExtension(jsonl);
            DateTime? firstTs = null;
            var fileEdges = new List<BrainScanEdge>();

            await using var stream = new FileStream(jsonl, FileMode.Open, FileAccess.Read, FileShare.Read,
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

                var ts = TryDate(root, "timestamp");
                if (ts is { } t && (firstTs is null || t < firstTs)) firstTs = t;

                if (!TryString(root, "type", out var type) || type != "assistant") continue;
                if (!TryGetMessage(root, out var msg)) continue;
                if (!msg.TryGetProperty("content", out var content)) continue;
                if (content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (!TryString(block, "type", out var bt) || bt != "tool_use") continue;
                    if (!TryString(block, "name", out var toolName)) continue;
                    if (!block.TryGetProperty("input", out var input)) continue;
                    if (!TryString(block, "id", out var toolUseId)) toolUseId = string.Empty;

                    var ts2 = ts ?? fileInfo.LastWriteTimeUtc;

                    if (FileTools.Contains(toolName))
                    {
                        if (!TryString(input, "file_path", out var filePath)) continue;
                        if (!MatchesTarget(filePath, options)) continue;

                        fileEdges.Add(new BrainScanEdge
                        {
                            SessionId = sessionId,
                            JsonlPath = jsonl,
                            TimestampUtc = ts2,
                            ToolUseId = toolUseId,
                            ToolName = toolName,
                            FilePath = filePath,
                            MatchKind = "tool_input_file_path",
                        });
                    }
                    else if (options.IncludeBash && toolName == "Bash")
                    {
                        if (!TryString(input, "command", out var command)) continue;
                        if (command.IndexOf(options.TargetPath, StringComparison.Ordinal) < 0) continue;

                        fileEdges.Add(new BrainScanEdge
                        {
                            SessionId = sessionId,
                            JsonlPath = jsonl,
                            TimestampUtc = ts2,
                            ToolUseId = toolUseId,
                            ToolName = toolName,
                            FilePath = options.TargetPath,
                            MatchKind = "bash_command_substring",
                        });
                    }
                }
            }

            if (options.SinceUtc is { } sinceUtc && firstTs is { } first && first < sinceUtc)
                continue;

            if (fileEdges.Count > 0)
            {
                edges.AddRange(fileEdges);
                sessionsWithMatches.Add(sessionId);
            }
        }

        return new BrainScanResult
        {
            ScannedJsonls = scannedJsonls,
            MatchedSessions = sessionsWithMatches.Count,
            Edges = edges,
        };
    }

    private static bool MatchesTarget(string filePath, BrainScanOptions options)
    {
        if (options.TargetIsDirectory)
        {
            var target = options.TargetPath;
            if (!target.EndsWith('/') && !target.EndsWith('\\')) target += "/";
            var normalized = filePath.Replace('\\', '/');
            var normalizedTarget = target.Replace('\\', '/');
            return normalized.StartsWith(normalizedTarget, StringComparison.Ordinal);
        }
        return string.Equals(filePath, options.TargetPath, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<string>> ResolveCandidateJsonlsAsync(BrainScanOptions options, CancellationToken ct)
    {
        var rgResult = await TryRipgrepPrefilterAsync(options.TargetPath, options.ProjectsDir, ct);
        if (rgResult is not null) return rgResult;

        var all = new List<string>();
        foreach (var sub in Directory.EnumerateDirectories(options.ProjectsDir))
        {
            foreach (var file in Directory.EnumerateFiles(sub, "*.jsonl"))
                all.Add(file);
        }
        return all;
    }

    private static async Task<IReadOnlyList<string>?> TryRipgrepPrefilterAsync(string needle, string root, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("rg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--files-with-matches");
            psi.ArgumentList.Add("--fixed-strings");
            psi.ArgumentList.Add("--glob");
            psi.ArgumentList.Add("*.jsonl");
            psi.ArgumentList.Add(needle);
            psi.ArgumentList.Add(root);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            // rg exits 1 when no matches found; both 0 and 1 are valid outcomes here.
            if (proc.ExitCode != 0 && proc.ExitCode != 1) return null;

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.ToList();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryString(JsonElement el, string name, out string value)
    {
        value = string.Empty;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind != JsonValueKind.String) return false;
        value = p.GetString() ?? string.Empty;
        return value.Length > 0;
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

    private static bool TryGetMessage(JsonElement root, out JsonElement msg)
    {
        msg = default;
        if (!root.TryGetProperty("message", out var m)) return false;
        if (m.ValueKind != JsonValueKind.Object) return false;
        msg = m;
        return true;
    }
}
