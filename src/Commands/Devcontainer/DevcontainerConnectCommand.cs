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
    private readonly ISshTargetConfigurationService? _sshConfigService;
    private readonly ISshCommandRunner? _sshCommandRunner;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevcontainerConnectCommand"/> class
    /// </summary>
    /// <param name="spawnerService">Service for devcontainer operations</param>
    /// <param name="console">Console for output</param>
    /// <param name="sshConfigService">Optional SSH target configuration service</param>
    /// <param name="sshCommandRunner">Optional SSH command runner</param>
    public DevcontainerConnectCommand(
        IDevcontainerSpawnerService spawnerService,
        IAnsiConsole console,
        ISshTargetConfigurationService? sshConfigService = null,
        ISshCommandRunner? sshCommandRunner = null)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshConfigService = sshConfigService;
        _sshCommandRunner = sshCommandRunner;
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

        /// <summary>
        /// Connect to a devcontainer on a remote SSH target
        /// </summary>
        [CommandOption("--remote <TARGET>")]
        [Description("Connect to a devcontainer on a remote SSH target (host, label, or user@host)")]
        public string? RemoteTarget { get; set; }
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

            // Handle remote connection if --remote is specified
            if (!string.IsNullOrEmpty(settings.RemoteTarget))
            {
                return await ExecuteRemoteConnectAsync(settings);
            }

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
    /// Handles connecting to a devcontainer on a remote SSH target
    /// </summary>
    private async Task<int> ExecuteRemoteConnectAsync(Settings settings)
    {
        if (_sshConfigService == null || _sshCommandRunner == null)
        {
            DisplayError("SSH support not available");
            return 1;
        }

        // Find the SSH target
        var sshTarget = await _sshConfigService.FindTargetAsync(settings.RemoteTarget!);
        if (sshTarget == null)
        {
            DisplayError($"SSH target not found: {settings.RemoteTarget}");
            DisplayWarning("Run 'pks ssh list' to see registered targets");
            return 1;
        }

        DisplayInfo($"Connecting to {sshTarget.Username}@{sshTarget.Host}...");

        // Test connectivity
        var connected = await _sshCommandRunner.TestConnectivityAsync(
            new RemoteHostConfig
            {
                Host = sshTarget.Host,
                Username = sshTarget.Username,
                Port = sshTarget.Port,
                KeyPath = sshTarget.KeyPath
            });

        if (!connected)
        {
            DisplayError($"Cannot connect to {sshTarget.Username}@{sshTarget.Host}");
            return 1;
        }

        var remoteHost = new RemoteHostConfig
        {
            Host = sshTarget.Host,
            Username = sshTarget.Username,
            Port = sshTarget.Port,
            KeyPath = sshTarget.KeyPath
        };

        // List devcontainers on remote host using JSON format (avoids shell escaping issues)
        DisplayInfo("Listing devcontainers on remote host...");
        var containerResult = await _sshCommandRunner.RunAsync(remoteHost,
            "docker ps --filter label=devcontainer.local_folder --format json");

        if (!containerResult.Success)
        {
            DisplayError($"Failed to list remote containers: {containerResult.StdErr}");
            return 1;
        }

        var lines = containerResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            DisplayWarning("No devcontainers found on remote host");
            DisplayInfo("SSH to the remote host and run 'pks init' to create a devcontainer");
            return 0;
        }

        // Parse container JSON
        var remoteContainers = lines.Select(line =>
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(line);
                var root = json.RootElement;
                var id = root.TryGetProperty("ID", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("Names", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var status = root.TryGetProperty("Status", out var statusEl) ? statusEl.GetString() ?? "" : "";
                var labels = root.TryGetProperty("Labels", out var labelsEl) ? labelsEl.GetString() ?? "" : "";

                var pksProject = "";
                var localFolder = "";
                foreach (var label in labels.Split(','))
                {
                    var kv = label.Split('=', 2);
                    if (kv.Length == 2)
                    {
                        if (kv[0].Trim() == "pks.project") pksProject = kv[1].Trim();
                        if (kv[0].Trim() == "devcontainer.local_folder") localFolder = kv[1].Trim();
                    }
                }

                var project = !string.IsNullOrEmpty(pksProject) ? pksProject
                    : !string.IsNullOrEmpty(localFolder) ? Path.GetFileName(localFolder.TrimEnd('/'))
                    : name;

                return new { Id = id, Name = name, Status = status, Project = project };
            }
            catch { return null; }
        }).Where(c => c != null).Select(c => c!).ToList();

        // Select container
        string selectedProject;
        if (remoteContainers.Count == 1)
        {
            var c = remoteContainers[0];
            selectedProject = !string.IsNullOrEmpty(c.Project) ? c.Project : c.Name;
            DisplayInfo($"Found container: {selectedProject} ({c.Status})");
        }
        else
        {
            var choices = remoteContainers.Select(c =>
                $"{(!string.IsNullOrEmpty(c.Project) ? c.Project : c.Name)} ({c.Status})").ToList();

            var selected = Console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a remote devcontainer to connect:[/]")
                    .AddChoices(choices));

            var index = choices.IndexOf(selected);
            var c = remoteContainers[index];
            selectedProject = !string.IsNullOrEmpty(c.Project) ? c.Project : c.Name;
        }

        // Construct VS Code SSH remote URI
        var vsCodeUri = $"vscode-remote://ssh-remote+{sshTarget.Username}@{sshTarget.Host}/workspaces/{selectedProject}";

        // Check VS Code and launch
        VsCodeInstallationInfo? vsCodeInfo = null;
        await WithSpinnerAsync("Checking VS Code installation...", async () =>
        {
            vsCodeInfo = await _spawnerService.CheckVsCodeInstallationAsync();
        });

        if (!settings.NoLaunchVsCode && vsCodeInfo?.IsInstalled == true && vsCodeInfo.ExecutablePath != null)
        {
            DisplayInfo("Launching VS Code with SSH remote...");
            var launched = await LaunchVsCodeAsync(vsCodeUri, vsCodeInfo.ExecutablePath);

            if (launched)
            {
                var successPanel = new Panel($"""
                    [green]Connecting to remote devcontainer![/]

                    [cyan1]Host:[/] {sshTarget.Username}@{sshTarget.Host}
                    [cyan1]Project:[/] {selectedProject}
                    [cyan1]Workspace:[/] /workspaces/{selectedProject}

                    [dim]VS Code is opening the remote devcontainer via SSH...[/]

                    [bold]Happy coding![/]
                    """)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle("green")
                    .Header(" [bold green]Remote Connected[/] ");

                Console.Write(successPanel);
                return 0;
            }
        }

        // Fallback: show manual connection command
        DisplaySuccess("Connection details ready");
        Console.WriteLine();
        var infoPanel = new Panel($"""
            [cyan]To connect manually, run:[/]

            code --folder-uri "{vsCodeUri}"

            [dim]Requires VS Code with Remote - SSH extension installed[/]
            """)
            .Border(BoxBorder.Rounded)
            .BorderStyle("cyan")
            .Header(" [bold cyan]Remote Connection[/] ");

        Console.Write(infoPanel);
        return 0;
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
