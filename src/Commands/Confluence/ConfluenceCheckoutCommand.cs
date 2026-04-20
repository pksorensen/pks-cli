using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Confluence;

/// <summary>Sync Confluence pages to local markdown files.</summary>
[Description("Sync Confluence pages to local markdown files")]
public class ConfluenceCheckoutCommand : Command<ConfluenceCheckoutCommand.Settings>
{
    private readonly IConfluenceService _confluenceService;
    private readonly IConfluenceMarkdownConverter _converter;
    private readonly IAnsiConsole _console;

    public ConfluenceCheckoutCommand(IConfluenceService confluenceService, IConfluenceMarkdownConverter converter, IAnsiConsole console)
    {
        _confluenceService = confluenceService;
        _converter = converter;
        _console = console;
    }

    public class Settings : ConfluenceSettings
    {
        [CommandArgument(0, "[page]")]
        [Description("Page ID or title to checkout for editing (omit for full sync)")]
        public string? Page { get; set; }

        [CommandOption("--create|-c")]
        [Description("Create the page on Confluence if it doesn't exist (non-interactive)")]
        public bool Create { get; set; }

        [CommandOption("--parent|-p")]
        [Description("Parent page ID when creating a new page (defaults to root page)")]
        public string? ParentId { get; set; }
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

        // WorkDir is resolved to absolute path by LoadWorkspaceConfigAsync
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

        if (string.IsNullOrEmpty(settings.Page))
            return await FullCheckout(workingDir, config);
        else
            return await SingleCheckout(workingDir, config, settings);
    }

