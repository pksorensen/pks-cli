using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Microsoft.Extensions.DependencyInjection;

namespace PKS.Commands;

/// <summary>
/// Command to initialize a new agentic development project with devcontainer support
/// </summary>
public class InitCommand : Command<InitCommand.Settings>
{
    private readonly INuGetTemplateDiscoveryService _templateDiscovery;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAnsiConsole _console;
    private readonly string? _workingDirectory;

    public InitCommand(
        INuGetTemplateDiscoveryService templateDiscovery,
        IServiceProvider serviceProvider,
        IAnsiConsole? console = null,
        string? workingDirectory = null)
    {
        _templateDiscovery = templateDiscovery;
        _serviceProvider = serviceProvider;
        _console = console ?? AnsiConsole.Console;
        _workingDirectory = workingDirectory;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT_NAME]")]
        [Description("The name of the project to initialize")]
        public string? ProjectName { get; set; }

        [CommandOption("-t|--template <TEMPLATE>")]
        [Description("Template short name to use (e.g., pks-claude-dotnet9)")]
        public string? Template { get; set; }

        [CommandOption("-d|--description <DESCRIPTION>")]
        [Description("Optional project description")]
        public string? Description { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force overwrite existing files")]
        public bool Force { get; set; }

        [CommandOption("--nuget-source <SOURCE>")]
        [Description("Custom NuGet source/feed to search for templates")]
        public string? NuGetSource { get; set; }

        [CommandOption("--local-template-path <PATH>")]
        [Description("Path to a local template directory (bypasses NuGet for testing)")]
        public string? LocalTemplatePath { get; set; }

        [CommandOption("--tag <TAG>")]
        [Description("NuGet tag to filter templates (default: pks-templates)")]
        [DefaultValue("pks-templates")]
        public string Tag { get; set; } = "pks-templates";

        [CommandOption("--prerelease")]
        [Description("Include prerelease/preview template packages")]
        public bool IncludePrerelease { get; set; }

        [CommandOption("--agentic")]
        [Description("Enable agentic features and AI automation")]
        public bool EnableAgentic { get; set; }

        [CommandOption("--mcp")]
        [Description("Enable Model Context Protocol (MCP) integration")]
        public bool EnableMcp { get; set; }

        [CommandOption("--spawn-devcontainer")]
        [Description("Automatically spawn devcontainer after initialization")]
        public bool SpawnDevcontainer { get; set; }

        [CommandOption("--no-devcontainer-prompt")]
        [Description("Skip the prompt to spawn devcontainer")]
        public bool NoDevcontainerPrompt { get; set; }

        [CommandOption("--volume-name <NAME>")]
        [Description("Custom volume name for devcontainer")]
        public string? VolumeName { get; set; }

        [CommandOption("--no-launch-vscode")]
        [Description("Don't automatically launch VS Code")]
        public bool NoLaunchVsCode { get; set; }

        [CommandOption("--build-arg <ARG>")]
        [Description("Docker build argument in KEY=VALUE format (can be specified multiple times)")]
        public string[]? BuildArgs { get; set; }

        [CommandOption("--prompt-build-args")]
        [Description("Interactively prompt for Docker build arguments with defaults from devcontainer.json")]
        public bool PromptBuildArgs { get; set; }

        [CommandOption("--build-log <PATH>")]
        [Description("Write devcontainer build output to a log file instead of console")]
        public string? BuildLogPath { get; set; }
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Display PKS CLI banner
        DisplayBanner();

        // Interactive project name collection if not provided
        if (string.IsNullOrEmpty(settings.ProjectName))
        {
            settings.ProjectName = _console.Ask<string>("\n[cyan]What's the[/] [green]name[/] [cyan]of your project?[/]");
        }

        // Validate project name
        if (string.IsNullOrWhiteSpace(settings.ProjectName) ||
            settings.ProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _console.MarkupLine("[red]❌ Invalid project name. Please use valid file name characters.[/]");
            return 1;
        }

        try
        {
            // Discover available templates from NuGet or local path
            List<NuGetDevcontainerTemplate> templates = new();
            bool useLocalTemplate = !string.IsNullOrEmpty(settings.LocalTemplatePath);
            string? localTemplateContentPath = null;

            if (useLocalTemplate)
            {
                // Validate local template path
                if (!Directory.Exists(settings.LocalTemplatePath))
                {
                    _console.MarkupLine($"[red]❌ Local template path '{settings.LocalTemplatePath}' does not exist.[/]");
                    return 1;
                }

                // Load local template
                _console.MarkupLine($"[cyan]📂 Loading template from local path:[/] {settings.LocalTemplatePath}");
                var localTemplate = await LoadLocalTemplateAsync(settings.LocalTemplatePath!);

                if (localTemplate == null)
                {
                    _console.MarkupLine($"[red]❌ Failed to load local template. Ensure .template.config/template.json exists.[/]");
                    return 1;
                }

                templates.Add(localTemplate);
                localTemplateContentPath = Path.Combine(settings.LocalTemplatePath!, "content");
            }
            else
            {
                // Discover from NuGet
                await _console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("[cyan]Discovering available templates...[/]", async ctx =>
                    {
                        var sources = string.IsNullOrEmpty(settings.NuGetSource)
                            ? null
                            : new[] { settings.NuGetSource };

                        templates = await _templateDiscovery.DiscoverTemplatesAsync(
                            tag: settings.Tag,
                            sources: sources,
                            includePrerelease: settings.IncludePrerelease,
                            cancellationToken: CancellationToken.None);
                    });

                if (!templates.Any())
                {
                    _console.MarkupLine($"[yellow]⚠️  No templates found with tag '{settings.Tag}'.[/]");
                    _console.MarkupLine("[dim]Try running: pks template list --all[/]");
                    return 1;
                }
            }

            // Interactive template selection if not provided
            NuGetDevcontainerTemplate selectedTemplate;

            if (useLocalTemplate)
            {
                // When using local template, there's only one template so select it directly
                selectedTemplate = templates.First();
            }
            else if (string.IsNullOrEmpty(settings.Template))
            {
                selectedTemplate = _console.Prompt(
                    new SelectionPrompt<NuGetDevcontainerTemplate>()
                        .Title("\n[cyan]Which template would you like to use?[/]")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more templates)[/]")
                        .AddChoices(templates)
                        .UseConverter(template =>
                            $"{GetTemplateIcon(template)} {template.Title} [dim]({template.PackageId}) v{template.Version}[/]"));
            }
            else
            {
                selectedTemplate = templates.FirstOrDefault(t =>
                    t.ShortNames.Any(sn => sn.Equals(settings.Template, StringComparison.OrdinalIgnoreCase)) ||
                    t.PackageId.Equals(settings.Template, StringComparison.OrdinalIgnoreCase))!;

                if (selectedTemplate == null)
                {
                    _console.MarkupLine($"[red]❌ Template '{settings.Template}' not found.[/]");
                    _console.MarkupLine($"\n[cyan]Available templates:[/]");

                    var table = new Table();
                    table.AddColumn("Short Name");
                    table.AddColumn("Package ID");
                    table.AddColumn("Version");
                    table.AddColumn("Description");

                    foreach (var t in templates)
                    {
                        table.AddRow(
                            t.ShortNames.Length > 0 ? string.Join(", ", t.ShortNames) : "[dim]N/A[/]",
                            t.PackageId,
                            $"[dim]v{t.Version}[/]",
                            t.Description ?? "[dim]No description[/]");
                    }

                    _console.Write(table);
                    return 1;
                }
            }

            // Get project description
            if (string.IsNullOrEmpty(settings.Description))
            {
                var defaultDesc = selectedTemplate.Description ?? "An agentic development project";
                settings.Description = _console.Prompt(
                    new TextPrompt<string>("[cyan]What's the[/] [green]description/objective[/] [cyan]of your project?[/]")
                        .DefaultValue(defaultDesc));
            }

            // Create target directory
            var workingDir = _workingDirectory ?? Environment.CurrentDirectory;
            var targetDirectory = Path.Combine(workingDir, settings.ProjectName);

            if (Directory.Exists(targetDirectory) && !settings.Force)
            {
                _console.MarkupLine($"[red]❌ Directory '{settings.ProjectName}' already exists. Use --force to overwrite.[/]");
                return 1;
            }

            // Install/extract template
            _console.MarkupLine($"\n[cyan]📦 Installing template:[/] {selectedTemplate.Title} v{selectedTemplate.Version}");

            NuGetTemplateExtractionResult extractionResult;

            if (useLocalTemplate && localTemplateContentPath != null)
            {
                // Copy from local template
                await _console.Status()
                    .Spinner(Spinner.Known.Star2)
                    .SpinnerStyle(Style.Parse("green bold"))
                    .StartAsync($"Copying template to '{settings.ProjectName}'...", async ctx =>
                    {
                        extractionResult = await CopyLocalTemplateAsync(
                            localTemplateContentPath,
                            targetDirectory,
                            selectedTemplate);
                    });
            }
            else
            {
                // Extract from NuGet
                await _console.Status()
                    .Spinner(Spinner.Known.Star2)
                    .SpinnerStyle(Style.Parse("green bold"))
                    .StartAsync($"Extracting template to '{settings.ProjectName}'...", async ctx =>
                    {
                        extractionResult = await _templateDiscovery.ExtractTemplateAsync(
                            selectedTemplate.PackageId,
                            selectedTemplate.Version,
                            targetDirectory,
                            cancellationToken: CancellationToken.None);
                    });
            }

            // Display success message
            var panel = new Panel($"""
                🎉 [bold green]Project '{settings.ProjectName}' initialized successfully![/]

                [cyan1]Template:[/] {selectedTemplate.Title}
                [cyan1]Package:[/] {selectedTemplate.PackageId} v{selectedTemplate.Version}
                [cyan1]Description:[/] {settings.Description}
                [cyan1]Location:[/] {targetDirectory}

                [bold cyan]Next steps:[/]
                • [cyan]cd {settings.ProjectName}[/] - Navigate to your project
                • [cyan]code .[/] - Open in VS Code
                • [cyan]Select "Reopen in Container"[/] to start development
                {(settings.EnableAgentic ? "\n• [cyan]pks agent create[/] - Add AI development agents" : "")}
                {(settings.EnableMcp ? "\n• [cyan]pks mcp init[/] - Configure MCP integration" : "")}

                [dim]Ready for agentic development! 🚀[/]
                """)
                .Border(BoxBorder.Double)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]🚀 PKS Project Ready[/] ");

