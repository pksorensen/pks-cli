using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainStatusCommand : AsyncCommand<BrainSettings>
{
    private readonly IBrainPathResolver _paths;
    private readonly IBrainIndexStore _store;
    private readonly IBrainSkillReader _skillReader;

    public BrainStatusCommand(IBrainPathResolver paths, IBrainIndexStore store, IBrainSkillReader skillReader)
    {
        _paths = paths;
        _store = store;
        _skillReader = skillReader;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain status[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        if (!File.Exists(_paths.GlobalIndexPath))
        {
            AnsiConsole.MarkupLine("[yellow]Brain not initialized yet.[/]");
            AnsiConsole.MarkupLine("Run [bold]pks brain init[/] first.");
            return 0;
        }

        var index = await _store.LoadIndexAsync();

        AnsiConsole.MarkupLine("[bold]Global raw layer[/]");
        var t = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
        t.AddColumn(""); t.AddColumn("");
        t.AddRow("[grey]Schema version[/]",   index.SchemaVersion.ToString());
        t.AddRow("[grey]Global root[/]",      $"[cyan]{_paths.GlobalRoot}[/]");
        t.AddRow("[grey]Created[/]",          index.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        t.AddRow("[grey]Updated[/]",          index.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        t.AddRow("[grey]Projects[/]",         index.ProjectCount.ToString());
        t.AddRow("[grey]Sessions[/]",         index.SessionCount.ToString());
        t.AddRow("[grey]Prompts[/]",          index.PromptCount.ToString("N0"));
        t.AddRow("[grey]Tool calls[/]",       index.ToolCallCount.ToString("N0"));
        t.AddRow("[grey]File ops[/]",         index.FileOpCount.ToString("N0"));
        t.AddRow("[grey]Errors[/]",           index.ErrorCount.ToString("N0"));
        if (index.LastIngestAt.HasValue)
        {
            t.AddRow("[grey]Last ingest[/]",  index.LastIngestAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            if (index.LastIngestDuration.HasValue)
                t.AddRow("[grey]Last duration[/]", index.LastIngestDuration.Value.ToString(@"hh\:mm\:ss"));
        }
        else
        {
            t.AddRow("[grey]Last ingest[/]",  "[yellow](never — run [bold]pks brain ingest[/])[/]");
        }
        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();

        // Per-project extract stats — only present when run from inside a project.
        var projectRoot = _paths.ResolveProjectRoot(Directory.GetCurrentDirectory());
        if (projectRoot is not null)
        {
            var extractsDir = Path.Combine(projectRoot, "extracts");
            var stats = await LoadExtractStatsAsync(extractsDir);
            var stale = await ComputeStaleAsync(extractsDir);
            AnsiConsole.MarkupLine($"[bold]Per-project extracts[/] [grey]({projectRoot})[/]");
            var et = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
            et.AddColumn(""); et.AddColumn("");
            if (stats.Count == 0)
            {
                et.AddRow("[grey]Status[/]", "[yellow](no extracts yet — run [bold]pks brain extract[/])[/]");
            }
            else
            {
                et.AddRow("[grey]Extracts[/]",          stats.Count.ToString("N0"));
                et.AddRow("[grey]Total cost[/]",        $"[green]${stats.TotalCost:0.####}[/]");
                et.AddRow("[grey]Avg cost / extract[/]",$"${stats.TotalCost / Math.Max(1, stats.Count):0.####}");
                et.AddRow("[grey]Total input tokens[/]",FormatTokens(stats.TotalInput));
                et.AddRow("[grey]Total output tokens[/]",FormatTokens(stats.TotalOutput));
                if (stats.ModelCounts.Count > 0)
                    et.AddRow("[grey]Models[/]",        string.Join(", ", stats.ModelCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}")));
                if (stats.LatestAt.HasValue)
                    et.AddRow("[grey]Latest extract[/]",stats.LatestAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                if (stats.SkillHashes.Count > 1)
                    et.AddRow("[grey]Skill versions[/]", $"[yellow]{stats.SkillHashes.Count} different skill bodies[/] [grey](`pks brain extract --force` to re-extract with current skill)[/]");
            }
            AnsiConsole.Write(et);
            AnsiConsole.WriteLine();

            // Stale section — only show when something is stale.
            if (stale.SessionStale > 0 || stale.SkillStale > 0 || stale.NoExtract > 0)
            {
                AnsiConsole.MarkupLine("[bold]Refresh suggestions[/]");
                var st = new Table().Border(TableBorder.MinimalHeavyHead).HideHeaders();
                st.AddColumn(""); st.AddColumn("");
                if (stale.NoExtract > 0)
                {
                    st.AddRow("[grey]Sessions without an extract[/]",
                        $"[yellow]{stale.NoExtract:N0}[/] [grey]→ [/][bold]pks brain extract[/]");
                }
                if (stale.SessionStale > 0)
                {
                    st.AddRow("[grey]Extracts older than the source session[/]",
                        $"[yellow]{stale.SessionStale:N0}[/] [grey]→ [/][bold]pks brain extract[/] [grey](mtime cursor picks them up automatically)[/]");
                }
                if (stale.SkillStale > 0)
                {
                    st.AddRow("[grey]Extracts from a different skill version[/]",
                        $"[yellow]{stale.SkillStale:N0}[/] [grey]→ [/][bold]pks brain extract --force[/]");
                }
                AnsiConsole.Write(st);
                AnsiConsole.WriteLine();
            }
        }
        return 0;
    }

    private async Task<StaleCounts> ComputeStaleAsync(string extractsDir)
    {
        var result = new StaleCounts();
        var cwd = Directory.GetCurrentDirectory();
        var slug = _paths.EncodeSlug(_paths.Normalize(cwd) ?? cwd);
        var sessionsDir = Path.Combine(_paths.GlobalProjectDir(slug), "sessions");
        if (!Directory.Exists(sessionsDir)) return result;

        string? currentSkillHash = null;
        try
        {
            var skill = await _skillReader.ReadAsync("brain-extract", null);
            currentSkillHash = BrainHash.Short(skill.Body);
        }
        catch { /* skill not resolvable — skip skill staleness */ }

        var hasExtracts = Directory.Exists(extractsDir);
        foreach (var sessionJsonPath in Directory.EnumerateFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            var sessionId = Path.GetFileNameWithoutExtension(sessionJsonPath);
            var sessionMtime = File.GetLastWriteTimeUtc(sessionJsonPath);
            if (!hasExtracts)
            {
                result.NoExtract++;
                continue;
            }
            var extractMd = Path.Combine(extractsDir, sessionId + ".md");
            if (!File.Exists(extractMd))
            {
                result.NoExtract++;
                continue;
            }
            if (File.GetLastWriteTimeUtc(extractMd) < sessionMtime)
            {
                result.SessionStale++;
                continue;
            }
            // Skill mismatch — compare sidecar's recorded skillHash to current skill.
            if (currentSkillHash is null) continue;
            var sidecar = Path.Combine(extractsDir, sessionId + ".meta.json");
            if (!File.Exists(sidecar)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<ExtractMetadata>(
                    await File.ReadAllTextAsync(sidecar), DefaultJson);
                if (meta is null) continue;
                if (!string.Equals(meta.SkillHash, currentSkillHash, StringComparison.Ordinal))
                    result.SkillStale++;
            }
            catch (JsonException) { /* ignore corrupt sidecar */ }
        }
        return result;
    }

    private sealed class StaleCounts
    {
        public int NoExtract;
        public int SessionStale;
        public int SkillStale;
    }

    private static async Task<ExtractStats> LoadExtractStatsAsync(string extractsDir)
    {
        var stats = new ExtractStats();
        if (!Directory.Exists(extractsDir)) return stats;
        foreach (var path in Directory.EnumerateFiles(extractsDir, "*.meta.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var meta = JsonSerializer.Deserialize<ExtractMetadata>(
                    await File.ReadAllTextAsync(path), DefaultJson);
                if (meta is null) continue;
                stats.Count++;
                stats.TotalCost += meta.CostUsd;
                stats.TotalInput += meta.InputTokens + meta.CacheReadInputTokens + meta.CacheCreationInputTokens;
                stats.TotalOutput += meta.OutputTokens;
                if (meta.Model is { Length: > 0 } m)
                    stats.ModelCounts[m] = stats.ModelCounts.GetValueOrDefault(m) + 1;
                if (meta.SkillHash is { Length: > 0 } h)
                    stats.SkillHashes.Add(h);
                if (stats.LatestAt is null || meta.ExtractedAtUtc > stats.LatestAt) stats.LatestAt = meta.ExtractedAtUtc;
            }
            catch (JsonException) { /* skip corrupt sidecar */ }
        }
        return stats;
    }

    private static readonly JsonSerializerOptions DefaultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed class ExtractStats
    {
        public int Count;
        public double TotalCost;
        public long TotalInput;
        public long TotalOutput;
        public DateTime? LatestAt;
        public Dictionary<string, int> ModelCounts = new(StringComparer.Ordinal);
        public HashSet<string> SkillHashes = new(StringComparer.Ordinal);
    }

    private static string FormatTokens(long n) =>
        n < 10_000   ? n.ToString("N0")
        : n < 1_000_000 ? $"{n / 1000.0:0.#}K"
        :                 $"{n / 1_000_000.0:0.##}M";
}
