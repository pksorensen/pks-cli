using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

// ── list ────────────────────────────────────────────────────────────────────

public class BrainSkillListSettings : BrainSettings { }

public class BrainSkillListCommand : AsyncCommand<BrainSkillListSettings>
{
    private readonly IBrainSkillCatalog _catalog;
    private readonly IBrainSkillReader _reader;

    public BrainSkillListCommand(IBrainSkillCatalog catalog, IBrainSkillReader reader)
    {
        _catalog = catalog;
        _reader = reader;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSkillListSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain skill list[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        // Embedded body is the default — diff against user copies to flag customizations.
        var embeddedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedSources = new Dictionary<string, BrainSkillSource>(StringComparer.Ordinal);
        foreach (var s in _catalog.AllSkills)
        {
            // Force the embedded fallback by passing an obviously-non-existing override
            // path is not safe (it'd throw), so we read from the actual hierarchy and
            // then separately load the embedded body via the reader without override.
            // The simplest way: read with override=null (resolved hierarchy), and read
            // the embedded copy explicitly via a synthetic forced path. Since the
            // reader doesn't expose embedded directly, we rely on resolved.Source to
            // tell us whether the current copy IS the embedded one.
            resolvedSources[s.Name] = await _reader.ReadAsync(s.Name, null);
        }

        // For each user-installed override, also load the embedded copy so we can diff.
        foreach (var s in _catalog.AllSkills)
        {
            var resolved = resolvedSources[s.Name];
            if (resolved.Source.StartsWith("embedded", StringComparison.Ordinal))
            {
                embeddedHashes[s.Name] = BrainHash.Short(resolved.Body);
                continue;
            }
            // Temporarily move the user copy aside is unsafe — easier: assume reader
            // returns embedded when the user paths don't exist. Hash the user body.
            embeddedHashes[s.Name] = ""; // unknown; we'll just show resolved hash.
        }

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("[grey]skill[/]");
        table.AddColumn("[grey]command[/]");
        table.AddColumn("[grey]source[/]");
        table.AddColumn("[grey]hash[/]");
        table.AddColumn("[grey]status[/]");

        foreach (var s in _catalog.AllSkills)
        {
            var src = resolvedSources[s.Name];
            var hash = BrainHash.Short(src.Body);
            string source;
            string status;
            if (src.Source.StartsWith("embedded", StringComparison.Ordinal))
            {
                source = "[grey]embedded[/]";
                status = "[grey](default)[/]";
            }
            else
            {
                // Source is a file path — flag it as a user override.
                source = $"[cyan]{Truncate(src.Source, 60)}[/]";
                status = "[yellow]customized[/]";
            }
            table.AddRow(s.Name, s.Command, source, $"[dim]{hash}[/]", status);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Customize a skill with[/] [bold]pks brain skill init <name>[/] [grey]— copies the embedded default to[/]");
        AnsiConsole.MarkupLine("[grey]Use --agents to write .agents/skills/<name>/SKILL.md for a project-shared Claude + Codex skill.[/]");
        return 0;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : "…" + s[^(n - 1)..];
}

// ── init ────────────────────────────────────────────────────────────────────

public class BrainSkillInitSettings : BrainSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Skill name (e.g. brain-extract, brain-synth-cluster, brain-synth-habits, brain-wiki-page).")]
    public string Name { get; set; } = string.Empty;

    [CommandOption("--target")]
    [Description("Destination dir. Default: ~/.claude/skills/<name>/")]
    public string? Target { get; set; }

    [CommandOption("--agents")]
    [Description("Install into the current repo's .agents/skills/<name>/ so Claude and Codex can share it.")]
    public bool Agents { get; set; }

    [CommandOption("--force")]
    [Description("Overwrite an existing file instead of refusing.")]
    public bool Force { get; set; }
}

public class BrainSkillInitCommand : AsyncCommand<BrainSkillInitSettings>
{
    private readonly IBrainSkillCatalog _catalog;
    private readonly IBrainSkillReader _reader;

    public BrainSkillInitCommand(IBrainSkillCatalog catalog, IBrainSkillReader reader)
    {
        _catalog = catalog;
        _reader = reader;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSkillInitSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks brain skill init[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var entry = _catalog.Get(settings.Name);
        if (entry is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown skill:[/] {settings.Name}");
            AnsiConsole.MarkupLine("Run [bold]pks brain skill list[/] to see available skills.");
            return 1;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (settings.Target is not null && settings.Agents)
        {
            AnsiConsole.MarkupLine("[red]Use either --target or --agents, not both.[/]");
            return 1;
        }
        var targetDir = settings.Target ?? (settings.Agents
            ? Path.Combine(FindRepositoryRoot(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory(),
                ".agents", "skills", entry.Name)
            : Path.Combine(home, ".claude", "skills", entry.Name));
        var targetPath = Path.Combine(targetDir, "SKILL.md");

        if (File.Exists(targetPath) && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[yellow]Already exists:[/] [cyan]{targetPath}[/]");
            AnsiConsole.MarkupLine("Use [bold]--force[/] to overwrite, or edit the existing file directly.");
            return 1;
        }

        // Read the embedded body — careful: we want the EMBEDDED copy specifically,
        // not whatever the lookup hierarchy resolves to. The reader's only override
        // hook is a file path, so we look it up via the assembly directly.
        var embedded = await ReadEmbeddedAsync(entry.Name);
        if (embedded is null)
        {
            AnsiConsole.MarkupLine($"[red]Embedded default missing for[/] [bold]{entry.Name}[/] — pks-cli build issue.");
            return 1;
        }

        Directory.CreateDirectory(targetDir);
        await File.WriteAllTextAsync(targetPath, embedded);
        AnsiConsole.MarkupLine($"[green]✓[/] Wrote [cyan]{targetPath}[/]");
        AnsiConsole.MarkupLine($"[grey]Edit it. Next [/][bold]{entry.Command}[/][grey] picks up the changes automatically.[/]");
        AnsiConsole.MarkupLine($"[grey]Check status with[/] [bold]pks brain skill list[/]");
        return 0;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static async Task<string?> ReadEmbeddedAsync(string skillName)
    {
        var asm = typeof(BrainSkillInitCommand).Assembly;
        var resourceName = skillName + ".SKILL.md";
        await using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

// ── show ────────────────────────────────────────────────────────────────────

public class BrainSkillShowSettings : BrainSettings
{
    [CommandArgument(0, "<NAME>")]
    [Description("Skill name to print.")]
    public string Name { get; set; } = string.Empty;
}

public class BrainSkillShowCommand : AsyncCommand<BrainSkillShowSettings>
{
    private readonly IBrainSkillCatalog _catalog;
    private readonly IBrainSkillReader _reader;

    public BrainSkillShowCommand(IBrainSkillCatalog catalog, IBrainSkillReader reader)
    {
        _catalog = catalog;
        _reader = reader;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSkillShowSettings settings)
    {
        var entry = _catalog.Get(settings.Name);
        if (entry is null)
        {
            AnsiConsole.MarkupLine($"[red]Unknown skill:[/] {settings.Name}");
            return 1;
        }
        var src = await _reader.ReadAsync(entry.Name, null);
        AnsiConsole.WriteLine($"# source: {src.Source}");
        AnsiConsole.WriteLine($"# hash:   {BrainHash.Short(src.Body)}");
        AnsiConsole.WriteLine();
        Console.Out.Write(src.Body);
        return 0;
    }
}