            _console.Write(panel);

            // Check if devcontainer exists and offer to spawn
            var devcontainerPath = Path.Combine(targetDirectory, ".devcontainer");
            var hasDevcontainer = Directory.Exists(devcontainerPath) &&
                                  File.Exists(Path.Combine(devcontainerPath, "devcontainer.json"));

            if (hasDevcontainer && !settings.NoDevcontainerPrompt)
            {
                var spawnerService = _serviceProvider.GetRequiredService<IDevcontainerSpawnerService>();

                bool shouldSpawn = settings.SpawnDevcontainer;

                if (!shouldSpawn)
                {
                    _console.WriteLine();
                    shouldSpawn = _console.Confirm(
                        "[cyan]Would you like to spawn the devcontainer now?[/] " +
                        "[dim](Opens in Docker volume with VS Code)[/]",
                        defaultValue: true); // USER PREFERENCE: Default to Yes
                }

                if (shouldSpawn)
                {
                    await SpawnDevcontainerWorkflowAsync(
                        spawnerService,
                        settings,
                        targetDirectory);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return 1;
        }
    }

    private void DisplayBanner()
    {
        _console.Write(new FigletText("PKS CLI")
            .LeftJustified()
            .Color(Color.Cyan1));

        _console.MarkupLine("[cyan]🤖 Agentic Development Environment Setup[/]");
        _console.MarkupLine("[dim]Discover and install devcontainer templates from NuGet[/]\n");
    }

