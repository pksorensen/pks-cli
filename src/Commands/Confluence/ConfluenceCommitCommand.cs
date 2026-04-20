using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Confluence;

/// <summary>Interactive commit of edited markdown files back to Confluence.</summary>
[Description("Push local edits back to Confluence")]
public class ConfluenceCommitCommand : Command<ConfluenceCommitCommand.Settings>
{
    private readonly IConfluenceService _confluenceService;
    private readonly IConfluenceMarkdownConverter _converter;
    private readonly IAnsiConsole _console;

    public ConfluenceCommitCommand(IConfluenceService confluenceService, IConfluenceMarkdownConverter converter, IAnsiConsole console)
    {
        _confluenceService = confluenceService;
        _converter = converter;
        _console = console;
    }

    public class Settings : ConfluenceSettings
    {
        [CommandOption("--all|-a")]
        [Description("Commit all pending files without interactive selection")]
        public bool All { get; set; }
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
        if (!Directory.Exists(workingDir))
        {
            _console.MarkupLine($"[red]Workspace directory not found: {Markup.Escape(workingDir)}[/]");
            return 1;
        }

        if (!await _confluenceService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated. Run [bold]pks jira init[/] first.[/]");
            return 1;
        }

        // Scan _pending/ for markdown files
        var pendingDir = Path.Combine(workingDir, "_pending");
        if (!Directory.Exists(pendingDir))
        {
            _console.MarkupLine("[yellow]No _pending directory found. Nothing to commit.[/]");
            return 0;
        }

        var pendingFiles = Directory.GetFiles(pendingDir, "*.md");
        var deleteFiles = Directory.GetFiles(pendingDir, "*.delete");

        if (pendingFiles.Length == 0 && deleteFiles.Length == 0)
        {
            _console.MarkupLine("[yellow]No pending files to commit.[/]");
            return 0;
        }

        // Parse frontmatter for each markdown file
        var candidates = new List<(string FilePath, string FileName, ConfluenceFrontmatter Frontmatter)>();
        foreach (var file in pendingFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            var fm = _converter.ParseFrontmatter(content);
            if (fm == null)
            {
                _console.MarkupLine($"[yellow]Skipping {Path.GetFileName(file)} — no valid frontmatter.[/]");
                continue;
            }
            candidates.Add((file, Path.GetFileName(file), fm));
        }

        // Parse .delete marker files
        var deleteCandidates = new List<(string FilePath, string FileName, string PageId, string Title)>();
        foreach (var file in deleteFiles)
        {
            var lines = await File.ReadAllLinesAsync(file);
            var title = lines.FirstOrDefault(l => l.StartsWith("title:"))?.Substring(6).Trim() ?? "Unknown";
            var pageId = lines.FirstOrDefault(l => l.StartsWith("id:"))?.Substring(3).Trim() ?? Path.GetFileNameWithoutExtension(file);
            deleteCandidates.Add((file, Path.GetFileName(file), pageId, title));
        }

        if (candidates.Count == 0 && deleteCandidates.Count == 0)
        {
            _console.MarkupLine("[yellow]No valid pending files to commit.[/]");
            return 0;
        }

        // Build unified display list
        var displayItems = candidates.Select(c =>
            c.Frontmatter.Id == "new"
                ? $"{c.Frontmatter.Title} ({c.FileName}) [green][[NEW]][/]"
                : $"{c.Frontmatter.Title} ({c.FileName}) [dim]v{c.Frontmatter.Version}[/]").ToList();

        var deleteDisplayItems = deleteCandidates.Select(d =>
            $"{d.Title} ({d.FileName}) [red][[DELETE]][/]").ToList();

        var allDisplayItems = displayItems.Concat(deleteDisplayItems).ToList();

        var selected = _console.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[cyan]Select changes to apply to Confluence:[/]")
                .PageSize(15)
                .InstructionsText("[dim](Space to toggle, Enter to confirm)[/]")
                .AddChoices(allDisplayItems));

        if (selected.Count == 0)
        {
            _console.MarkupLine("[yellow]No pages selected.[/]");
            return 0;
        }

        // Sort selected items: parents before children (pages with pending: parent_id go after their parent)
        // Build a map of slug -> candidate index for pending parent resolution
        var slugToIdx = new Dictionary<string, int>();
        for (var i = 0; i < candidates.Count; i++)
            slugToIdx[Path.GetFileNameWithoutExtension(candidates[i].FileName)] = i;

        // Track created page IDs for pending: parent resolution
        var slugToPageId = new Dictionary<string, string>();

        // Reorder selected: items whose parent_id is a real ID go first, pending: references go after their parent
        var orderedSelected = new List<string>();
        var pendingChildren = new List<string>();
        foreach (var item in selected)
        {
            var idx = displayItems.IndexOf(item);
            if (idx >= 0 && candidates[idx].Frontmatter.ParentId?.StartsWith("pending:") == true)
                pendingChildren.Add(item);
            else
                orderedSelected.Add(item);
        }
        orderedSelected.AddRange(pendingChildren);

