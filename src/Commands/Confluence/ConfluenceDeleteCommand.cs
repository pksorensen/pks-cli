using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Confluence;

/// <summary>Stage a page for deletion. The actual delete happens when the user runs commit.</summary>
[Description("Stage a Confluence page for deletion (applied on commit)")]
public class ConfluenceDeleteCommand : Command<ConfluenceDeleteCommand.Settings>
{
    private readonly IConfluenceService _confluenceService;
    private readonly IAnsiConsole _console;

    public ConfluenceDeleteCommand(IConfluenceService confluenceService, IAnsiConsole console)
    {
        _confluenceService = confluenceService;
        _console = console;
    }

    public class Settings : ConfluenceSettings
    {
        [CommandArgument(0, "<page-id>")]
        [Description("Confluence page ID to delete")]
        public string PageId { get; set; } = string.Empty;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var config = await _confluenceService.LoadWorkspaceConfigAsync(Directory.GetCurrentDirectory());
        if (config == null)
        {
            _console.MarkupLine("[red]No Confluence workspace found. Run [bold]pks confluence init[/] first.[/]");
            return 1;
        }

        var workingDir = config.WorkDir;

        // Fetch page info to confirm what we're staging for deletion
        ConfluencePage? page = null;
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cyan]Fetching page info...[/]", async _ =>
            {
                page = await _confluenceService.GetPageByIdAsync(settings.PageId, expandBody: false);
            });

        if (page == null)
        {
            _console.MarkupLine($"[red]Page not found: {settings.PageId}[/]");
            return 1;
        }

        // Write a .delete marker file in _pending/
        var pendingDir = Path.Combine(workingDir, "_pending");
        Directory.CreateDirectory(pendingDir);

        var deleteFile = Path.Combine(pendingDir, $"{settings.PageId}.delete");
        await File.WriteAllTextAsync(deleteFile,
            $"title: {page.Title}\nid: {page.Id}\nspace: {page.Space?.Key}\n");

        // Git commit
        var gitDir = Path.Combine(workingDir, ".git");
        var psi = new ProcessStartInfo("git", $"add \"{Path.Combine("_pending", $"{settings.PageId}.delete")}\"")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_DIR"] = gitDir;
        psi.Environment["GIT_WORK_TREE"] = workingDir;
        using (var p = Process.Start(psi)) { p?.WaitForExit(10_000); }

        var commitPsi = new ProcessStartInfo("git", $"commit -m \"delete: staged {page.Title} for deletion\"")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        commitPsi.Environment["GIT_DIR"] = gitDir;
        commitPsi.Environment["GIT_WORK_TREE"] = workingDir;
        using (var p = Process.Start(commitPsi)) { p?.WaitForExit(10_000); }

        _console.MarkupLine($"[yellow]Staged for deletion:[/] {Markup.Escape(page.Title)} (id: {page.Id})");
        _console.MarkupLine("[dim]Run [bold]pks confluence commit[/] to apply the deletion.[/]");

        return 0;
    }
}