    private string GetTemplateIcon(NuGetDevcontainerTemplate template)
    {
        // Try to determine icon from package ID or tags
        var id = template.PackageId.ToLowerInvariant();
        var tags = template.Tags.Select(t => t.ToLowerInvariant()).ToList();

        if (id.Contains("dotnet") || id.Contains("csharp") || tags.Contains("dotnet"))
            return "🔷";
        if (id.Contains("python") || tags.Contains("python"))
            return "🐍";
        if (id.Contains("node") || id.Contains("javascript") || tags.Contains("nodejs"))
            return "📗";
        if (id.Contains("go") || tags.Contains("go") || tags.Contains("golang"))
            return "🐹";
        if (id.Contains("rust") || tags.Contains("rust"))
            return "🦀";
        if (id.Contains("java") || tags.Contains("java"))
            return "☕";
        if (id.Contains("claude") || id.Contains("ai") || tags.Contains("claude"))
            return "🤖";
        if (id.Contains("aspire") || tags.Contains("aspire"))
            return "✨";

        return "📦";
    }

    private async Task SpawnDevcontainerWorkflowAsync(
        IDevcontainerSpawnerService spawnerService,
        Settings settings,
        string projectPath)
    {
        try
        {
            _console.WriteLine();
            _console.MarkupLine("[cyan]Preparing to spawn devcontainer...[/]");

            // 1. Pre-flight checks
            DockerAvailabilityResult? dockerCheck = null;
            bool? cliInstalled = null;

            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Checking Docker and devcontainer CLI...[/]", async ctx =>
                {
                    dockerCheck = await spawnerService.CheckDockerAvailabilityAsync();
                    if (!dockerCheck.IsAvailable)
                    {
                        return;
                    }

                    cliInstalled = await spawnerService.IsDevcontainerCliInstalledAsync();
                });

            if (dockerCheck != null && !dockerCheck.IsAvailable)
            {
                _console.MarkupLine($"[red]❌ Docker Not Available[/]");
                _console.MarkupLine($"[yellow]{dockerCheck.Message}[/]");
                _console.MarkupLine("[dim]To spawn devcontainer later, run: pks devcontainer spawn[/]");
                return;
            }

            if (cliInstalled == false)
            {
                _console.MarkupLine("[red]❌ devcontainer CLI Not Found[/]");
                _console.MarkupLine("[yellow]Install: npm install -g @devcontainers/cli[/]");
                _console.MarkupLine("[dim]To spawn devcontainer later, run: pks devcontainer spawn[/]");
                return;
            }

            // 2. Generate and confirm volume name (USER PREFERENCE: Always show, allow edit)
            string confirmedVolumeName;
            if (!string.IsNullOrEmpty(settings.VolumeName))
            {
                // Volume name provided via command-line, use it directly
                confirmedVolumeName = settings.VolumeName;
                _console.MarkupLine($"[cyan]Docker volume name:[/] {confirmedVolumeName}");
            }
            else
            {
                // Interactive prompt for volume name
                var volumeName = spawnerService.GenerateVolumeName(settings.ProjectName!);
                _console.WriteLine();
                confirmedVolumeName = _console.Prompt(
                    new TextPrompt<string>("[cyan]Docker volume name:[/]")
                        .DefaultValue(volumeName)
                        .AllowEmpty());

                if (string.IsNullOrWhiteSpace(confirmedVolumeName))
                    confirmedVolumeName = volumeName;
            }

            // 3. Handle build arguments
            Dictionary<string, string>? buildArgs = null;

            // Parse build args from command line if provided
            if (settings.BuildArgs != null && settings.BuildArgs.Length > 0)
            {
                buildArgs = new Dictionary<string, string>();
                foreach (var arg in settings.BuildArgs)
                {
                    var parts = arg.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        buildArgs[parts[0].Trim()] = parts[1].Trim();
                    }
                    else
                    {
                        _console.MarkupLine($"[yellow]⚠️  Invalid build arg format: {arg} (expected KEY=VALUE)[/]");
                    }
                }
            }

