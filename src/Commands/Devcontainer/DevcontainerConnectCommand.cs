using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to connect to an existing devcontainer via VS Code
/// </summary>
public class DevcontainerConnectCommand : DevcontainerCommand<DevcontainerConnectCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevcontainerConnectCommand"/> class
    /// </summary>
    /// <param name="spawnerService">Service for devcontainer operations</param>
    /// <param name="console">Console for output</param>
    public DevcontainerConnectCommand(
        IDevcontainerSpawnerService spawnerService,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
    }

    /// <summary>
    /// Command settings for the connect operation
    /// </summary>
    public class Settings : DevcontainerSettings
    {
        /// <summary>
        /// Optional container ID to connect to directly
        /// </summary>
        [CommandArgument(0, "[CONTAINER_ID]")]
        [Description("Container ID to connect to (optional - will prompt if not provided)")]
        public string? ContainerId { get; set; }

        /// <summary>
        /// Don't automatically launch VS Code after selection
        /// </summary>
        [CommandOption("--no-launch-vscode")]
        [Description("Don't automatically launch VS Code after connecting")]
        public bool NoLaunchVsCode { get; set; }
    }

    /// <inheritdoc/>
    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the connect command asynchronously
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="settings">Command settings</param>
    /// <returns>Exit code (0 for success, non-zero for failure)</returns>
    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Display banner
            DisplayBanner("Connect");

            // Step 1: Check VS Code installation
            VsCodeInstallationInfo? vsCodeInfo = null;
            await WithSpinnerAsync("Checking VS Code installation...", async () =>
            {
                vsCodeInfo = await _spawnerService.CheckVsCodeInstallationAsync();
            });

            if (vsCodeInfo != null && !vsCodeInfo.IsInstalled)
            {
                DisplayError("VS Code Not Installed");
                DisplayWarning("Please install Visual Studio Code to connect to devcontainers");
                DisplayInfo("Download from: https://code.visualstudio.com/");
                return 1;
            }

            DisplaySuccess($"VS Code {vsCodeInfo?.Edition} detected (v{vsCodeInfo?.Version})");
            Console.WriteLine();

            // Step 2: Get container to connect to
            DevcontainerContainerInfo? selectedContainer = null;

            if (!string.IsNullOrEmpty(settings.ContainerId))
            {
                // Container ID provided, get its details
                DisplayInfo($"Looking up container: {settings.ContainerId[..Math.Min(12, settings.ContainerId.Length)]}");

                List<DevcontainerContainerInfo> allContainers = new();
                await WithSpinnerAsync("Fetching container details...", async () =>
                {
                    allContainers = await _spawnerService.ListManagedContainersAsync();
                });

                selectedContainer = allContainers.FirstOrDefault(c =>
                    c.ContainerId.StartsWith(settings.ContainerId, StringComparison.OrdinalIgnoreCase));

                if (selectedContainer == null)
                {
                    DisplayError($"Container not found: {settings.ContainerId}");
                    DisplayWarning("Run 'pks devcontainer list' to see available containers");
                    return 1;
                }
            }
            else
            {
                // No container ID provided, show interactive selection
                List<DevcontainerContainerInfo> containers = new();
                await WithSpinnerAsync("Loading containers...", async () =>
                {
                    containers = await _spawnerService.ListManagedContainersAsync();
                });

                if (!containers.Any())
                {
                    DisplayWarning("No devcontainers found");
                    DisplayInfo("Run 'pks devcontainer spawn' to create a new devcontainer");
                    return 0;
                }

                // Filter to running containers by default, but show all if none are running
                var runningContainers = containers.Where(c => c.Status.Equals("running", StringComparison.OrdinalIgnoreCase)).ToList();
                var containersToShow = runningContainers.Any() ? runningContainers : containers;

                if (!runningContainers.Any() && containers.Any())
                {
                    DisplayWarning("No running containers found. Showing all containers (including stopped).");
                    Console.WriteLine();
                }

                // Create selection prompt with formatted choices
                var selectionPrompt = new SelectionPrompt<DevcontainerContainerInfo>()
                    .Title("[cyan]Select a devcontainer to connect:[/]")
                    .PageSize(10)
                    .MoreChoicesText("[grey](Move up and down to see more containers)[/]")
                    .UseConverter(container =>
                    {
                        var shortId = container.ContainerId[..Math.Min(12, container.ContainerId.Length)];
                        var statusColor = container.Status.Equals("running", StringComparison.OrdinalIgnoreCase) ? "green" : "yellow";
                        var projectName = string.IsNullOrEmpty(container.ProjectName) ? "unknown" : container.ProjectName;

                        return $"{projectName} [{statusColor}]({container.Status})[/] - {shortId}";
                    });

                foreach (var container in containersToShow)
                {
                    selectionPrompt.AddChoice(container);
                }

                selectedContainer = Console.Prompt(selectionPrompt);
            }

            // Display selected container info
            Console.WriteLine();
            DisplayInfo("Selected Container:");
            DisplayProgress($"Project: {selectedContainer.ProjectName}");
            DisplayProgress($"Container ID: {selectedContainer.ContainerId[..12]}");
            DisplayProgress($"Status: {selectedContainer.Status}");
            DisplayProgress($"Volume: {selectedContainer.VolumeName}");
            Console.WriteLine();

            // Step 3: Check if container is running
            if (!selectedContainer.Status.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                DisplayWarning($"Container is not running (status: {selectedContainer.Status})");

                var shouldStart = PromptConfirmation("Would you like to start the container first?", defaultValue: true);

                if (!shouldStart)
                {
                    DisplayWarning("Cannot connect to a stopped container");
                    DisplayInfo("Tip: Start the container with Docker and try again");
                    return 1;
                }

                DisplayError("Auto-start is not yet implemented");
                DisplayInfo("Please start the container manually with:");
                DisplayProgress($"docker start {selectedContainer.ContainerId[..12]}");
                return 1;
            }

            // Step 4: Get workspace folder (default to /workspaces/{projectName})
            var workspaceFolder = string.IsNullOrEmpty(selectedContainer.WorkspaceFolder)
                ? $"/workspaces/{selectedContainer.ProjectName}"
                : selectedContainer.WorkspaceFolder;

            // Step 5: Get VS Code URI
            string? vsCodeUri = null;
            await WithSpinnerAsync("Generating VS Code connection URI...", async () =>
            {
                vsCodeUri = await _spawnerService.GetContainerVsCodeUriAsync(
                    selectedContainer.ContainerId,
                    workspaceFolder);
            });

            if (string.IsNullOrEmpty(vsCodeUri))
            {
                DisplayError("Failed to generate VS Code URI");
                return 1;
            }

            DisplaySuccess("Connection URI generated");
            Console.WriteLine();

            // Step 6: Launch VS Code (unless --no-launch-vscode)
            if (!settings.NoLaunchVsCode && vsCodeInfo != null && vsCodeInfo.ExecutablePath != null)
            {
                DisplayInfo("Launching VS Code...");

                var launched = await LaunchVsCodeAsync(vsCodeUri, vsCodeInfo.ExecutablePath);

                if (launched)
                {
                    var successPanel = new Panel($"""
                        [green]Successfully connected to devcontainer![/]

                        [cyan1]Project:[/] {selectedContainer.ProjectName}
                        [cyan1]Container:[/] {selectedContainer.ContainerId[..12]}
                        [cyan1]Workspace:[/] {workspaceFolder}

                        [dim]VS Code is opening the devcontainer...[/]

                        [bold]Happy coding![/]
                        """)
                        .Border(BoxBorder.Rounded)
                        .BorderStyle("green")
                        .Header(" [bold green]Connected[/] ");

                    Console.Write(successPanel);
                    return 0;
                }
                else
                {
                    DisplayWarning("Failed to launch VS Code automatically");
                    DisplayInfo("You can connect manually using:");
                    DisplayProgress($"code --folder-uri \"{vsCodeUri}\"");
                    return 1;
                }
            }
            else
            {
                DisplaySuccess("Connection details ready");
                Console.WriteLine();

                var infoPanel = new Panel($"""
                    [cyan]To connect manually, run:[/]

                    code --folder-uri "{vsCodeUri}"

                    [dim]Or open VS Code and use the Remote-Containers extension[/]
                    """)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle("cyan")
                    .Header(" [bold cyan]Manual Connection[/] ");

                Console.Write(infoPanel);
                return 0;
            }
        }
        catch (Exception ex)
        {
            DisplayError("Unexpected error");
            Console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Launches VS Code with the specified URI
    /// </summary>
    /// <param name="uri">VS Code remote URI</param>
    /// <param name="vsCodePath">Path to VS Code executable</param>
    /// <returns>True if successfully launched, false otherwise</returns>
    private async Task<bool> LaunchVsCodeAsync(string uri, string vsCodePath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = vsCodePath,
                    Arguments = $"--folder-uri \"{uri}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                }
            };

            process.Start();
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteException(ex);
            return false;
        }
    }
}
