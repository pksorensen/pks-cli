using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to spawn a devcontainer in a Docker volume for an existing project
/// </summary>
public class DevcontainerSpawnCommand : DevcontainerCommand<DevcontainerSpawnCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;

    public DevcontainerSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
    }

    public class Settings : DevcontainerSettings
    {
        [CommandArgument(0, "[PROJECT_PATH]")]
        [Description("Path to project directory (defaults to current directory)")]
        public string? ProjectPath { get; set; }

        [CommandOption("--volume-name <NAME>")]
        [Description("Custom volume name for the devcontainer")]
        public string? VolumeName { get; set; }

        [CommandOption("--no-launch-vscode")]
        [Description("Don't automatically launch VS Code after spawning")]
        public bool NoLaunchVsCode { get; set; }

        [CommandOption("--no-copy-source")]
        [Description("Don't copy source files (only .devcontainer configuration)")]
        public bool NoCopySource { get; set; }

        [CommandOption("--no-bootstrap")]
        [Description("Use direct execution instead of bootstrap container (advanced)")]
        public bool NoBootstrap { get; set; }

        [CommandOption("--forward-docker-config")]
        [Description("Forward Docker credentials from host to devcontainer (default: true, matches VS Code behavior)")]
        public bool? ForwardDockerConfig { get; set; }

        [CommandOption("--no-forward-docker-config")]
        [Description("Disable Docker credential forwarding (use when you don't want host Docker credentials in container)")]
        public bool NoForwardDockerConfig { get; set; }

        [CommandOption("--docker-config-path <PATH>")]
        [Description("Path to Docker config.json to forward (defaults to ~/.docker/config.json)")]
        public string? DockerConfigPath { get; set; }

        [CommandOption("--rebuild")]
        [Description("Force rebuild even if no configuration changes detected")]
        public bool ForceRebuild { get; set; }

        [CommandOption("--no-rebuild")]
        [Description("Skip rebuild even if configuration changes detected")]
        public bool NoRebuild { get; set; }

        [CommandOption("--auto-rebuild")]
        [Description("Automatically rebuild without prompting when configuration changes detected")]
        public bool AutoRebuild { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Display banner
            DisplayBanner("Spawn");

            // 1. Determine project path
            var projectPath = settings.ProjectPath ?? Directory.GetCurrentDirectory();
            projectPath = Path.GetFullPath(projectPath);

            if (!Directory.Exists(projectPath))
            {
                DisplayError($"Project path does not exist: {projectPath}");
                return 1;
            }

            // 2. Check for devcontainer config
            var devcontainerPath = Path.Combine(projectPath, ".devcontainer");
            var devcontainerJsonPath = Path.Combine(devcontainerPath, "devcontainer.json");

            if (!File.Exists(devcontainerJsonPath))
            {
                DisplayError($"No devcontainer.json found in {devcontainerPath}");
                DisplayWarning("Run 'pks devcontainer init' to create a devcontainer configuration");
                return 1;
            }

            // 3. Extract project name from path
            var projectName = Path.GetFileName(projectPath);

            DisplayInfo($"Project: {projectName}");
            DisplayInfo($"Path: {projectPath}");
            Console.WriteLine();

            // 4. Check for existing container
            if (!settings.Force)
            {
                var existing = await _spawnerService.FindExistingContainerAsync(projectPath);
                if (existing != null)
                {
                    DisplayWarning("Existing container found");
                    DisplayProgress($"Container ID: {existing.ContainerId[..12]}");
                    DisplayProgress($"Volume: {existing.VolumeName}");
                    DisplayProgress($"Status: {(existing.IsRunning ? "Running" : "Stopped")}");
                    Console.WriteLine();

                    // Perform three-way hash detection (host, container label, volume)
                    string? labelHash = null;
                    string? hostHash = null;
                    string? volumeHash = null;

                    await WithSpinnerAsync("Detecting configuration changes...", async () =>
                    {
                        // Get hash from container label (what it was built with)
                        labelHash = await _spawnerService.GetContainerLabelAsync(
                            existing.ContainerId, "devcontainer.config.hash");

                        // Compute hash from host files
                        var devcontainerPath = Path.Combine(projectPath, ".devcontainer");
                        if (Directory.Exists(devcontainerPath))
                        {
                            try
                            {
                                var hashResult = await _spawnerService.ComputeConfigurationHashAsync(
                                    projectPath, devcontainerPath);
                                hostHash = hashResult;
                            }
                            catch (Exception ex)
                            {
                                DisplayWarning($"Failed to compute host hash: {ex.Message}");
                            }
                        }

                        // Compute hash from volume files (what's in the container)
                        try
                        {
                            volumeHash = await _spawnerService.ComputeVolumeHashAsync(
                                existing.VolumeName, projectName);
                        }
                        catch (Exception ex)
                        {
                            DisplayWarning($"Failed to compute volume hash: {ex.Message}");
                        }
                    });

                    // Analyze hash differences
                    bool hostChanged = hostHash != null && labelHash != null && hostHash != labelHash;
                    bool volumeChanged = volumeHash != null && labelHash != null && volumeHash != labelHash;
                    bool hostVolumeMatch = hostHash != null && volumeHash != null && hostHash == volumeHash;

                    // Handle volume changes (edited inside container)
                    if (volumeChanged && !hostChanged)
                    {
                        DisplayWarning("Configuration changes detected inside container!");
                        DisplayInfo($"Container was built with hash: {labelHash?[..16]}...");
                        DisplayInfo($"Volume now has hash: {volumeHash?[..16]}...");
                        Console.WriteLine();

                        var choice = PromptSelection(
                            "How should we handle these changes?",
                            new[] {
                                "Sync to host and rebuild (recommended)",
                                "Discard container changes (revert to host)",
                                "Cancel"
                            });

                        if (choice == "Sync to host and rebuild (recommended)") // Sync to host and rebuild
                        {
                            DisplayInfo("Syncing .devcontainer from volume to host...");
                            var synced = await WithSpinnerAsync("Syncing files...",
                                async () => await _spawnerService.SyncVolumeToHostAsync(
                                    existing.VolumeName, projectName, projectPath));

                            if (!synced)
                            {
                                DisplayError("Failed to sync files from volume to host");
                                return 1;
                            }

                            DisplaySuccess("Files synced successfully");
                            DisplayInfo("Now rebuilding container with updated configuration...");
                            // Fall through to rebuild (host files now changed)
                        }
                        else if (choice == "Discard container changes (revert to host)") // Discard container changes
                        {
                            DisplayInfo("Container changes will be discarded on rebuild");
                            // Continue with normal flow - host hash will be used
                        }
                        else // Cancel
                        {
                            DisplayWarning("Spawn cancelled");
                            return 0;
                        }
                    }
                    // Handle both host and volume changed (conflict)
                    else if (volumeChanged && hostChanged && !hostVolumeMatch)
                    {
                        DisplayWarning("Configuration changed BOTH on host AND inside container!");
                        DisplayInfo($"Container was built with: {labelHash?[..16]}...");
                        DisplayInfo($"Host now has: {hostHash?[..16]}...");
                        DisplayInfo($"Volume now has: {volumeHash?[..16]}...");
                        Console.WriteLine();

                        var choice = PromptSelection(
                            "Conflict resolution:",
                            new[] {
                                "Use host version (discard container edits)",
                                "Use container version (sync to host and rebuild)",
                                "Cancel and resolve manually"
                            });

                        if (choice == "Use host version (discard container edits)") // Use host
                        {
                            DisplayInfo("Using host configuration, container edits will be discarded");
                            // Continue with normal flow
                        }
                        else if (choice == "Use container version (sync to host and rebuild)") // Use container
                        {
                            DisplayInfo("Syncing .devcontainer from volume to host...");
                            var synced = await WithSpinnerAsync("Syncing files...",
                                async () => await _spawnerService.SyncVolumeToHostAsync(
                                    existing.VolumeName, projectName, projectPath));

                            if (!synced)
                            {
                                DisplayError("Failed to sync files from volume to host");
                                return 1;
                            }

                            DisplaySuccess("Files synced successfully");
                        }
                        else // Cancel
                        {
                            DisplayWarning("Spawn cancelled - please resolve conflicts manually");
                            return 0;
                        }
                    }

                    // Offer to connect to existing container
                    var shouldConnect = PromptConfirmation(
                        "Connect to existing container?",
                        defaultValue: true);

                    if (shouldConnect)
                    {
                        // Start container if stopped
                        if (!existing.IsRunning)
                        {
                            DisplayInfo("Starting container...");
                            await WithSpinnerAsync("Starting container...", async () =>
                            {
                                await _spawnerService.StartContainerAsync(existing.ContainerId);
                            });
                        }

                        // Launch VS Code if not disabled
                        if (!settings.NoLaunchVsCode)
                        {
                            DisplayInfo("Launching VS Code...");
                            var workspaceFolder = $"/workspaces/{projectName}";
                            var vsCodeUri = await _spawnerService.GetContainerVsCodeUriAsync(
                                existing.ContainerId,
                                workspaceFolder);

                            await _spawnerService.LaunchVsCodeAsync(vsCodeUri);
                        }

                        DisplaySuccess("Connected to existing container");
                        Console.WriteLine();

                        var successPanel = new Panel($"""
                            [green]Connected to existing devcontainer![/]

                            [cyan1]Container ID:[/] {existing.ContainerId[..12]}
                            [cyan1]Volume Name:[/] {existing.VolumeName}
                            [cyan1]Workspace:[/] /workspaces/{projectName}

                            {(!settings.NoLaunchVsCode ? "[dim]VS Code is opening...[/]" : "[dim]Container is ready[/]")}

                            [bold]Ready for development![/]
                            """)
                            .Border(BoxBorder.Rounded)
                            .BorderStyle("green")
                            .Header(" [bold green]Success[/] ");

                        Console.Write(successPanel);
                        return 0;
                    }

                    // User doesn't want to connect, ask if they want to create new
                    var shouldCreateNew = PromptConfirmation(
                        "Create a new container anyway?",
                        defaultValue: false);

                    if (!shouldCreateNew)
                    {
                        DisplayWarning("Spawn cancelled");
                        return 0;
                    }
                }
            }

            // 5. Pre-flight checks
            DisplayInfo("Running pre-flight checks...");

            DockerAvailabilityResult? dockerCheck = null;
            bool? cliInstalled = null;

            await WithSpinnerAsync("Checking Docker and devcontainer CLI...", async () =>
            {
                dockerCheck = await _spawnerService.CheckDockerAvailabilityAsync();
                if (!dockerCheck.IsAvailable)
                {
                    return;
                }

                cliInstalled = await _spawnerService.IsDevcontainerCliInstalledAsync();
            });

            if (dockerCheck != null && !dockerCheck.IsAvailable)
            {
                DisplayError("Docker Not Available");
                DisplayWarning(dockerCheck.Message);
                return 1;
            }

            if (cliInstalled == false)
            {
                DisplayError("devcontainer CLI Not Found");
                DisplayWarning("Install: npm install -g @devcontainers/cli");
                return 1;
            }

            DisplaySuccess("Pre-flight checks passed");
            Console.WriteLine();

            // 6. Generate and confirm volume name
            string confirmedVolumeName;
            if (!string.IsNullOrEmpty(settings.VolumeName))
            {
                // Volume name provided via command-line, use it directly
                confirmedVolumeName = settings.VolumeName;
                DisplayInfo($"Docker volume name: {confirmedVolumeName}");
            }
            else
            {
                // Interactive prompt for volume name
                var volumeName = _spawnerService.GenerateVolumeName(projectName);
                confirmedVolumeName = PromptText(
                    "Docker volume name:",
                    defaultValue: volumeName);

                if (string.IsNullOrWhiteSpace(confirmedVolumeName))
                    confirmedVolumeName = volumeName;
            }

            Console.WriteLine();

            // 7. Spawn devcontainer
            DevcontainerSpawnResult? result = null;
            await WithSpinnerAsync("Spawning devcontainer...", async (ctx) =>
            {
                // Determine Docker credential forwarding behavior
                // Priority: --no-forward-docker-config > --forward-docker-config > default (true)
                bool forwardDockerConfig = true; // Default matches VS Code behavior
                if (settings.NoForwardDockerConfig)
                {
                    forwardDockerConfig = false;
                }
                else if (settings.ForwardDockerConfig.HasValue)
                {
                    forwardDockerConfig = settings.ForwardDockerConfig.Value;
                }

                // Determine rebuild behavior based on flags
                // Priority: --rebuild (Always) > --no-rebuild (Never) > --auto-rebuild (Auto) > default (Auto)
                var rebuildBehavior = RebuildBehavior.Auto;
                if (settings.ForceRebuild)
                {
                    rebuildBehavior = RebuildBehavior.Always;
                }
                else if (settings.NoRebuild)
                {
                    rebuildBehavior = RebuildBehavior.Never;
                }
                else if (settings.AutoRebuild)
                {
                    rebuildBehavior = RebuildBehavior.Auto;
                }

                var options = new DevcontainerSpawnOptions
                {
                    ProjectName = projectName,
                    ProjectPath = projectPath,
                    DevcontainerPath = devcontainerPath,
                    VolumeName = confirmedVolumeName,
                    CopySourceFiles = !settings.NoCopySource,
                    LaunchVsCode = !settings.NoLaunchVsCode,
                    ReuseExisting = !settings.Force,
                    UseBootstrapContainer = !settings.NoBootstrap,
                    ForwardDockerConfig = forwardDockerConfig,
                    DockerConfigPath = settings.DockerConfigPath,
                    RebuildBehavior = rebuildBehavior,
                    SkipRebuild = settings.NoRebuild
                };

                result = await _spawnerService.SpawnLocalAsync(
                    options,
                    onProgress: message => ctx.Status(message));
            });

            // 8. Display result
            Console.WriteLine();
            if (result != null && result.Success)
            {
                var successPanel = new Panel($"""
                    [green]Devcontainer spawned successfully![/]

                    [cyan1]Container ID:[/] {result.ContainerId?[..12]}
                    [cyan1]Volume Name:[/] {result.VolumeName}
                    [cyan1]Workspace:[/] /workspaces/{projectName}

                    {(!settings.NoLaunchVsCode ? "[dim]VS Code is opening...[/]" : "[dim]Connect manually with VS Code Dev Containers extension[/]")}

                    [bold]Devcontainer ready for development![/]
                    """)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle("green")
                    .Header(" [bold green]Success[/] ");

                Console.Write(successPanel);
                return 0;
            }
            else if (result != null)
            {
                DisplayError("Failed to spawn devcontainer");
                DisplayWarning(result.Message);

                // Display detailed CLI output if available
                if (!string.IsNullOrWhiteSpace(result.DevcontainerCliOutput) ||
                    !string.IsNullOrWhiteSpace(result.DevcontainerCliStderr))
                {
                    Console.WriteLine();
                    var cliOutputPanel = new Panel(new Markup(
                        "[bold yellow]devcontainer CLI Output:[/]\n\n" +
                        (string.IsNullOrWhiteSpace(result.DevcontainerCliOutput)
                            ? ""
                            : $"[bold cyan]STDOUT:[/]\n{result.DevcontainerCliOutput.EscapeMarkup()}\n\n") +
                        (string.IsNullOrWhiteSpace(result.DevcontainerCliStderr)
                            ? ""
                            : $"[bold red]STDERR:[/]\n{result.DevcontainerCliStderr.EscapeMarkup()}")))
                        .Border(BoxBorder.Rounded)
                        .BorderStyle("red")
                        .Header(" [bold red]Diagnostics[/] ");

                    Console.Write(cliOutputPanel);
                }

                if (result.Errors.Any())
                {
                    Console.WriteLine();
                    DisplayError("Errors:");
                    foreach (var error in result.Errors)
                        Console.MarkupLine($"  [red]• {error.EscapeMarkup()}[/]");
                }

                return 1;
            }

            return 1;
        }
        catch (Exception ex)
        {
            DisplayError("Unexpected error");
            Console.WriteException(ex);
            return 1;
        }
    }
}