            // Prompt for build args if requested
            if (settings.PromptBuildArgs)
            {
                buildArgs = await PromptForBuildArgsAsync(projectPath, buildArgs);
            }

            if (buildArgs != null && buildArgs.Count > 0)
            {
                _console.WriteLine();
                _console.MarkupLine("[cyan]Build arguments:[/]");
                foreach (var kvp in buildArgs)
                {
                    _console.MarkupLine($"  [dim]{kvp.Key}=[/]{kvp.Value}");
                }
            }

            // 4. Spawn devcontainer with progress tracking
            DevcontainerSpawnResult? result = null;
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Spawning devcontainer...[/]", async ctx =>
                {
                    var options = new DevcontainerSpawnOptions
                    {
                        ProjectName = settings.ProjectName!,
                        ProjectPath = projectPath,
                        DevcontainerPath = Path.Combine(projectPath, ".devcontainer"),
                        VolumeName = confirmedVolumeName,
                        CopySourceFiles = true, // USER PREFERENCE: Copy full project
                        LaunchVsCode = !settings.NoLaunchVsCode,
                        ReuseExisting = true,
                        BuildArgs = buildArgs,
                        BuildLogPath = settings.BuildLogPath
                    };

                    result = await spawnerService.SpawnLocalAsync(options);
                });

            // 5. Display result
            _console.WriteLine();
            if (result != null && result.Success)
            {
                var successPanel = new Panel($"""
                    ✓ [green]Devcontainer spawned successfully![/]

                    [cyan1]Container ID:[/] {result.ContainerId?[..12]}
                    [cyan1]Volume Name:[/] {result.VolumeName}
                    [cyan1]Workspace:[/] /workspaces/{settings.ProjectName}

                    {(!settings.NoLaunchVsCode ? "[dim]VS Code is opening...[/]" : "[dim]Connect manually with VS Code Dev Containers extension[/]")}

                    [bold]🚀 Devcontainer ready for development![/]
                    """)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle("green")
                    .Header(" [bold green]✓ Devcontainer Ready[/] ");

                _console.Write(successPanel);
            }
            else if (result != null)
            {
                _console.MarkupLine($"[red]❌ Failed to spawn devcontainer[/]");
                _console.MarkupLine($"[yellow]{result.Message.EscapeMarkup()}[/]");

                if (result.Errors.Any())
                {
                    _console.MarkupLine("[dim]Errors:[/]");
                    foreach (var error in result.Errors)
                        _console.MarkupLine($"  [red]• {error.EscapeMarkup()}[/]");
                }

                _console.WriteLine();
                _console.MarkupLine("[dim]To retry, run: pks devcontainer spawn[/]");

                // Note: Cleanup already handled by service (USER PREFERENCE: Auto-cleanup)
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]❌ Unexpected error during devcontainer spawn[/]");
            _console.WriteException(ex);
            _console.WriteLine();
            _console.MarkupLine("[dim]To retry, run: pks devcontainer spawn[/]");
        }
    }

    private async Task<Dictionary<string, string>> PromptForBuildArgsAsync(
        string projectPath,
        Dictionary<string, string>? existingArgs)
    {
        var buildArgs = existingArgs ?? new Dictionary<string, string>();
        var devcontainerJsonPath = Path.Combine(projectPath, ".devcontainer", "devcontainer.json");

        if (!File.Exists(devcontainerJsonPath))
        {
            _console.MarkupLine("[yellow]⚠️  devcontainer.json not found, skipping build arg prompts[/]");
            return buildArgs;
        }

        try
        {
            var jsonContent = await File.ReadAllTextAsync(devcontainerJsonPath);
            var devcontainerConfig = System.Text.Json.JsonDocument.Parse(jsonContent);

            // Try to find build.args in the JSON
            if (devcontainerConfig.RootElement.TryGetProperty("build", out var buildElement) &&
                buildElement.TryGetProperty("args", out var argsElement))
            {
                _console.WriteLine();
                _console.MarkupLine("[cyan]Build arguments from devcontainer.json:[/]");

                foreach (var arg in argsElement.EnumerateObject())
                {
                    var argName = arg.Name;
                    var defaultValue = arg.Value.GetString() ?? "";

                    // Skip if already provided via command line
                    if (buildArgs.ContainsKey(argName))
                    {
                        _console.MarkupLine($"  [dim]{argName}=[/]{buildArgs[argName]} [dim](from command line)[/]");
                        continue;
                    }

                    // Prompt for value with default
                    var prompt = new TextPrompt<string>($"  [cyan]{argName}:[/]")
                        .DefaultValue(defaultValue)
                        .AllowEmpty();

                    var value = _console.Prompt(prompt);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        buildArgs[argName] = value;
                    }
                }
            }
            else
            {
                _console.MarkupLine("[yellow]⚠️  No build args found in devcontainer.json[/]");
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]⚠️  Failed to parse devcontainer.json: {ex.Message}[/]");
        }

        return buildArgs;
    }

    private async Task<NuGetDevcontainerTemplate?> LoadLocalTemplateAsync(string templatePath)
    {
        try
        {
            var templateJsonPath = Path.Combine(templatePath, ".template.config", "template.json");

            if (!File.Exists(templateJsonPath))
            {
                return null;
            }

            var jsonContent = await File.ReadAllTextAsync(templateJsonPath);
            var templateConfig = System.Text.Json.JsonDocument.Parse(jsonContent);

            var root = templateConfig.RootElement;

            // Extract basic template info
            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? "Local Template"
                : "Local Template";

            var description = root.TryGetProperty("description", out var descElement)
                ? descElement.GetString() ?? "A local development template"
                : "A local development template";

            var shortName = root.TryGetProperty("shortName", out var shortNameElement)
                ? shortNameElement.GetString() ?? "local"
                : "local";

            var identity = root.TryGetProperty("identity", out var identityElement)
                ? identityElement.GetString() ?? "Local.Template"
                : "Local.Template";

            var author = root.TryGetProperty("author", out var authorElement)
                ? authorElement.GetString() ?? "Local"
                : "Local";

            // Extract classifications/tags
            var classifications = new List<string>();
            if (root.TryGetProperty("classifications", out var classificationsElement) &&
                classificationsElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in classificationsElement.EnumerateArray())
                {
                    if (item.GetString() is string classification)
                    {
                        classifications.Add(classification);
                    }
                }
            }

            return new NuGetDevcontainerTemplate
            {
                Id = identity,
                PackageId = identity,
                Version = "local",
                Title = name,
                Description = description,
                Authors = author,
                Tags = classifications.ToArray(),
                ShortNames = new[] { shortName },
                InstallPath = templatePath,
                Source = "local"
            };
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]⚠️  Failed to load local template: {ex.Message}[/]");
            return null;
        }
    }

    private async Task<NuGetTemplateExtractionResult> CopyLocalTemplateAsync(
        string sourceContentPath,
        string targetDirectory,
        NuGetDevcontainerTemplate template)
    {
        var result = new NuGetTemplateExtractionResult
        {
            Success = false,
            ExtractedPath = targetDirectory
        };

        try
        {
            var startTime = DateTime.UtcNow;

            // Create target directory
            Directory.CreateDirectory(targetDirectory);

            // Copy all files from source to target
            await CopyDirectoryRecursiveAsync(sourceContentPath, targetDirectory, result.ExtractedFiles);

            result.Success = true;
            result.ExtractionTime = DateTime.UtcNow - startTime;
            result.Message = $"Copied {result.ExtractedFiles.Count} files from local template";

            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Failed to copy local template: {ex.Message}";
            return result;
        }
    }

    private async Task CopyDirectoryRecursiveAsync(string sourceDir, string targetDir, List<string> copiedFiles)
    {
        // Create target directory
        Directory.CreateDirectory(targetDir);

        // Copy all files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);

            File.Copy(file, targetFile, true);
            copiedFiles.Add(targetFile);
        }

        // Copy all subdirectories recursively
        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(directory);
            var targetSubDir = Path.Combine(targetDir, dirName);

            await CopyDirectoryRecursiveAsync(directory, targetSubDir, copiedFiles);
        }
    }
}
