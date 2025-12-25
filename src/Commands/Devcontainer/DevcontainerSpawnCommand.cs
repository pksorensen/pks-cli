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

                    var shouldContinue = PromptConfirmation(
                        "Create a new container anyway?",
                        defaultValue: false);

                    if (!shouldContinue)
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
            await WithSpinnerAsync("Spawning devcontainer...", async () =>
            {
                var options = new DevcontainerSpawnOptions
                {
                    ProjectName = projectName,
                    ProjectPath = projectPath,
                    DevcontainerPath = devcontainerPath,
                    VolumeName = confirmedVolumeName,
                    CopySourceFiles = !settings.NoCopySource,
                    LaunchVsCode = !settings.NoLaunchVsCode,
                    ReuseExisting = !settings.Force
                };

                result = await _spawnerService.SpawnLocalAsync(options);
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
