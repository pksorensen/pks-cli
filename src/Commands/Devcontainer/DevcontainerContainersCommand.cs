using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to list all managed devcontainers with their status and details.
/// Supports both local and remote (SSH) targets.
/// </summary>
public class DevcontainerContainersCommand : DevcontainerCommand<DevcontainerContainersCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly ISshTargetConfigurationService? _sshConfigService;
    private readonly ISshCommandRunner? _sshCommandRunner;

    /// <summary>
    /// Initializes a new instance of the DevcontainerContainersCommand
    /// </summary>
    public DevcontainerContainersCommand(
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
    /// Settings for the containers command
    /// </summary>
    public class Settings : DevcontainerSettings
    {
        /// <summary>
        /// Show all containers including stopped ones (default: only running)
        /// </summary>
        [CommandOption("--all|-a")]
        [Description("Show all containers (not just running)")]
        public bool ShowAll { get; set; }

        /// <summary>
        /// Output format for the container list
        /// </summary>
        [CommandOption("--format <FORMAT>")]
        [Description("Output format: table or json (default: table)")]
        [DefaultValue("table")]
        public string Format { get; set; } = "table";
    }

    /// <inheritdoc/>
    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the containers command asynchronously
    /// </summary>
    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Containers");

            // Validate format option
            var format = settings.Format.ToLowerInvariant();
            if (format != "table" && format != "json")
            {
                DisplayError($"Invalid format '{settings.Format}'. Valid formats are: table, json");
                return 1;
            }

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

            List<ContainerDisplayInfo> containers;

            if (remoteHost != null)
            {
                containers = await GetRemoteContainersAsync(remoteHost);
                // Fetch volumes for each container
                if (containers.Count > 0)
                {
                    await WithSpinnerAsync("Fetching volume info...", async () =>
                    {
                        foreach (var container in containers)
                        {
                            var inspectResult = await _sshCommandRunner!.RunAsync(remoteHost,
                                $"docker inspect {container.ContainerId} --format json");
                            if (inspectResult.Success)
                            {
                                try
                                {
                                    var inspectJson = System.Text.Json.JsonDocument.Parse(inspectResult.StdOut.Trim().TrimStart('[').TrimEnd(']'));
                                    if (inspectJson.RootElement.TryGetProperty("Mounts", out var mounts))
                                    {
                                        foreach (var mount in mounts.EnumerateArray())
                                        {
                                            if (mount.TryGetProperty("Type", out var typeEl) && typeEl.GetString() == "volume"
                                                && mount.TryGetProperty("Name", out var nameEl))
                                            {
                                                container.Volumes.Add(nameEl.GetString() ?? "");
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    });
                }
            }
            else
            {
                containers = await GetLocalContainersAsync();
            }

            // Filter containers based on --all flag
            var filteredContainers = settings.ShowAll
                ? containers
                : containers.Where(c => c.Status.Contains("Up", StringComparison.OrdinalIgnoreCase)
                    || c.Status.Equals("running", StringComparison.OrdinalIgnoreCase)).ToList();

            // Handle empty results
            if (!filteredContainers.Any())
            {
                if (format == "json")
                {
                    System.Console.WriteLine("[]");
                }
                else
                {
                    Console.WriteLine();
                    DisplayInfo("No managed devcontainers found");
                    Console.WriteLine();

                    var helpPanel = new Panel(
                        settings.ShowAll
                            ? "[dim]No devcontainers have been spawned yet.[/]\n\nCreate one with: [cyan]pks devcontainer spawn[/]"
                            : "[dim]No running devcontainers found.[/]\n\nUse [cyan]--all[/] to show stopped containers,\nor spawn a new one with: [cyan]pks devcontainer spawn[/]")
                        .Border(BoxBorder.Rounded)
                        .BorderStyle("yellow")
                        .Header(" [yellow]Tip[/] ");

                    Console.Write(helpPanel);
                }
                return 0;
            }

            // Display results based on format
            if (format == "json")
            {
                DisplayJsonOutput(filteredContainers);
            }
            else
            {
                DisplayTableOutput(filteredContainers, settings.ShowAll, remoteHost);
            }

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError("Unexpected error");
            Console.WriteException(ex);
            return 1;
        }
    }

    private async Task<List<ContainerDisplayInfo>> GetLocalContainersAsync()
    {
        var containers = new List<ContainerDisplayInfo>();

        List<DevcontainerContainerInfo>? managed = null;
        await WithSpinnerAsync("Retrieving managed containers...", async () =>
        {
            managed = await _spawnerService.ListManagedContainersAsync();
        });

        if (managed == null)
            return containers;

        foreach (var c in managed)
        {
            containers.Add(new ContainerDisplayInfo
            {
                ProjectName = c.ProjectName,
                ContainerId = c.ContainerId,
                Status = c.Status,
                Created = c.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                Image = c.Labels.TryGetValue("devcontainer.metadata", out var _) ? "(devcontainer)" : ""
            });
        }

        return containers;
    }

    private async Task<List<ContainerDisplayInfo>> GetRemoteContainersAsync(RemoteHostConfig remoteHost)
    {
        var containers = new List<ContainerDisplayInfo>();

        if (_sshCommandRunner == null)
        {
            DisplayError("SSH command runner not available");
            return containers;
        }

        SshCommandResult? result = null;
        await WithSpinnerAsync($"Retrieving containers from {remoteHost.Username}@{remoteHost.Host}...", async () =>
        {
            // Use JSON format to avoid shell escaping issues with Go template labels
            result = await _sshCommandRunner.RunAsync(remoteHost,
                "docker ps -a --filter label=devcontainer.local_folder --format json");
        });

        if (result == null || !result.Success)
        {
            DisplayError($"Failed to list remote containers: {result?.StdErr ?? "unknown error"}");
            return containers;
        }

        var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            try
            {
                var json = System.Text.Json.JsonDocument.Parse(line);
                var root = json.RootElement;

                var id = root.TryGetProperty("ID", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("Names", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var status = root.TryGetProperty("Status", out var statusEl) ? statusEl.GetString() ?? "" : "";
                var image = root.TryGetProperty("Image", out var imageEl) ? imageEl.GetString() ?? "" : "";
                var createdAt = root.TryGetProperty("CreatedAt", out var createdEl) ? createdEl.GetString() ?? "" : "";

                // Get labels for project name
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

                var projectName = !string.IsNullOrEmpty(pksProject)
                    ? pksProject
                    : !string.IsNullOrEmpty(localFolder)
                        ? Path.GetFileName(localFolder.TrimEnd('/'))
                        : name;

                containers.Add(new ContainerDisplayInfo
                {
                    ProjectName = projectName,
                    ContainerId = id,
                    Status = status,
                    Created = createdAt,
                    Image = image
                });
            }
            catch
            {
                // Skip unparseable lines
            }
        }

        return containers;
    }

    private void DisplayTableOutput(List<ContainerDisplayInfo> containers, bool showAll, RemoteHostConfig? remoteHost)
    {
        Console.WriteLine();

        var hostLabel = remoteHost != null
            ? $" on {remoteHost.Username}@{remoteHost.Host}"
            : "";

        var table = new Table()
            .Title($"[cyan]Managed Devcontainers{hostLabel}[/] [dim]({containers.Count} {(showAll ? "total" : "running")})[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1);

        table.AddColumn(new TableColumn("[yellow]Project[/]").LeftAligned());
        table.AddColumn(new TableColumn("[yellow]Container ID[/]").LeftAligned());
        table.AddColumn(new TableColumn("[yellow]Status[/]").Centered());
        table.AddColumn(new TableColumn("[yellow]Volumes[/]").Centered());
        table.AddColumn(new TableColumn("[yellow]Created[/]").RightAligned());
        table.AddColumn(new TableColumn("[yellow]Image[/]").LeftAligned());

        foreach (var container in containers)
        {
            var projectName = string.IsNullOrEmpty(container.ProjectName)
                ? "[dim]<unknown>[/]"
                : container.ProjectName;

            var containerId = container.ContainerId.Length > 12
                ? container.ContainerId[..12]
                : container.ContainerId;

            var isRunning = container.Status.Contains("Up", StringComparison.OrdinalIgnoreCase)
                || container.Status.Equals("running", StringComparison.OrdinalIgnoreCase);

            var status = isRunning
                ? $"[green]{container.Status.EscapeMarkup()}[/]"
                : $"[yellow]{container.Status.EscapeMarkup()}[/]";

            var image = string.IsNullOrEmpty(container.Image)
                ? "[dim]<unknown>[/]"
                : $"[dim]{container.Image.EscapeMarkup()}[/]";

            var volumeDisplay = container.Volumes.Count > 0
                ? $"[dim]{container.Volumes.Count}[/]"
                : "[dim]-[/]";

            table.AddRow(
                projectName,
                $"[cyan]{containerId}[/]",
                status,
                volumeDisplay,
                $"[dim]{container.Created.EscapeMarkup()}[/]",
                image
            );
        }

        Console.Write(table);
        Console.WriteLine();
    }

    private void DisplayJsonOutput(List<ContainerDisplayInfo> containers)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        var json = JsonSerializer.Serialize(containers, options);
        System.Console.WriteLine(json);
    }

    /// <summary>
    /// Container information for display purposes
    /// </summary>
    private class ContainerDisplayInfo
    {
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        [JsonPropertyName("containerId")]
        public string ContainerId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("created")]
        public string Created { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("volumes")]
        public List<string> Volumes { get; set; } = new();
    }
}
