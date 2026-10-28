using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Devcontainer;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command to list all managed devcontainers with their status and details.
/// Supports both local and remote (SSH) targets. <c>--remote &lt;target&gt;</c> picks the host without
/// prompting, and <c>--format json</c> puts nothing but the document on stdout.
/// </summary>
public class DevcontainerContainersCommand : DevcontainerCommand<DevcontainerContainersCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly ISshTargetConfigurationService? _sshConfigService;
    private readonly ISshCommandRunner? _sshCommandRunner;
    private readonly ISshKeyStore? _keyStore;
    private readonly IActionGuard? _guard;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    /// <summary>
    /// Initializes a new instance of the DevcontainerContainersCommand
    /// </summary>
    public DevcontainerContainersCommand(
        IDevcontainerSpawnerService spawnerService,
        IAnsiConsole console,
        ISshTargetConfigurationService? sshConfigService = null,
        ISshCommandRunner? sshCommandRunner = null,
        ISshKeyStore? keyStore = null,
        IActionGuard? guard = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshConfigService = sshConfigService;
        _sshCommandRunner = sshCommandRunner;
        _keyStore = keyStore;
        _guard = guard;
        _stdout = stdout ?? System.Console.Out;
        _stderr = stderr ?? System.Console.Error;
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

        /// <summary>
        /// Registered SSH target to list on, or 'local'. Skips the host prompt.
        /// </summary>
        [CommandOption("--remote <TARGET>")]
        [Description("Registered SSH target (label or host) to list on, or 'local'. Never prompts.")]
        public string? Remote { get; set; }
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
        var format = settings.Format.ToLowerInvariant();
        var json = format == "json";
        if (!json && format != "table")
        {
            DisplayError($"Invalid format '{settings.Format}'. Valid formats are: table, json");
            return 1;
        }

        if (!json) DisplayBanner("Containers");

        DevcontainerRemoteHost? host = null;
        try
        {
            string? error;
            (host, error) = await ResolveHostAsync(settings.Remote, json);
            if (host == null)
            {
                Fail(json, error ?? "Could not resolve host");
                return 1;
            }

            var remoteHost = host.Host;
            List<ContainerDisplayInfo> containers;

            if (remoteHost != null)
            {
                if (_sshCommandRunner == null)
                {
                    Fail(json, "SSH command runner not available");
                    return 1;
                }

                var (remote, remoteError) = await GetRemoteContainersAsync(remoteHost, json);
                if (remote == null)
                {
                    if (json)
                    {
                        WriteJson(new ContainersReport { Host = host.Label, Reachable = false, Error = remoteError });
                        return 0;
                    }
                    DisplayError($"Failed to list remote containers: {remoteError}");
                    return 1;
                }
                containers = remote;

                // Fetch volumes for each container
                if (containers.Count > 0)
                {
                    await MaybeSpinnerAsync(json ? null : "Fetching volume info...", async () =>
                    {
                        foreach (var container in containers)
                        {
                            var inspectResult = await _sshCommandRunner!.RunAsync(remoteHost,
                                $"docker inspect {container.ContainerId} --format json");
                            if (inspectResult.Success)
                            {
                                try
                                {
                                    var inspectJson = JsonDocument.Parse(inspectResult.StdOut.Trim().TrimStart('[').TrimEnd(']'));
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
                containers = await GetLocalContainersAsync(json);
            }

            // Filter containers based on --all flag
            var filteredContainers = settings.ShowAll
                ? containers
                : containers.Where(c => c.Status.Contains("Up", StringComparison.OrdinalIgnoreCase)
                    || c.Status.Equals("running", StringComparison.OrdinalIgnoreCase)).ToList();

            if (json)
            {
                WriteJson(new ContainersReport { Host = host.Label, Reachable = true, Containers = filteredContainers });
                return 0;
            }

            // Handle empty results
            if (!filteredContainers.Any())
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
                return 0;
            }

            DisplayTableOutput(filteredContainers, settings.ShowAll, remoteHost);
            return 0;
        }
        catch (Exception ex)
        {
            if (json)
            {
                _stderr.WriteLine($"Unexpected error: {ex.Message}");
            }
            else
            {
                DisplayError("Unexpected error");
                Console.WriteException(ex);
            }
            return 1;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private async Task<(DevcontainerRemoteHost? Host, string? Error)> ResolveHostAsync(string? remoteSpec, bool json)
    {
        if (!string.IsNullOrWhiteSpace(remoteSpec))
            return await DevcontainerRemoteHost.ResolveAsync(remoteSpec, _sshConfigService, _keyStore, _guard);

        // No --remote: keep the historical behaviour — ask which host when targets are registered.
        // A json consumer cannot answer a prompt, so it gets the local daemon.
        if (json || _sshConfigService == null)
            return (DevcontainerRemoteHost.Local, null);

        var sshTargets = await _sshConfigService.ListTargetsAsync();
        if (sshTargets.Count == 0)
            return (DevcontainerRemoteHost.Local, null);

        var choices = new List<string> { "Local (this machine)" };
        choices.AddRange(sshTargets.Select(t =>
            $"{t.Username}@{t.Host}" + (string.IsNullOrEmpty(t.Label) ? "" : $" ({t.Label})")));

        var selected = Console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Which host?[/]")
                .AddChoices(choices));

        var selectedIndex = choices.IndexOf(selected) - 1;
        if (selectedIndex < 0)
            return (DevcontainerRemoteHost.Local, null);

        return await DevcontainerRemoteHost.FromTargetAsync(sshTargets[selectedIndex], _keyStore, _guard);
    }

    private void Fail(bool json, string message)
    {
        if (json) _stderr.WriteLine(message);
        else DisplayError(message);
    }

    private async Task MaybeSpinnerAsync(string? message, Func<Task> operation)
    {
        if (message == null) await operation();
        else await WithSpinnerAsync(message, operation);
    }

    private async Task<List<ContainerDisplayInfo>> GetLocalContainersAsync(bool json)
    {
        var containers = new List<ContainerDisplayInfo>();

        List<DevcontainerContainerInfo>? managed = null;
        await MaybeSpinnerAsync(json ? null : "Retrieving managed containers...", async () =>
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
                Name = c.ContainerName,
                LocalFolder = c.Labels.TryGetValue("devcontainer.local_folder", out var folder) ? folder : "",
                Status = c.Status,
                Created = c.CreatedDate.ToString("yyyy-MM-dd HH:mm:ss"),
                Image = c.Labels.ContainsKey("devcontainer.metadata") ? "(devcontainer)" : ""
            });
        }

        return containers;
    }

    /// <returns>The containers, or <c>null</c> with an error when the host could not be reached.</returns>
    private async Task<(List<ContainerDisplayInfo>? Containers, string? Error)> GetRemoteContainersAsync(RemoteHostConfig remoteHost, bool json)
    {
        SshCommandResult? result = null;
        await MaybeSpinnerAsync(json ? null : $"Retrieving containers from {remoteHost.Username}@{remoteHost.Host}...", async () =>
        {
            // Use JSON format to avoid shell escaping issues with Go template labels
            result = await _sshCommandRunner!.RunAsync(remoteHost,
                "docker ps -a --filter " + DevcontainerLabels.Filter + " --format json");
        });

        if (result == null || !result.Success)
        {
            var error = string.IsNullOrWhiteSpace(result?.StdErr)
                ? (result == null ? "unknown error" : $"docker ps exited {result.ExitCode}")
                : result.StdErr.Trim();
            return (null, error);
        }

        var containers = new List<ContainerDisplayInfo>();
        var lines = result.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            try
            {
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = root.TryGetProperty("ID", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("Names", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var status = root.TryGetProperty("Status", out var statusEl) ? statusEl.GetString() ?? "" : "";
                var image = root.TryGetProperty("Image", out var imageEl) ? imageEl.GetString() ?? "" : "";
                var createdAt = root.TryGetProperty("CreatedAt", out var createdEl) ? createdEl.GetString() ?? "" : "";

                var labels = root.TryGetProperty("Labels", out var labelsEl) ? labelsEl.GetString() ?? "" : "";
                var (projectName, localFolder) = DevcontainerLabels.Describe(labels, name, image);

                containers.Add(new ContainerDisplayInfo
                {
                    ProjectName = projectName,
                    ContainerId = id,
                    Name = name,
                    LocalFolder = localFolder,
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

        return (containers, null);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep '+' and '/' readable
    };

    private void WriteJson(ContainersReport report)
    {
        _stdout.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        _stdout.Flush();
    }

    /// <summary>
    /// The JSON document: <c>{host, reachable, error, containers[]}</c>. Before <c>--remote</c> the
    /// command printed the bare array; the wrapper is what lets an unreachable host be data.
    /// </summary>
    public sealed class ContainersReport
    {
        [JsonPropertyName("host")]
        public string Host { get; init; } = "local";

        [JsonPropertyName("reachable")]
        public bool Reachable { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("containers")]
        public List<ContainerDisplayInfo> Containers { get; init; } = new();
    }

    /// <summary>
    /// Container information for display purposes. Carries both the contract names
    /// (<c>id, name, project, localFolder, status, image, createdAt</c>) and the legacy ones
    /// (<c>projectName, containerId, created, volumes</c>) that earlier consumers of the array read.
    /// </summary>
    public sealed class ContainerDisplayInfo
    {
        [JsonPropertyName("id")]
        public string Id => ContainerId;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("project")]
        public string Project => ProjectName;

        [JsonPropertyName("localFolder")]
        public string LocalFolder { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt => DockerTimestamps.ToIso(Created);

        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        [JsonPropertyName("containerId")]
        public string ContainerId { get; set; } = string.Empty;

        [JsonPropertyName("created")]
        public string Created { get; set; } = string.Empty;

        [JsonPropertyName("volumes")]
        public List<string> Volumes { get; set; } = new();
    }
}
