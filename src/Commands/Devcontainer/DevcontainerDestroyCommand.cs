using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to destroy a devcontainer and its associated volumes.
/// Supports both local and remote (SSH) targets.
/// </summary>
public class DevcontainerDestroyCommand : DevcontainerCommand<DevcontainerDestroyCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly ISshTargetConfigurationService? _sshConfigService;
    private readonly ISshCommandRunner? _sshCommandRunner;

    /// <summary>
    /// Initializes a new instance of the DevcontainerDestroyCommand
    /// </summary>
    public DevcontainerDestroyCommand(
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
    /// Settings for the destroy command
    /// </summary>
    public class Settings : DevcontainerSettings
    {
        /// <summary>
        /// Optional container ID to destroy directly (skip selection prompt)
        /// </summary>
        [CommandArgument(0, "[CONTAINER_ID]")]
        [Description("Container ID to destroy (optional - will prompt if not provided)")]
        public string? ContainerId { get; set; }
    }

    /// <inheritdoc/>
    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the destroy command asynchronously
    /// </summary>
    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Destroy");

            // Check for registered SSH targets and prompt
            SshTarget? selectedSshTarget = null;
            RemoteHostConfig? remoteHost = null;

            if (_sshConfigService != null)
            {
                var sshTargets = await _sshConfigService.ListTargetsAsync();
                if (sshTargets.Count > 0)
                {
                    var choices = new List<string> { "Local (this machine)" };
                    choices.AddRange(sshTargets.Select(t =>
                        $"{t.Username}@{t.Host}" + (string.IsNullOrEmpty(t.Label) ? "" : $" ({t.Label})")));

                    var selected = Console.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[cyan]Which host?[/]")
                            .AddChoices(choices));

                    var selectedIndex = choices.IndexOf(selected) - 1;
                    if (selectedIndex >= 0)
                    {
                        selectedSshTarget = sshTargets[selectedIndex];
                        remoteHost = new RemoteHostConfig
                        {
                            Host = selectedSshTarget.Host,
                            Username = selectedSshTarget.Username,
                            Port = selectedSshTarget.Port,
                            KeyPath = selectedSshTarget.KeyPath
                        };
                    }
                }
            }

            if (remoteHost != null)
            {
                return await ExecuteRemoteDestroyAsync(settings, remoteHost);
            }
            else
            {
                return await ExecuteLocalDestroyAsync(settings);
            }
        }
        catch (Exception ex)
        {
            DisplayError("Unexpected error");
            Console.WriteException(ex);
            return 1;
        }
    }

    private async Task<int> ExecuteLocalDestroyAsync(Settings settings)
    {
        // List containers
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

        // Select container
        DevcontainerContainerInfo selectedContainer;

        if (!string.IsNullOrEmpty(settings.ContainerId))
        {
            var match = containers.FirstOrDefault(c =>
                c.ContainerId.StartsWith(settings.ContainerId, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                DisplayError($"Container not found: {settings.ContainerId}");
                DisplayWarning("Run 'pks devcontainer containers --all' to see available containers");
                return 1;
            }

            selectedContainer = match;
        }
        else
        {
            var selectionPrompt = new SelectionPrompt<DevcontainerContainerInfo>()
                .Title("[cyan]Select a devcontainer to destroy:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up and down to see more containers)[/]")
                .UseConverter(container =>
                {
                    var shortId = container.ContainerId[..Math.Min(12, container.ContainerId.Length)];
                    var statusColor = container.Status.Equals("running", StringComparison.OrdinalIgnoreCase) ? "green" : "yellow";
                    var projectName = string.IsNullOrEmpty(container.ProjectName) ? "unknown" : container.ProjectName;
                    return $"{projectName} [{statusColor}]({container.Status})[/] - {shortId}";
                });

            foreach (var container in containers)
            {
                selectionPrompt.AddChoice(container);
            }

            selectedContainer = Console.Prompt(selectionPrompt);
        }

        var containerId = selectedContainer.ContainerId;
        var shortContainerId = containerId[..Math.Min(12, containerId.Length)];
        var projectName = string.IsNullOrEmpty(selectedContainer.ProjectName) ? shortContainerId : selectedContainer.ProjectName;

        // Get container volumes via docker inspect
        var volumes = await GetContainerVolumesLocalAsync(containerId);

        // Confirm destruction
        DisplayWarning($"This will permanently destroy:");
        DisplayProgress($"Container: {projectName} ({shortContainerId})");
        if (volumes.Count > 0)
        {
            DisplayProgress($"Volumes: {volumes.Count} volume(s)");
            foreach (var vol in volumes)
            {
                DisplayProgress($"  - {vol}");
            }
        }
        Console.WriteLine();

        var confirmMessage = volumes.Count > 0
            ? $"Destroy container {projectName} and {volumes.Count} volume(s)?"
            : $"Destroy container {projectName}?";

        if (!settings.Force && !PromptConfirmation(confirmMessage, defaultValue: false))
        {
            DisplayInfo("Destruction cancelled");
            return 0;
        }

        // Remove container
        await WithSpinnerAsync($"Removing container {shortContainerId}...", async () =>
        {
            await RunLocalDockerCommandAsync($"rm -f {containerId}");
        });
        DisplaySuccess($"Container {shortContainerId} removed");

        // Remove volumes
        foreach (var vol in volumes)
        {
            await WithSpinnerAsync($"Removing volume {vol}...", async () =>
            {
                await RunLocalDockerCommandAsync($"volume rm {vol}");
            });
            DisplaySuccess($"Volume {vol} removed");
        }

        Console.WriteLine();
        DisplaySuccess($"Devcontainer '{projectName}' has been destroyed");

        return 0;
    }

    private async Task<int> ExecuteRemoteDestroyAsync(Settings settings, RemoteHostConfig remoteHost)
    {
        if (_sshCommandRunner == null)
        {
            DisplayError("SSH command runner not available");
            return 1;
        }

        // List containers on remote host using JSON format
        SshCommandResult? listResult = null;
        await WithSpinnerAsync($"Retrieving containers from {remoteHost.Username}@{remoteHost.Host}...", async () =>
        {
            listResult = await _sshCommandRunner.RunAsync(remoteHost,
                "docker ps -a --filter label=devcontainer.local_folder --format json");
        });

        if (listResult == null || !listResult.Success)
        {
            DisplayError($"Failed to list remote containers: {listResult?.StdErr ?? "unknown error"}");
            return 1;
        }

        var lines = listResult.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            DisplayWarning("No devcontainers found on remote host");
            return 0;
        }

        // Parse containers from JSON
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

                return new
                {
                    Id = id,
                    Name = name,
                    Status = status,
                    LocalFolder = localFolder,
                    Project = !string.IsNullOrEmpty(pksProject) ? pksProject
                        : !string.IsNullOrEmpty(localFolder) ? Path.GetFileName(localFolder.TrimEnd('/'))
                        : name
                };
            }
            catch { return null; }
        }).Where(c => c != null).Select(c => c!).ToList();

        // Select container
        string selectedId;
        string selectedProject;

        if (!string.IsNullOrEmpty(settings.ContainerId))
        {
            var match = remoteContainers.FirstOrDefault(c =>
                c.Id.StartsWith(settings.ContainerId, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                DisplayError($"Container not found: {settings.ContainerId}");
                return 1;
            }

            selectedId = match.Id;
            selectedProject = match.Project;
        }
        else
        {
            var containerChoices = remoteContainers.Select(c =>
                $"{c.Project} ({c.Status}) - {c.Id[..Math.Min(12, c.Id.Length)]}").ToList();

            var selected = Console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a remote devcontainer to destroy:[/]")
                    .AddChoices(containerChoices));

            var index = containerChoices.IndexOf(selected);
            selectedId = remoteContainers[index].Id;
            selectedProject = remoteContainers[index].Project;
        }

        var shortId = selectedId[..Math.Min(12, selectedId.Length)];

        // Get container volumes via docker inspect on remote
        var volumes = await GetContainerVolumesRemoteAsync(remoteHost, selectedId);

        // Show what will be destroyed in a table
        Console.WriteLine();
        var destroyTable = new Table()
            .Title($"[red]Resources to destroy on {remoteHost.Username}@{remoteHost.Host}[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Red)
            .AddColumn("[yellow]Type[/]")
            .AddColumn("[yellow]Name[/]");

        destroyTable.AddRow("[cyan]Container[/]", $"{selectedProject} ({shortId})");
        foreach (var vol in volumes)
        {
            destroyTable.AddRow("[cyan]Volume[/]", vol.EscapeMarkup());
        }
        destroyTable.AddRow("[cyan]Project files[/]", $"/tmp/pks-devcontainer/{selectedProject}");

        Console.Write(destroyTable);
        Console.WriteLine();

        var confirmMessage = volumes.Count > 0
            ? $"Destroy container and {volumes.Count} volume(s)?"
            : $"Destroy container {selectedProject}?";

        if (!settings.Force && !PromptConfirmation(confirmMessage, defaultValue: false))
        {
            DisplayInfo("Destruction cancelled");
            return 0;
        }

        // Remove container
        await WithSpinnerAsync($"Removing container {shortId}...", async () =>
        {
            await _sshCommandRunner.RunAsync(remoteHost, $"docker rm -f {selectedId}");
        });
        DisplaySuccess($"Container {shortId} removed");

        // Remove volumes
        foreach (var vol in volumes)
        {
            await WithSpinnerAsync($"Removing volume {vol}...", async () =>
            {
                await _sshCommandRunner.RunAsync(remoteHost, $"docker volume rm {vol}");
            });
            DisplaySuccess($"Volume {vol} removed");
        }

        // Clean up project files on remote
        await WithSpinnerAsync("Cleaning up remote project files...", async () =>
        {
            await _sshCommandRunner.RunAsync(remoteHost,
                $"rm -rf /tmp/pks-devcontainer/{selectedProject}");
        });
        DisplaySuccess("Remote project files cleaned up");

        Console.WriteLine();
        DisplaySuccess($"Devcontainer '{selectedProject}' has been destroyed on {remoteHost.Username}@{remoteHost.Host}");

        return 0;
    }

    /// <summary>
    /// Gets volume names for a container by running docker inspect locally
    /// </summary>
    private async Task<List<string>> GetContainerVolumesLocalAsync(string containerId)
    {
        var volumes = new List<string>();
        try
        {
            var result = await RunLocalDockerCommandAsync($"inspect {containerId} --format '{{{{json .Mounts}}}}'");
            if (string.IsNullOrWhiteSpace(result))
                return volumes;

            // Remove surrounding single quotes if present
            var json = result.Trim().Trim('\'');
            return ParseVolumesFromMountsJson(json);
        }
        catch
        {
            return volumes;
        }
    }

    /// <summary>
    /// Gets volume names for a container by running docker inspect on a remote host
    /// </summary>
    private async Task<List<string>> GetContainerVolumesRemoteAsync(RemoteHostConfig remoteHost, string containerId)
    {
        var volumes = new List<string>();
        if (_sshCommandRunner == null) return volumes;

        try
        {
            var result = await _sshCommandRunner.RunAsync(remoteHost,
                $"docker inspect {containerId} --format '{{{{json .Mounts}}}}'");

            if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
                return volumes;

            var json = result.StdOut.Trim().Trim('\'');
            return ParseVolumesFromMountsJson(json);
        }
        catch
        {
            return volumes;
        }
    }

    /// <summary>
    /// Parses volume names from docker inspect Mounts JSON
    /// </summary>
    private static List<string> ParseVolumesFromMountsJson(string json)
    {
        var volumes = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return volumes;

            foreach (var mount in doc.RootElement.EnumerateArray())
            {
                var type = mount.TryGetProperty("Type", out var typeProp)
                    ? typeProp.GetString()
                    : null;

                if (string.Equals(type, "volume", StringComparison.OrdinalIgnoreCase))
                {
                    var name = mount.TryGetProperty("Name", out var nameProp)
                        ? nameProp.GetString()
                        : null;

                    if (!string.IsNullOrEmpty(name))
                    {
                        volumes.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return volumes;
    }

    /// <summary>
    /// Runs a docker command locally and returns stdout
    /// </summary>
    private static async Task<string> RunLocalDockerCommandAsync(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout.Trim();
    }
}
