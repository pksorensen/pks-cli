using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Commands.Brain;

public sealed class BrainSourcesSettings : BrainSettings
{
    [CommandOption("--source <KIND>")]
    [Description("Only show one source: claude, codex or opencode.")]
    public string? Source { get; set; }

    [CommandOption("--project <FILTER>")]
    [Description("Only count sessions whose project slug contains this text.")]
    public string? Project { get; set; }

    [CommandOption("--verify")]
    [Description("Parse the newest session of each source and report the ASF event mix.")]
    public bool Verify { get; set; }

    [CommandOption("--docker")]
    [Description("Also mount docker volumes read-only and count the sessions inside them.")]
    public bool Docker { get; set; }
}

/// `pks brain sources` — what the brain can see on this machine.
///
/// Answers the two questions that matter before a backup exists: which of the
/// three tools are installed, and how far back their data actually goes. The
/// "oldest" column is the point of the whole exercise — opencode's spilled tool
/// outputs are deleted after 7 days, so a gap there is data already lost.
public sealed class BrainSourcesCommand : AsyncCommand<BrainSourcesSettings>
{
    private readonly IEnumerable<IAgentSessionSource> _sources;
    private readonly IBrainPathResolver _paths;
    private readonly IDockerSessionScanner _docker;
    private readonly IBrainRootRegistry _roots;

