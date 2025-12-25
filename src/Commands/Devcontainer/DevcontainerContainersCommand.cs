using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to list all managed devcontainers with their status and details
/// </summary>
/// <remarks>
/// Displays information about devcontainers managed by PKS CLI including:
/// - Project name associated with the container
/// - Container ID (shortened for readability)
/// - Current status (running/stopped) with color coding
/// - Volume name used by the container
/// - Creation timestamp
///
/// Supports multiple output formats:
/// - Table format (default): Rich formatted table with colors
/// - JSON format: Machine-readable JSON output for scripting
///
/// Usage examples:
/// <code>
/// pks devcontainer containers                    # Show running containers in table format
/// pks devcontainer containers --all              # Show all containers (including stopped)
/// pks devcontainer containers --format json      # Output in JSON format
/// pks devcontainer containers --all --format json # All containers in JSON format
/// </code>
/// </remarks>
public class DevcontainerContainersCommand : DevcontainerCommand<DevcontainerContainersCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;

    /// <summary>
    /// Initializes a new instance of the DevcontainerContainersCommand
    /// </summary>
    /// <param name="spawnerService">Service for managing devcontainer operations</param>
    /// <param name="console">Spectre.Console instance for output rendering</param>
    /// <exception cref="ArgumentNullException">Thrown when spawnerService is null</exception>
    public DevcontainerContainersCommand(
        IDevcontainerSpawnerService spawnerService,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
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

    /// <summary>
    /// Executes the containers command synchronously (delegates to async implementation)
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="settings">Command settings</param>
    /// <returns>Exit code: 0 for success, 1 for failure</returns>
    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Executes the containers command asynchronously
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="settings">Command settings</param>
    /// <returns>Exit code: 0 for success, 1 for failure</returns>
    /// <remarks>
    /// Operation flow:
    /// 1. Display command banner
    /// 2. Retrieve list of managed containers from service
    /// 3. Filter containers based on --all flag (running vs all)
    /// 4. Render output in requested format (table or JSON)
    /// 5. Display helpful message if no containers found
    /// </remarks>
    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Display banner
            DisplayBanner("Containers");

            // Validate format option
            var format = settings.Format.ToLowerInvariant();
            if (format != "table" && format != "json")
            {
                DisplayError($"Invalid format '{settings.Format}'. Valid formats are: table, json");
                return 1;
            }

            // Retrieve containers with spinner
            List<ContainerInfo>? containers = null;
            await WithSpinnerAsync("Retrieving managed containers...", async () =>
            {
                // Get volume information
                var volumes = await _spawnerService.ListManagedVolumesAsync();

                // Convert volumes to container info
                // Note: This is a simplified version. The actual implementation would need
                // to query Docker to get container status for each volume
                containers = await GetContainerInfoFromVolumesAsync(volumes);
            });

            if (containers == null)
            {
                DisplayError("Failed to retrieve container list");
                return 1;
            }

            // Filter containers based on --all flag
            var filteredContainers = settings.ShowAll
                ? containers
                : containers.Where(c => c.IsRunning).ToList();

            // Handle empty results
            if (!filteredContainers.Any())
            {
                if (format == "json")
                {
                    // Output empty JSON array
                    System.Console.WriteLine("[]");
                }
                else
                {
                    System.Console.WriteLine();
                    DisplayInfo("No managed devcontainers found");
                    System.Console.WriteLine();

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
                DisplayTableOutput(filteredContainers, settings.ShowAll);
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

    /// <summary>
    /// Converts volume information to container information by querying Docker
    /// </summary>
    /// <param name="volumes">List of managed volumes</param>
    /// <returns>List of container information</returns>
    /// <remarks>
    /// This method queries Docker to get container details for each volume.
    /// For Phase 1, this is a simplified implementation that maps volumes to containers.
    /// </remarks>
    private async Task<List<ContainerInfo>> GetContainerInfoFromVolumesAsync(List<DevcontainerVolumeInfo> volumes)
    {
        var containers = new List<ContainerInfo>();

        foreach (var volume in volumes)
        {
            // For each volume, try to find the associated container
            // This would require Docker CLI calls or Docker API integration
            // For now, we create a placeholder based on volume info

            var container = new ContainerInfo
            {
                ProjectName = volume.ProjectName,
                ContainerId = volume.Name, // Volume name as placeholder
                IsRunning = false, // Would need to query Docker
                VolumeName = volume.Name,
                Created = volume.Created
            };

            containers.Add(container);
        }

        return containers;
    }

    /// <summary>
    /// Displays container information in a formatted table
    /// </summary>
    /// <param name="containers">List of containers to display</param>
    /// <param name="showAll">Whether showing all containers or just running ones</param>
    private void DisplayTableOutput(List<ContainerInfo> containers, bool showAll)
    {
        System.Console.WriteLine();

        var table = new Table()
            .Title($"[cyan]Managed Devcontainers[/] [dim]({containers.Count} {(showAll ? "total" : "running")})[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1);

        // Add columns
        table.AddColumn(new TableColumn("[yellow]Project[/]").LeftAligned());
        table.AddColumn(new TableColumn("[yellow]Container ID[/]").LeftAligned());
        table.AddColumn(new TableColumn("[yellow]Status[/]").Centered());
        table.AddColumn(new TableColumn("[yellow]Volume[/]").LeftAligned());
        table.AddColumn(new TableColumn("[yellow]Created[/]").RightAligned());

        // Add rows
        foreach (var container in containers.OrderByDescending(c => c.Created))
        {
            var projectName = string.IsNullOrEmpty(container.ProjectName)
                ? "[dim]<unknown>[/]"
                : container.ProjectName;

            var containerId = container.ContainerId.Length > 12
                ? container.ContainerId[..12]
                : container.ContainerId;

            var status = container.IsRunning
                ? "[green]Running[/]"
                : "[yellow]Stopped[/]";

            var volumeName = string.IsNullOrEmpty(container.VolumeName)
                ? "[dim]<none>[/]"
                : $"[dim]{container.VolumeName}[/]";

            var created = FormatDateTime(container.Created);

            table.AddRow(
                projectName,
                $"[cyan]{containerId}[/]",
                status,
                volumeName,
                $"[dim]{created}[/]"
            );
        }

        Console.Write(table);
        System.Console.WriteLine();

        // Display summary statistics
        if (showAll)
        {
            var running = containers.Count(c => c.IsRunning);
            var stopped = containers.Count - running;

            Console.MarkupLine($"[dim]Summary: {running} running, {stopped} stopped[/]");
        }

        System.Console.WriteLine();
    }

    /// <summary>
    /// Displays container information in JSON format
    /// </summary>
    /// <param name="containers">List of containers to display</param>
    private void DisplayJsonOutput(List<ContainerInfo> containers)
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
    /// Formats a DateTime as a relative time string (e.g., "2 hours ago")
    /// </summary>
    /// <param name="dateTime">DateTime to format</param>
    /// <returns>Human-readable relative time string</returns>
    private string FormatDateTime(DateTime dateTime)
    {
        var timeSpan = DateTime.UtcNow - dateTime.ToUniversalTime();

        if (timeSpan.TotalSeconds < 60)
            return "just now";

        if (timeSpan.TotalMinutes < 60)
            return $"{(int)timeSpan.TotalMinutes}m ago";

        if (timeSpan.TotalHours < 24)
            return $"{(int)timeSpan.TotalHours}h ago";

        if (timeSpan.TotalDays < 7)
            return $"{(int)timeSpan.TotalDays}d ago";

        if (timeSpan.TotalDays < 30)
            return $"{(int)(timeSpan.TotalDays / 7)}w ago";

        if (timeSpan.TotalDays < 365)
            return $"{(int)(timeSpan.TotalDays / 30)}mo ago";

        return dateTime.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Container information for display purposes
    /// </summary>
    /// <remarks>
    /// This class is used as a DTO for serialization to JSON and table display.
    /// It represents the essential information about a managed devcontainer.
    /// </remarks>
    private class ContainerInfo
    {
        /// <summary>
        /// Name of the project associated with this container
        /// </summary>
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>
        /// Docker container ID (full ID)
        /// </summary>
        [JsonPropertyName("containerId")]
        public string ContainerId { get; set; } = string.Empty;

        /// <summary>
        /// Current status of the container
        /// </summary>
        [JsonPropertyName("isRunning")]
        public bool IsRunning { get; set; }

        /// <summary>
        /// Status text for JSON output
        /// </summary>
        [JsonPropertyName("status")]
        public string Status => IsRunning ? "running" : "stopped";

        /// <summary>
        /// Name of the Docker volume used by this container
        /// </summary>
        [JsonPropertyName("volumeName")]
        public string VolumeName { get; set; } = string.Empty;

        /// <summary>
        /// When the container was created (ISO 8601 format)
        /// </summary>
        [JsonPropertyName("created")]
        public DateTime Created { get; set; }

        /// <summary>
        /// Human-readable creation time
        /// </summary>
        [JsonPropertyName("createdRelative")]
        public string CreatedRelative
        {
            get
            {
                var timeSpan = DateTime.UtcNow - Created.ToUniversalTime();
                if (timeSpan.TotalDays < 1) return $"{(int)timeSpan.TotalHours}h ago";
                if (timeSpan.TotalDays < 7) return $"{(int)timeSpan.TotalDays}d ago";
                return Created.ToString("yyyy-MM-dd");
            }
        }
    }
}