    /// <summary>Full sync: fetch entire page tree and write to folder hierarchy.</summary>
    private async Task<int> FullCheckout(string workingDir, ConfluenceWorkspaceConfig config)
    {
        if (string.IsNullOrEmpty(config.RootPageId))
        {
            _console.MarkupLine("[red]Full checkout requires a root page ID. Reinitialize with --root-page.[/]");
            return 1;
        }

        List<ConfluencePage> pages = null!;
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cyan]Fetching page tree from Confluence...[/]", async _ =>
            {
                pages = await _confluenceService.GetPageTreeAsync(config.RootPageId);
            });

        _console.MarkupLine($"[green]Found {pages.Count} pages.[/]");

        // Fetch body for each page (GetPageTree only returns version/space)
        var pagesWithBody = new List<ConfluencePage>();
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[cyan]Downloading page contents...[/]", async _ =>
            {
                foreach (var page in pages)
                {
                    var full = await _confluenceService.GetPageByIdAsync(page.Id, expandBody: true);
                    if (full != null)
                        pagesWithBody.Add(full);
                }
            });

        // Build parent→children map for folder hierarchy
        var parentMap = new Dictionary<string, string>(); // pageId → parentId
        foreach (var page in pagesWithBody)
        {
            if (page.Ancestors.Count > 0)
            {
                var directParent = page.Ancestors[^1];
                parentMap[page.Id] = directParent.Id;
            }
        }

        var pageById = pagesWithBody.ToDictionary(p => p.Id);
        var now = DateTime.UtcNow;
        var writtenFiles = new List<string>();

        foreach (var page in pagesWithBody)
        {
            var relativePath = BuildPagePath(page, parentMap, pageById, config.RootPageId);
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
            writtenFiles.Add(relativePath + "/index.md");
        }

        // Git commit
        RunGit(workingDir, "add -A");
        RunGit(workingDir, $"commit -m \"checkout: synced {pagesWithBody.Count} pages from Confluence\" --allow-empty");

        // Display tree
        var tree = new Tree("[green]Synced pages[/]");
        foreach (var file in writtenFiles.OrderBy(f => f))
            tree.AddNode($"[dim]{Markup.Escape(file)}[/]");
        _console.Write(tree);

        return 0;
    }

    /// <summary>Single page checkout: fetch one page and place in _pending/.</summary>
    private async Task<int> SingleCheckout(string workingDir, ConfluenceWorkspaceConfig config, Settings settings)
    {
        var pageIdOrTitle = settings.Page!;
        ConfluencePage? page = null;
        await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"[cyan]Fetching page...[/]", async _ =>
            {
                // Try as page ID first (numeric), then as title
                if (pageIdOrTitle.All(char.IsDigit))
                    page = await _confluenceService.GetPageByIdAsync(pageIdOrTitle);

                if (page == null)
                    page = await _confluenceService.FindPageByTitleAsync(config.SpaceKey, pageIdOrTitle);
            });

        if (page == null)
        {
            // Page doesn't exist on Confluence — prepare a new page locally.
            // The page will only be created on Confluence when the user runs `pks confluence commit`.
            _console.MarkupLine($"[yellow]Page not found on Confluence: {Markup.Escape(pageIdOrTitle)}[/]");

            bool shouldCreate = settings.Create;
            if (!shouldCreate)
            {
                shouldCreate = _console.Prompt(
                    new ConfirmationPrompt($"Prepare new page \"{pageIdOrTitle}\" locally?") { DefaultValue = true });
            }

            if (!shouldCreate)
                return 1;

            // Determine parent: --parent flag, or prompt, or fall back to root
            // Parent can be a page ID, a pending file slug (e.g. "datamodeller-og-mapping"),
            // or a ref like "pending:datamodeller-og-mapping" for explicit pending reference
            var newParentId = settings.ParentId ?? config.RootPageId;
            if (string.IsNullOrEmpty(settings.ParentId) && !settings.Create)
            {
                var parentInput = _console.Prompt(
                    new TextPrompt<string>($"[cyan]Parent page ID[/] [dim](default: root {config.RootPageId})[/]:")
                        .AllowEmpty()
                        .DefaultValue(config.RootPageId ?? ""));

                if (!string.IsNullOrWhiteSpace(parentInput))
                    newParentId = parentInput;
            }

            // If parent references a pending file, store as "pending:<slug>" so commit can resolve it
            if (!string.IsNullOrEmpty(newParentId) && !newParentId.All(char.IsDigit))
            {
                var slug = newParentId.StartsWith("pending:") ? newParentId.Substring(8) : newParentId;
                // Check if a pending file with this slug exists
                var pendingCheck = Path.Combine(workingDir, "_pending", slug + ".md");
                if (!File.Exists(pendingCheck))
                    pendingCheck = Path.Combine(workingDir, "_pending", Slugify(slug) + ".md");
                if (File.Exists(pendingCheck))
                    newParentId = "pending:" + Path.GetFileNameWithoutExtension(pendingCheck);
            }

            // Create local _pending file with "new" marker — no Confluence API call
            {
                var pd = Path.Combine(workingDir, "_pending");
                Directory.CreateDirectory(pd);

                var fm = new ConfluenceFrontmatter
                {
                    Id = "new",
                    Version = 0,
                    Space = config.SpaceKey,
                    Title = pageIdOrTitle,
                    ParentId = newParentId,
                    LastSynced = DateTime.UtcNow
                };

                var md = _converter.StorageToMarkdown("", fm);
                var fn = Slugify(pageIdOrTitle) + ".md";
                var fp = Path.Combine(pd, fn);

                await File.WriteAllTextAsync(fp, md);

                RunGit(workingDir, $"add \"{Path.Combine("_pending", fn)}\"");
                RunGit(workingDir, $"commit -m \"checkout: {EscapeGitMessage(pageIdOrTitle)} prepared as new page\"");

                _console.MarkupLine($"[green]Prepared new page:[/] _pending/{Markup.Escape(fn)}");
                _console.MarkupLine($"[dim]Edit the file, then run [bold]pks confluence commit[/] to create it on Confluence.[/]");
                return 0;
            }
        }

        var pendingDir = Path.Combine(workingDir, "_pending");
        Directory.CreateDirectory(pendingDir);

        var parentId = page.Ancestors.Count > 0 ? page.Ancestors[^1].Id : null;
        var frontmatter = new ConfluenceFrontmatter
        {
            Id = page.Id,
            Version = page.Version.Number,
            Space = page.Space?.Key ?? config.SpaceKey,
            Title = page.Title,
            ParentId = parentId,
            LastSynced = DateTime.UtcNow
        };

        var storageHtml = page.Body?.Storage?.Value ?? "";
        var markdown = _converter.StorageToMarkdown(storageHtml, frontmatter);

        var filename = Slugify(page.Title) + ".md";
        var filePath = Path.Combine(pendingDir, filename);

        // Avoid overwriting without warning
        if (File.Exists(filePath))
        {
            _console.MarkupLine($"[yellow]File already exists in _pending: {filename}[/]");
            var overwrite = _console.Prompt(
                new ConfirmationPrompt("Overwrite?") { DefaultValue = true });
            if (!overwrite)
                return 0;
        }

        await File.WriteAllTextAsync(filePath, markdown);

        // Git add + commit so edits show as diff
        RunGit(workingDir, $"add \"{Path.Combine("_pending", filename)}\"");
        RunGit(workingDir, $"commit -m \"checkout: {EscapeGitMessage(page.Title)} checked out for editing\"");

        _console.MarkupLine($"[green]Checked out to:[/] _pending/{Markup.Escape(filename)}");
        _console.MarkupLine("[dim]Edit the file, then run [bold]pks confluence commit[/] to push changes back.[/]");

        return 0;
    }

    /// <summary>Builds relative folder path for a page based on its ancestry.</summary>
    internal static string BuildPagePath(ConfluencePage page, Dictionary<string, string> parentMap, Dictionary<string, ConfluencePage> pageById, string rootPageId)
    {
        var segments = new List<string>();
        segments.Add(Slugify(page.Title));

        var currentId = page.Id;
        while (parentMap.TryGetValue(currentId, out var pid) && pid != rootPageId)
        {
            if (pageById.TryGetValue(pid, out var parentPage))
                segments.Insert(0, Slugify(parentPage.Title));
            else
                break;
            currentId = pid;
        }

        return Path.Combine(segments.ToArray());
    }

    internal static string Slugify(string title)
    {
        var slug = title.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("/", "-")
            .Replace("\\", "-");

        slug = Regex.Replace(slug, @"[^a-z0-9\-æøåöäü]", "");
        slug = Regex.Replace(slug, @"-{2,}", "-");
        slug = slug.Trim('-');

        // Truncate to 40 chars to avoid path length issues
        if (slug.Length > 40)
            slug = slug[..40].TrimEnd('-');

        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
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
