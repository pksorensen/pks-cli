using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Writing;

public class WritingSkillInstallSettings : WritingSettings
{
    [CommandOption("--force")]
    [Description("Overwrite existing skill file.")]
    public bool Force { get; set; }

    [CommandOption("--target")]
    [Description("Override the install directory (default ~/.claude/skills/pks-writing-score/).")]
    public string? Target { get; set; }
}

/// Installs the embedded pks-writing-score skill so an agent (Claude Code, etc.)
/// discovers the agent-driven prompt→accept flow. Mirrors [[BrainSkillInitCommand]].
public class WritingSkillInstallCommand : AsyncCommand<WritingSkillInstallSettings>
{
    private const string SkillName = "pks-writing-score";
    private const string ResourceName = "pks-writing-score.SKILL.md";

    public override async Task<int> ExecuteAsync(CommandContext context, WritingSkillInstallSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var targetDir = settings.Target ?? System.IO.Path.Combine(home, ".claude", "skills", SkillName);
        var targetPath = System.IO.Path.Combine(targetDir, "SKILL.md");

        if (System.IO.File.Exists(targetPath) && !settings.Force)
        {
            AnsiConsole.MarkupLine($"[yellow]Already exists:[/] [cyan]{targetPath}[/]");
            AnsiConsole.MarkupLine("Use [bold]--force[/] to overwrite.");
            return 1;
        }

        var asm = typeof(WritingSkillInstallCommand).Assembly;
        await using var stream = asm.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            AnsiConsole.MarkupLine($"[red]Embedded skill missing:[/] {ResourceName} — pks-cli build issue.");
            return 1;
        }
        using var reader = new StreamReader(stream);
        var body = await reader.ReadToEndAsync();

        System.IO.Directory.CreateDirectory(targetDir);
        await System.IO.File.WriteAllTextAsync(targetPath, body);

        AnsiConsole.MarkupLine($"[green]✓[/] Wrote [cyan]{targetPath}[/]");
        AnsiConsole.MarkupLine($"[grey]An agent (e.g. Claude Code) will pick it up automatically and drive the prompt → LLM → accept loop.[/]");
        return 0;
    }
}
