using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Confluence;

/// <summary>Initialize a Confluence workspace for local markdown editing with git tracking.</summary>
[Description("Initialize a Confluence workspace for local editing")]
public class ConfluenceInitCommand : Command<ConfluenceInitCommand.Settings>
{
    private readonly IConfluenceService _confluenceService;
    private readonly IJiraService _jiraService;
    private readonly IAnsiConsole _console;

    public ConfluenceInitCommand(IConfluenceService confluenceService, IJiraService jiraService, IAnsiConsole console)
    {
        _confluenceService = confluenceService;
        _jiraService = jiraService;
        _console = console;
    }

    public class Settings : ConfluenceSettings
    {
        [CommandOption("--space|-s")]
        [Description("Confluence space key (e.g. OptiDyna)")]
        public string? SpaceKey { get; set; }

        [CommandOption("--root-page")]
        [Description("Root page ID to scope the workspace (optional)")]
        public string? RootPageId { get; set; }

        [CommandOption("--dir|-d")]
        [Description("Working directory for the workspace (default: ./docs/confluence)")]
        public string? WorkingDir { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // 1. Verify Jira auth (same credentials used for Confluence)
        if (!await _jiraService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Atlassian. Run [bold]pks jira init[/] first.[/]");
            return 1;
        }

        var credentials = await _jiraService.GetStoredCredentialsAsync();
        if (credentials == null)
        {
            _console.MarkupLine("[red]Could not load stored credentials.[/]");
            return 1;
        }

        // 0. Find project root (nearest parent with .git) and determine workspace dir
        var projectRoot = FindProjectRoot(Directory.GetCurrentDirectory());
        if (projectRoot == null)
        {
            _console.MarkupLine("[red]No git repository found. Run from inside a git project.[/]");
            return 1;
        }

        var workDirRelative = settings.WorkingDir ?? "docs/confluence";
        if (string.IsNullOrEmpty(settings.WorkingDir))
        {
            workDirRelative = _console.Prompt(
                new TextPrompt<string>("[cyan]Workspace directory (relative to project root):[/]")
                    .DefaultValue("docs/confluence"));
        }

        var workingDir = Path.GetFullPath(Path.Combine(projectRoot, workDirRelative));
        Directory.CreateDirectory(workingDir);

        // Check if already initialized
        var existing = await _confluenceService.LoadWorkspaceConfigAsync(projectRoot);
        if (existing != null)
        {
            _console.MarkupLine($"[yellow]Workspace already initialized (space: {existing.SpaceKey}).[/]");
            var overwrite = _console.Prompt(
                new ConfirmationPrompt("Reinitialize?") { DefaultValue = false });
            if (!overwrite)
                return 0;
        }

        // 2. Prompt for space key (with list of available spaces)
        var spaceKey = settings.SpaceKey;
        string? homepageId = null;

        if (string.IsNullOrEmpty(spaceKey))
        {
            // Wire up debug output if requested
            if (settings.Debug && _confluenceService is ConfluenceService svc)
                svc.DebugWriter = msg => _console.MarkupLine(msg);

            List<ConfluenceSpaceInfo>? spaces = null;
            string? fetchError = null;
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Fetching available spaces...[/]", async _ =>
                {
                    try { spaces = await _confluenceService.GetSpacesAsync(); }
                    catch (Exception ex) { fetchError = ex.Message; }
                });

            if (fetchError != null)
                _console.MarkupLine($"[yellow]Could not fetch spaces: {Markup.Escape(fetchError)}[/]");

            if (spaces != null && spaces.Count > 0)
            {
                var choices = spaces.Select(s => $"{s.Key} — {s.Name}").ToList();
                choices.Add("Enter manually...");

                var selected = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select a Confluence space:[/]")
                        .PageSize(15)
                        .AddChoices(choices));

                if (selected == "Enter manually...")
                {
                    spaceKey = _console.Prompt(
                        new TextPrompt<string>("[cyan]Confluence space key:[/]")
                            .Validate(s => !string.IsNullOrWhiteSpace(s)
                                ? ValidationResult.Success()
                                : ValidationResult.Error("Space key is required")));
                }
                else
                {
                    var idx = choices.IndexOf(selected);
                    spaceKey = spaces[idx].Key;
                    homepageId = spaces[idx].HomepageId;
                    _console.MarkupLine($"[green]Selected space:[/] {spaceKey} ({Markup.Escape(spaces[idx].Name)})");
                }
            }
            else
            {
                spaceKey = _console.Prompt(
                    new TextPrompt<string>("[cyan]Confluence space key:[/]")
                        .Validate(s => !string.IsNullOrWhiteSpace(s)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Space key is required")));
            }
        }

        // 3. Prompt for optional root page
        var rootPageId = settings.RootPageId;
        if (string.IsNullOrEmpty(rootPageId))
        {
            var defaultHint = !string.IsNullOrEmpty(homepageId)
                ? $" [dim](homepage: {homepageId})[/]"
                : "";

            var prompt = new TextPrompt<string>($"[cyan]Root page ID{defaultHint} (leave empty for entire space):[/]")
                .AllowEmpty();

            if (!string.IsNullOrEmpty(homepageId))
                prompt.DefaultValue(homepageId);

            rootPageId = _console.Prompt(prompt);
            if (string.IsNullOrWhiteSpace(rootPageId))
                rootPageId = null;
        }

        // 4. Derive site URL
        var siteUrl = credentials.BaseUrl.Trim().TrimEnd('/');

        // 5. Save workspace config at project root
        var config = new ConfluenceWorkspaceConfig
        {
            SpaceKey = spaceKey,
            RootPageId = rootPageId,
            SiteUrl = siteUrl,
            WorkDir = workDirRelative,
            CreatedAt = DateTime.UtcNow
        };

        await _confluenceService.SaveWorkspaceConfigAsync(projectRoot, config);

        // 6. Create .gitignore
        var gitignorePath = Path.Combine(workingDir, ".gitignore");
        var gitignoreContent = ".confluence/\n";
        if (File.Exists(gitignorePath))
        {
            var existing2 = await File.ReadAllTextAsync(gitignorePath);
            if (!existing2.Contains(".confluence/"))
                await File.AppendAllTextAsync(gitignorePath, "\n" + gitignoreContent);
        }
        else
        {
            await File.WriteAllTextAsync(gitignorePath, gitignoreContent);
        }

        // 7. Git init (independent repo for the workspace)
        var gitDir = Path.Combine(workingDir, ".git");
        if (!Directory.Exists(gitDir))
        {
            // Use GIT_DIR to force creating a new repo even inside an existing parent repo
            var initPsi = new ProcessStartInfo("git", "init")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            initPsi.Environment["GIT_DIR"] = gitDir;
            initPsi.Environment["GIT_WORK_TREE"] = workingDir;
            using var initProcess = Process.Start(initPsi);
            initProcess?.WaitForExit(10_000);

            if (!Directory.Exists(gitDir))
                _console.MarkupLine("[yellow]Warning: git init did not create .git directory[/]");
        }

        // 8. Exclude this workspace from the parent repo's git
        var parentGitIgnore = FindParentGitIgnore(workingDir);
        if (parentGitIgnore != null)
        {
            var relativePath = Path.GetRelativePath(
                Path.GetDirectoryName(parentGitIgnore)!, workingDir).Replace('\\', '/');
            if (!relativePath.EndsWith("/")) relativePath += "/";

            var parentContent = await File.ReadAllTextAsync(parentGitIgnore);
            if (!parentContent.Contains(relativePath))
            {
                await File.AppendAllTextAsync(parentGitIgnore, $"\n# Confluence workspace (separate git repo)\n{relativePath}\n");
                _console.MarkupLine($"[dim]Added {relativePath} to parent .gitignore[/]");
            }
        }

        // 9. Initial commit in workspace repo
        RunGit(workingDir, gitDir, "add .gitignore");
        RunGit(workingDir, gitDir, "commit -m \"init: confluence workspace\" --allow-empty");

        // 8. Create _pending and _committed directories
        Directory.CreateDirectory(Path.Combine(workingDir, "_pending"));
        Directory.CreateDirectory(Path.Combine(workingDir, "_committed"));

        // 9. Success display
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Setting")
            .AddColumn("Value");

        table.AddRow("Space", spaceKey);
        table.AddRow("Root Page", rootPageId ?? "(entire space)");
        table.AddRow("Site URL", siteUrl);
        table.AddRow("Working Dir", workingDir);

        _console.Write(new Panel(table)
            .Header("[green]Confluence Workspace Initialized[/]")
            .Border(BoxBorder.Rounded));

        _console.MarkupLine("\n[dim]Next: run [bold]pks confluence checkout[/] to sync pages.[/]");

        return 0;
    }

    /// <summary>Run git targeting the workspace's own .git directory (not the parent repo).</summary>
    private static void RunGit(string workingDir, string gitDir, string args)
    {
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
        process?.WaitForExit(10_000);
    }

    /// <summary>Walk up to find the nearest directory containing .git.</summary>
    private static string? FindProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Walk up from workingDir to find the nearest .gitignore next to a .git directory (skipping workspace's own .git).</summary>
    private static string? FindParentGitIgnore(string workingDir)
    {
        var dir = Directory.GetParent(workingDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return Path.Combine(dir.FullName, ".gitignore");
            dir = dir.Parent;
        }
        return null;
    }
}