    public BrainSourcesCommand(
        IEnumerable<IAgentSessionSource> sources,
        IBrainPathResolver paths,
        IDockerSessionScanner docker,
        IBrainRootRegistry roots)
    {
        _sources = sources;
        _paths = paths;
        _docker = docker;
        _roots = roots;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSourcesSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain sources[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("Source");
        table.AddColumn(new TableColumn("Sessions").RightAligned());
        table.AddColumn(new TableColumn("Projects").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Oldest");
        table.AddColumn("Newest");
        table.AddColumn("Location");

        var selected = _sources
            .Where(s => settings.Source is null ||
                        string.Equals(s.Kind, settings.Source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();

        if (selected.Count == 0)
        {
            // Every run prints a `docker:` line, so `--source docker` is the first
            // thing someone tries. Say why it is not one rather than just refusing.
            if (string.Equals(settings.Source, "docker", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine(
                    "[yellow]docker is a location, not a source[/] — a session in a volume was still " +
                    "written by claude, codex or opencode. Use [bold]pks brain sources --docker[/].");

                return 1;
            }

            AnsiConsole.MarkupLine($"[red]Unknown source '{settings.Source}'.[/] Expected claude, codex or opencode.");

            return 1;
        }

        var found = new List<(IAgentSessionSource Source, List<DiscoveredAgentSession> Sessions)>();

        foreach (var source in selected)
        {
            if (!source.IsAvailable)
            {
                table.AddRow(
                    $"[grey]{source.Kind}[/]", "[grey]—[/]", "[grey]—[/]", "[grey]—[/]",
                    "[grey]—[/]", "[grey]—[/]", $"[grey]not installed[/]");

                continue;
            }

            List<DiscoveredAgentSession> sessions;
            try
            {
                sessions = source.Discover(settings.Project).ToList();
            }
            catch (Exception ex)
            {
                table.AddRow(
                    $"[yellow]{source.Kind}[/]", "[red]error[/]", "", "", "", "",
                    $"[red]{Markup.Escape(ex.Message)}[/]");

                continue;
            }

            found.Add((source, sessions));

            if (sessions.Count == 0)
            {
                table.AddRow(
                    source.Kind, "0", "0", "0 B", "[grey]—[/]", "[grey]—[/]",
                    $"[grey]{Markup.Escape(source.Location)}[/]");

                continue;
            }

            table.AddRow(
                $"[bold]{source.Kind}[/]",
                sessions.Count.ToString("N0"),
                sessions.Select(s => s.ProjectSlug).Distinct(StringComparer.Ordinal).Count().ToString("N0"),
                FormatBytes(sessions.Sum(s => s.Bytes)),
                sessions.Min(s => s.LastModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd"),
                sessions.Max(s => s.LastModifiedUtc).ToLocalTime().ToString("yyyy-MM-dd"),
                $"[grey]{Markup.Escape(source.Location)}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        WriteRescuedRoots(found);

        await WriteDockerReportAsync(settings);

        WriteSpillWarning();

        if (settings.Verify)
        {
            await VerifyAsync(found);
        }
        else if (found.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Run with [bold]--verify[/] to parse the newest session of each source.[/]");
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    /// Copies of somebody else's agent home that this machine has been told about
    /// — today, docker volumes pulled out by `brain docker backup`.
    ///
    /// The counts come from the same discovery pass as the table above, grouped by
    /// origin, so a root that contributes nothing shows a zero rather than being
    /// quietly omitted: an empty rescued root usually means the mount is gone, and
    /// that is worth seeing.
    private void WriteRescuedRoots(
        List<(IAgentSessionSource Source, List<DiscoveredAgentSession> Sessions)> found)
    {
        var registered = _roots.All();
        if (registered.Count == 0) return;

        var byOrigin = found
            .SelectMany(f => f.Sessions)
            .Where(s => s.Origin is not null)
            .GroupBy(s => s.Origin!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Sessions: g.Count(), Bytes: g.Sum(s => s.Bytes)), StringComparer.Ordinal);

        // A devcontainer mints one config volume per container and names it with
        // 83 characters, of which the first 26 are the same word repeated on
        // every row. Printing that in full wraps every line in an 80-column
        // terminal and buries the only part that differs. Both the shared parent
        // directory and the shared origin prefix are therefore printed once in the
        // header, and the rows carry just what distinguishes them.
        var parent = CommonParent(registered.Select(r => r.Path).ToList());
        var prefix = CommonPrefix(registered.Select(r => r.Origin).ToList());

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("Rescued root");
        table.AddColumn(new TableColumn("Sessions").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Added");
        table.AddColumn("Status");
        if (parent is null) table.AddColumn("Path");

        var totalSessions = 0;
        var totalBytes = 0L;

        foreach (var root in registered.OrderByDescending(r => byOrigin.GetValueOrDefault(r.Origin).Sessions)
                     .ThenBy(r => r.Origin, StringComparer.Ordinal))
        {
            var reachable = Directory.Exists(root.Path);
            var stats = byOrigin.GetValueOrDefault(root.Origin);
            totalSessions += stats.Sessions;
            totalBytes += stats.Bytes;

            string[] cells =
            [
                Markup.Escape(Elide(prefix is null ? root.Origin : root.Origin[prefix.Length..], 22)),
                reachable ? stats.Sessions.ToString("N0") : "[grey]—[/]",
                reachable ? FormatBytes(stats.Bytes) : "[grey]—[/]",
                root.AddedUtc.ToLocalTime().ToString("yyyy-MM-dd"),
                reachable
                    ? (stats.Sessions > 0 ? "[green]readable[/]" : "[grey]no sessions[/]")
                    : "[yellow]unreachable[/]",
            ];

            table.AddRow(parent is null
                ? [.. cells, $"[grey]{Markup.Escape(root.Path)}[/]"]
                : cells);
        }

        if (parent is not null)
            AnsiConsole.MarkupLine($"[grey]Rescued roots under[/] {Markup.Escape(parent)}");
        if (prefix is not null)
            AnsiConsole.MarkupLine($"[grey]Origins all begin[/] [cyan]{Markup.Escape(prefix)}[/]");

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[grey]{registered.Count:N0} root(s), {totalSessions:N0} session(s), {FormatBytes(totalBytes)} — " +
            "already counted in the table above. Their events carry [/][cyan]origin[/][grey], and a session " +
            "also known from a host path is read once, not twice.[/]");

        var missing = registered.Count(r => !Directory.Exists(r.Path));
        if (missing > 0)
        {
            // Host-injected mounts vanish on a container recreate. The registry
            // keeps the entry on purpose — remounting is what fixes this, not
            // re-registering.
            AnsiConsole.MarkupLine(
                $"[yellow]{missing:N0} root(s) unreachable right now[/] [grey]— skipped, not forgotten. " +
                "Restore the mount and they come back on their own.[/]");
        }

        AnsiConsole.WriteLine();
    }

    /// The directory every path sits directly inside, or null when they do not
    /// share one. Only used to shorten the display.
    private static string? CommonParent(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;

        var first = Path.GetDirectoryName(paths[0]);
        if (string.IsNullOrEmpty(first)) return null;

        return paths.All(p => string.Equals(Path.GetDirectoryName(p), first, StringComparison.Ordinal))
            ? first
            : null;
    }

    /// The leading run every origin shares, when it is long enough to be worth
    /// hoisting out of the rows. Returns null for a single root or a short prefix,
    /// where lifting it costs a header line and saves nothing.
    private static string? CommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count < 2) return null;

        var length = values[0].Length;
        foreach (var value in values.Skip(1))
        {
            length = Math.Min(length, value.Length);
            var i = 0;
            while (i < length && value[i] == values[0][i]) i++;
            length = i;
            if (length == 0) return null;
        }

        return length >= 8 ? values[0][..length] : null;
    }

    /// Keeps both ends of an identifier — the meaningful prefix and the tail that
    /// distinguishes near-identical names.
    private static string Elide(string value, int max)
    {
        if (value.Length <= max) return value;

        var head = (max - 1) * 2 / 3;

        return value[..head] + "…" + value[^(max - 1 - head)..];
    }

    /// Sessions that ran inside a container are invisible to every source above,
    /// because those only look at host paths. The volume *names* are cheap to read
    /// and are therefore always reported; opening the volumes means starting a
    /// container, so that stays behind `--docker`. Reporting-only by design: an
    /// unconditional prompt here would hang `brain daemon`, which runs this code
    /// path with no terminal attached.
    private async Task WriteDockerReportAsync(BrainSourcesSettings settings)
    {
        if (!await _docker.IsDockerAvailableAsync())
        {
            if (settings.Docker)
            {
                AnsiConsole.MarkupLine("[yellow]docker:[/] [grey]no daemon reachable — nothing to scan.[/]");
                AnsiConsole.WriteLine();
            }

            return;
        }

        var inventory = await _docker.InventoryAsync();
        if (inventory.Candidates.Count == 0)
        {
            if (settings.Docker)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]docker: {inventory.AllVolumes.Count:N0} volume(s), none named like an agent's config store.[/]");
                AnsiConsole.WriteLine();
            }

            return;
        }

        var dangling = new HashSet<string>(inventory.Dangling, StringComparer.Ordinal);

        if (!settings.Docker)
        {
            // A rescued volume is registered as a root under the origin
            // `docker:<volume>`, and its sessions are already in the table above.
            // Calling those "not scanned" one line later reads as a contradiction,
            // so only the volumes nobody has rescued yet are offered up.
            var rescued = new HashSet<string>(_roots.All().Select(r => r.Origin), StringComparer.Ordinal);
            var unrescued = inventory.Candidates.Where(v => !rescued.Contains($"docker:{v}")).ToList();

            if (unrescued.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]docker: all {inventory.Candidates.Count:N0} agent config volume(s) are already " +
                    "rescued and counted above.[/]");
            }
            else
            {
                var stillDangling = unrescued.Count(dangling.Contains);
                AnsiConsole.MarkupLine(
                    $"[yellow]docker:[/] {unrescued.Count:N0} volume(s) look like agent config stores " +
                    $"([bold]{stillDangling:N0}[/] of them dangling), [bold]not scanned[/]. " +
                    "Run [bold]pks brain sources --docker[/] to count what is inside.");
            }

            AnsiConsole.WriteLine();

            return;
        }

        IReadOnlyList<DockerSessionFile> files = [];
        await AnsiConsole.Status()
            .StartAsync(
                $"Mounting {inventory.Candidates.Count:N0} volume(s) read-only…",
                async _ => files = await _docker.ScanAsync(inventory.Candidates));

        // --source narrows this block too, so the flag means the same thing in both
        // halves of the output.
        if (settings.Source is not null)
        {
            files = files
                .Where(f => string.Equals(f.Tool, settings.Source, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (files.Count == 0)
        {
            var scope = settings.Source is null ? "" : $" for {settings.Source}";

            AnsiConsole.MarkupLine(
                $"[grey]docker: {inventory.Candidates.Count:N0} candidate volume(s), " +
                $"no session files inside{Markup.Escape(scope)}.[/]");
            AnsiConsole.WriteLine();

            return;
        }

        // Grouped by project rather than by volume. A devcontainer mints a fresh
        // config volume per container, so a per-volume table is twenty rows of one
        // session each — and its 83-character names wrap a terminal into ruin.
        var groups = DockerScan.ByProject(files);

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("Tool");
        table.AddColumn("Project (path inside container)");
        table.AddColumn(new TableColumn("Vols").RightAligned());
        table.AddColumn(new TableColumn("Sessions").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Oldest");
        table.AddColumn("Newest");

        foreach (var group in groups)
        {
            table.AddRow(
                group.Tool,
                $"[bold]{Markup.Escape(group.ProjectDir)}[/]",
                group.Volumes.ToString("N0"),
                group.Sessions.ToString("N0"),
                FormatBytes(group.Bytes),
                group.Oldest.ToLocalTime().ToString("yyyy-MM-dd"),
                group.Newest.ToLocalTime().ToString("yyyy-MM-dd"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var volumes = DockerScan.Volumes(files);

        AnsiConsole.MarkupLine(
            $"[yellow]docker:[/] {files.Count:N0} session(s) across {volumes.Count:N0} volume(s), " +
            $"{FormatBytes(files.Sum(f => f.Bytes))}. " +
            $"[bold]{volumes.Count(dangling.Contains):N0}[/] of those volumes are dangling — " +
            "their containers are gone, so nothing but this scan can still see the data.");
        AnsiConsole.MarkupLine(
            "[grey]The project column is the path as it existed inside the container — kept as written, " +
            "so it hashes to a different handle than the host checkout of the same repo. Run [/]" +
            "[bold]pks brain docker backup[/][grey] to copy these out and register them; ingest reads " +
            "volumes only once they are registered.[/]");
        AnsiConsole.WriteLine();
    }

    /// opencode deletes spilled tool outputs after 7 days. Counting what is left in
    /// the window is the honest way to say how urgent the daily job is.
    private void WriteSpillWarning()
    {
        var root = _paths.OpenCodeToolOutputRoot;
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow.AddDays(-7);
        var files = Directory.EnumerateFiles(root, "tool_*").ToList();
        var expiring = files.Count(f => File.GetLastWriteTimeUtc(f) < cutoff.AddDays(1));

        AnsiConsole.MarkupLine(
            $"[yellow]opencode spill:[/] {files.Count:N0} file(s) in {Markup.Escape(root)}, " +
            $"{expiring:N0} within 24h of the hardcoded 7-day cleanup.");
        AnsiConsole.WriteLine();
    }

    private static async Task VerifyAsync(
        List<(IAgentSessionSource Source, List<DiscoveredAgentSession> Sessions)> found)
    {
        var masker = SecretMasker.ForProject(Directory.GetCurrentDirectory());

        foreach (var (source, sessions) in found)
        {
            var newest = sessions.OrderByDescending(s => s.LastModifiedUtc).FirstOrDefault();
            if (newest is null) continue;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var events = 0;
            string? failure = null;

            try
            {
                await foreach (var e in source.ReadAsync(newest, masker))
                {
                    events++;
                    counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
                }
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            AnsiConsole.MarkupLine(
                $"[bold]{source.Kind}[/] [grey]{Markup.Escape(newest.ProjectSlug)}[/] " +
                $"[grey]({Markup.Escape(Path.GetFileName(newest.SourcePath))})[/]");

            if (failure is not null)
            {
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(failure)}[/]");
                AnsiConsole.WriteLine();

                continue;
            }

            AnsiConsole.MarkupLine(
                $"  {events:N0} ASF events — " +
                string.Join(", ", counts
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"[cyan]{kv.Key}[/] {kv.Value:N0}")));
            AnsiConsole.WriteLine();
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
