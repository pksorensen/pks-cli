using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

/// Shows the cowork authoring prompt and (optionally) opens the local profile
/// in $EDITOR. The intended flow is:
///   1. Copy the prompt → paste into a filesystem-less Claude (claude.ai etc.)
///      that has access to the user's writing.
///   2. That session emits a JSON bundle.
///   3. User saves the reply to a file.
///   4. `pks writing profile ingest &lt;path&gt;` writes it into ~/.pks-cli/writing/.
public class WritingProfileAuthorCommand : AsyncCommand<WritingSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingProfileAuthorCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingSettings settings)
    {
        await _store.EnsureGlobalLayoutAsync();

        var promptPath = _paths.GlobalAuthoringPromptPath;
        var profilePath = _paths.GlobalProfilePath;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold magenta]pks writing profile author[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Two ways to author your profile:");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]A.[/] [cyan]Cowork flow (recommended)[/] — give a filesystem-less Claude session");
        AnsiConsole.MarkupLine("   (claude.ai with your writing attached, or any Claude that knows you) the");
        AnsiConsole.MarkupLine("   authoring prompt below. It emits a JSON bundle you ingest with:");
        AnsiConsole.MarkupLine($"     [bold]pks writing profile ingest <path-to-bundle>[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"   Prompt is also saved at: [cyan]{promptPath}[/]");
        AnsiConsole.MarkupLine($"   Print it any time with:  [bold]pks writing profile prompt[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]B.[/] [cyan]Local edit[/] — open profile.md in $EDITOR and write it yourself.");
        AnsiConsole.WriteLine();

        var choice = new SelectionPrompt<string>()
            .Title("What now?")
            .AddChoices(
                "Print the cowork prompt to terminal (so I can copy it)",
                "Open profile.md in $EDITOR",
                "Quit");
        var picked = AnsiConsole.Prompt(choice);

        if (picked.StartsWith("Print"))
        {
            var prompt = await System.IO.File.ReadAllTextAsync(promptPath);
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(Markup.Escape(prompt))
                .Header($"[bold]{promptPath}[/]")
                .Border(BoxBorder.Rounded));
            return 0;
        }

        if (picked.StartsWith("Open"))
        {
            var editor = ResolveEditor();
            if (editor is null)
            {
                AnsiConsole.MarkupLine("[yellow]![/] No editor found. Open it manually: [cyan]" + profilePath + "[/]");
                return 0;
            }
            try
            {
                var psi = new ProcessStartInfo(editor, profilePath) { UseShellExecute = false };
                var p = Process.Start(psi);
                if (p is not null) await p.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
            AnsiConsole.MarkupLine("[green]✓[/] Done. Run [bold]pks writing profile show[/] to view.");
        }
        return 0;
    }

    private static string? ResolveEditor()
    {
        var fromEnv = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        foreach (var candidate in new[] { "code", "nano", "vim", "vi" })
            if (IsOnPath(candidate)) return candidate;
        return null;
    }

    private static bool IsOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { if (System.IO.File.Exists(System.IO.Path.Combine(dir, name))) return true; }
            catch { }
        }
        return false;
    }
}