        // Process each selected file
        var committedDir = Path.Combine(workingDir, "_committed");
        Directory.CreateDirectory(committedDir);

        var results = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Page")
            .AddColumn("Status");

        var successCount = 0;

        foreach (var displayItem in orderedSelected)
        {
            // Check if this is a delete action
            var deleteIdx = deleteDisplayItems.IndexOf(displayItem);
            if (deleteIdx >= 0)
            {
                var (delFilePath, delFileName, delPageId, delTitle) = deleteCandidates[deleteIdx];
                try
                {
                    bool deleted = false;
                    await _console.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync($"[cyan]Deleting {Markup.Escape(delTitle)}...[/]", async _ =>
                        {
                            deleted = await _confluenceService.DeletePageAsync(delPageId);
                        });

                    File.Delete(delFilePath);
                    RunGit(workingDir, $"add \"{Path.Combine("_pending", delFileName)}\"");
                    RunGit(workingDir, $"commit -m \"deleted: {EscapeGitMessage(delTitle)} removed from Confluence\"");
                    results.AddRow(Markup.Escape(delTitle), "[red]Deleted[/]");
                    successCount++;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    results.AddRow(Markup.Escape(delTitle), "[red]Forbidden — no delete permission[/]");
                    var discard = _console.Prompt(
                        new ConfirmationPrompt($"Remove {delFileName} from pending? (cannot be deleted via API)") { DefaultValue = true });
                    if (discard)
                    {
                        File.Delete(delFilePath);
                        RunGit(workingDir, $"add \"{Path.Combine("_pending", delFileName)}\"");
                        RunGit(workingDir, $"commit -m \"discarded: {EscapeGitMessage(delTitle)} delete request (no permission)\"");
                    }
                }
                catch (Exception ex)
                {
                    results.AddRow(Markup.Escape(delTitle), $"[red]Error: {Markup.Escape(ex.Message)}[/]");
                }
                continue;
            }

            var idx = displayItems.IndexOf(displayItem);
            var (filePath, fileName, frontmatter) = candidates[idx];

            try
            {
                var content = await File.ReadAllTextAsync(filePath);
                var body = _converter.ExtractBody(content);
                var storageHtml = _converter.MarkdownToStorage(body);

                ConfluencePage updatedPage = null!;
                var isNewPage = frontmatter.Id == "new";

                // Resolve pending: parent references to real page IDs
                var resolvedParentId = frontmatter.ParentId;
                if (resolvedParentId?.StartsWith("pending:") == true)
                {
                    var parentSlug = resolvedParentId.Substring(8);
                    if (slugToPageId.TryGetValue(parentSlug, out var realId))
                    {
                        resolvedParentId = realId;
                    }
                    else
                    {
                        results.AddRow(Markup.Escape(frontmatter.Title),
                            $"[red]Parent '{parentSlug}' not yet created — commit parent first[/]");
                        continue;
                    }
                }

                await _console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"[cyan]{(isNewPage ? "Creating" : "Pushing")} {Markup.Escape(frontmatter.Title)}...[/]", async _ =>
                    {
                        if (isNewPage)
                        {
                            updatedPage = await _confluenceService.CreatePageAsync(
                                frontmatter.Space,
                                frontmatter.Title,
                                storageHtml,
                                resolvedParentId);
                        }
                        else
                        {
                            updatedPage = await _confluenceService.UpdatePageAsync(
                                frontmatter.Id,
                                frontmatter.Title,
                                storageHtml,
                                frontmatter.Version);
                        }
                    });

                // Upload local image attachments referenced in the markdown
                var pageId = updatedPage.Id;
                var imageRefs = System.Text.RegularExpressions.Regex.Matches(body, @"!\[([^\]]*)\]\(([^)]+)\)");
                foreach (System.Text.RegularExpressions.Match imgMatch in imageRefs)
                {
                    var imgPath = imgMatch.Groups[2].Value;
                    if (imgPath.StartsWith("http://") || imgPath.StartsWith("https://"))
                        continue;

                    // Resolve relative to the markdown file's directory, then fall back to project root
                    var resolvedPath = Path.Combine(Path.GetDirectoryName(filePath)!, imgPath);
                    if (!File.Exists(resolvedPath))
                        resolvedPath = Path.Combine(workingDir, imgPath);
                    if (!File.Exists(resolvedPath))
                    {
                        // Try common locations
                        var projectRoot = Path.GetDirectoryName(workingDir);
                        if (projectRoot != null)
                            resolvedPath = Path.Combine(projectRoot, imgPath);
                    }

                    if (File.Exists(resolvedPath))
                    {
                        await _console.Status()
                            .Spinner(Spinner.Known.Dots)
                            .StartAsync($"[cyan]Uploading {Path.GetFileName(resolvedPath)}...[/]", async _ =>
                            {
                                await _confluenceService.UploadAttachmentAsync(pageId, resolvedPath);
                            });
                    }
                }

                // Track created page ID for pending: parent resolution by child pages
                var fileSlug = Path.GetFileNameWithoutExtension(fileName);
                slugToPageId[fileSlug] = updatedPage.Id;

                // Update frontmatter with real page ID and new version, move to _committed
                var updatedFrontmatter = new ConfluenceFrontmatter
                {
                    Id = updatedPage.Id,
                    Version = updatedPage.Version.Number,
                    Space = frontmatter.Space,
                    Title = frontmatter.Title,
                    ParentId = resolvedParentId ?? frontmatter.ParentId,
                    LastSynced = DateTime.UtcNow
                };

                var updatedMarkdown = _converter.StorageToMarkdown(
                    updatedPage.Body?.Storage?.Value ?? storageHtml,
                    updatedFrontmatter);

                var committedPath = Path.Combine(committedDir, fileName);
                await File.WriteAllTextAsync(committedPath, updatedMarkdown);
                File.Delete(filePath);

                // Git commit — only stage the specific files that changed
                RunGit(workingDir, $"add \"{Path.Combine("_pending", fileName)}\"");
                RunGit(workingDir, $"add \"{Path.Combine("_committed", fileName)}\"");
                RunGit(workingDir, $"commit -m \"committed: {EscapeGitMessage(frontmatter.Title)} synced to Confluence\"");

                results.AddRow(Markup.Escape(frontmatter.Title), "[green]Success[/]");
                successCount++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                results.AddRow(Markup.Escape(frontmatter.Title),
                    "[red]Conflict — page modified on Confluence. Re-checkout needed.[/]");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                results.AddRow(Markup.Escape(frontmatter.Title),
                    "[red]Not found — page may have been deleted on Confluence.[/]");
            }
            catch (Exception ex)
            {
                results.AddRow(Markup.Escape(frontmatter.Title),
                    $"[red]Error: {Markup.Escape(ex.Message)}[/]");
            }
        }

        _console.WriteLine();
        _console.Write(new Panel(results)
            .Header($"[green]Commit Results ({successCount}/{selected.Count} successful)[/]")
            .Border(BoxBorder.Rounded));

        // After successful commits, resync full page tree from Confluence
        if (successCount > 0 && !string.IsNullOrEmpty(config.RootPageId))
        {
            _console.MarkupLine("\n[dim]Resyncing from Confluence...[/]");
            await ResyncFromConfluence(workingDir, config);
        }

        return successCount == selected.Count ? 0 : 1;
    }

    /// <summary>Full resync: fetch entire page tree and write to folder hierarchy (same as checkout with no args).</summary>
    private async Task ResyncFromConfluence(string workingDir, ConfluenceWorkspaceConfig config)
    {
        try
        {
            List<ConfluencePage> pages = null!;
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Fetching page tree...[/]", async _ =>
                {
                    pages = await _confluenceService.GetPageTreeAsync(config.RootPageId!);
                });

            var pagesWithBody = new List<ConfluencePage>();
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"[cyan]Downloading {pages.Count} pages...[/]", async _ =>
                {
                    foreach (var page in pages)
                    {
                        var full = await _confluenceService.GetPageByIdAsync(page.Id, expandBody: true);
                        if (full != null)
                            pagesWithBody.Add(full);
                    }
                });

            // Build parent map
            var parentMap = new Dictionary<string, string>();
            foreach (var page in pagesWithBody)
            {
                if (page.Ancestors.Count > 0)
                    parentMap[page.Id] = page.Ancestors[^1].Id;
            }

            var pageById = pagesWithBody.ToDictionary(p => p.Id);
            var now = DateTime.UtcNow;

            foreach (var page in pagesWithBody)
            {
                var relativePath = ConfluenceCheckoutCommand.BuildPagePath(page, parentMap, pageById, config.RootPageId!);
                var fullPath = Path.Combine(workingDir, relativePath, "index.md");

                var parentId = parentMap.TryGetValue(page.Id, out var pid) ? pid : null;
                var frontmatter = new ConfluenceFrontmatter
                {
                    Id = page.Id,
                    Version = page.Version.Number,
                    Space = page.Space?.Key ?? config.SpaceKey,
                    Title = page.Title,
                    ParentId = parentId,
                    LastSynced = now
                };

                var storageHtml = page.Body?.Storage?.Value ?? "";
                var markdown = _converter.StorageToMarkdown(storageHtml, frontmatter);

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, markdown);
            }

            RunGit(workingDir, "add -A");
            RunGit(workingDir, $"commit -m \"resync: pulled {pagesWithBody.Count} pages from Confluence\" --allow-empty");
            _console.MarkupLine($"[green]Resynced {pagesWithBody.Count} pages.[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Resync failed: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static string EscapeGitMessage(string message)
    {
        return message.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
    }

    /// <summary>Run git targeting the workspace's own .git directory (not the parent repo).</summary>
    private static void RunGit(string workingDir, string args)
    {
        var gitDir = Path.Combine(workingDir, ".git");
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["GIT_DIR"] = gitDir;
        psi.Environment["GIT_WORK_TREE"] = workingDir;

        using var process = Process.Start(psi);
        process?.WaitForExit(30_000);
    }
}
