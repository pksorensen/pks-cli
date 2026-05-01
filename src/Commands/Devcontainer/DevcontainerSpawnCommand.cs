using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Helper record for remote devcontainer spawn results
/// </summary>
internal record DevcontainerRemoteResult(bool Success, string? ContainerId, string? RemoteWorkspaceFolder, string? Error);

/// <summary>
/// Existing devcontainer discovered on a remote host via docker labels.
/// </summary>
internal record RemoteDevcontainerInfo(
    string Id, string Name, string Status, bool IsRunning, string LocalFolder, string? VolumeName);

/// <summary>
/// Command to spawn a devcontainer in a Docker volume for an existing project
/// </summary>
public class DevcontainerSpawnCommand : DevcontainerCommand<DevcontainerSpawnCommand.Settings>
{
    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly ISshTargetConfigurationService _sshTargetService;
    private readonly INuGetTemplateDiscoveryService _nugetTemplateService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;

    public DevcontainerSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshTargetService = sshTargetService ?? throw new ArgumentNullException(nameof(sshTargetService));
        _nugetTemplateService = nugetTemplateService ?? throw new ArgumentNullException(nameof(nugetTemplateService));
        _vmMetadata = vmMetadata ?? throw new ArgumentNullException(nameof(vmMetadata));
        _azureAuth = azureAuth ?? throw new ArgumentNullException(nameof(azureAuth));
        _vmService = vmService ?? throw new ArgumentNullException(nameof(vmService));
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

        [CommandOption("--ssh-target <TARGET>")]
        [Description("SSH target label or host to spawn on remotely")]
        public string? SshTarget { get; set; }
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

            // 0. Check for remote SSH target
            SshTarget? remoteTarget = null;
            if (!string.IsNullOrEmpty(settings.SshTarget))
            {
                remoteTarget = await _sshTargetService.FindTargetAsync(settings.SshTarget);
                if (remoteTarget == null)
                {
                    DisplayError($"SSH target not found: {settings.SshTarget}");
                    return 1;
                }
            }
            else
            {
                var targets = await _sshTargetService.ListTargetsAsync();
                if (targets.Count > 0)
                {
                    var localOption = "Local (this machine)";
                    var choices = new[] { localOption }.Concat(
                        targets.Select(t => t.Label ?? t.Host)).ToList();
                    var choice = Console.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[cyan]Where would you like to spawn the devcontainer?[/]")
                            .AddChoices(choices));
                    if (choice != localOption)
                    {
                        remoteTarget = targets.First(t => (t.Label ?? t.Host) == choice);
                    }
                }
            }

            if (remoteTarget != null)
                return await ExecuteRemoteSpawnAsync(context, settings, remoteTarget);

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

    private async Task<int> ExecuteRemoteSpawnAsync(CommandContext context, Settings settings, SshTarget target)
    {
        var projectPath = settings.ProjectPath ?? Directory.GetCurrentDirectory();
        projectPath = Path.GetFullPath(projectPath);
        var projectName = Path.GetFileName(projectPath);

        DisplayInfo($"Spawning on remote: {target.Label ?? target.Host} ({target.Username}@{target.Host})");

        // Ensure VM is running before attempting SSH
        if (!await EnsureVmRunningAsync(target))
            return 1;

        var sshArgs = BuildSshArgs(target);

        // Check Docker on remote (try full path as fallback — non-interactive SSH may have a stripped PATH)
        var dockerCheck = await RunSshCommandAsync(sshArgs, target, "docker --version || /usr/bin/docker --version");
        if (Environment.GetEnvironmentVariable("PKS_DEBUG") == "1")
            DisplayInfo($"docker check: exit={dockerCheck.Success} output={Markup.Escape(dockerCheck.Output.Trim())}");

        if (!dockerCheck.Success)
        {
            DisplayWarning($"Could not confirm Docker on remote host ({Markup.Escape(dockerCheck.Output.Trim().Split('\n')[0])}).");
            var proceedDocker = Console.Confirm("[cyan]Docker check failed — proceed anyway?[/]", defaultValue: false);
            if (!proceedDocker)
                return 1;
        }
        else
        {
            DisplaySuccess($"Docker available: {Markup.Escape(dockerCheck.Output.Trim().Split('\n')[0])}");
        }

        // Offer to attach to an existing devcontainer on this host before going through template/spawn flow
        var existingContainers = await DiscoverRemoteDevcontainersAsync(sshArgs, target, projectName);
        if (existingContainers.Count > 0)
        {
            const string CreateNewOption = "Create new devcontainer...";
            var hostChoices = existingContainers
                .Select(c => $"{(c.IsRunning ? "▶" : "■")} {c.Name}  [dim]{c.Status}[/]  [dim]({c.LocalFolder})[/]")
                .ToList();
            hostChoices.Add(CreateNewOption);

            var hostChoice = Console.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[cyan]Existing devcontainers on {target.Label ?? target.Host}:[/]")
                    .AddChoices(hostChoices));

            if (hostChoice != CreateNewOption)
            {
                var picked = existingContainers[hostChoices.IndexOf(hostChoice)];

                if (!picked.IsRunning)
                {
                    DisplayInfo($"Starting container {picked.Id[..Math.Min(12, picked.Id.Length)]}...");
                    var startResult = await RunSshCommandAsync(sshArgs, target, $"docker start {picked.Id}");
                    if (!startResult.Success)
                    {
                        DisplayError($"Failed to start container: {Markup.Escape(startResult.Output.Trim())}");
                        return 1;
                    }
                }

                // VS Code/devcontainer CLI mounts the local folder at /workspaces/<basename>
                var insideFolder = $"/workspaces/{Path.GetFileName(picked.LocalFolder.TrimEnd('/'))}";
                var pickedVolumeName = picked.VolumeName ?? string.Empty;

                DisplaySuccess($"Attaching to existing container: {picked.Name} ({picked.Id[..Math.Min(12, picked.Id.Length)]})");
                await OnAfterRemoteSpawnAsync(
                    target, sshArgs, projectName, picked.Id, insideFolder, pickedVolumeName, settings);
                return 0;
            }
        }

        // Ask for template — discover from NuGet first
        const string UseExistingOption = "Use existing .devcontainer";
        var templateChoices = new List<string> { UseExistingOption };
        List<NuGetDevcontainerTemplate> nugetTemplates = new();
        await WithSpinnerAsync("Discovering templates...", async () =>
        {
            var byPksDevcontainers = await _nugetTemplateService.DiscoverTemplatesAsync(tag: "pks-devcontainers");
            var byPksCli = await _nugetTemplateService.DiscoverTemplatesAsync(tag: "pks-cli");

            // Combine, keeping only devcontainer-related packages from the pks-cli set, dedup by PackageId
            var devcontainerFiltered = byPksCli.Where(t =>
                t.Tags.Any(tag => tag.Contains("devcontainer", StringComparison.OrdinalIgnoreCase)) ||
                t.Title.Contains("devcontainer", StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains("devcontainer", StringComparison.OrdinalIgnoreCase));

            nugetTemplates = byPksDevcontainers
                .Concat(devcontainerFiltered)
                .GroupBy(t => t.PackageId)
                .Select(g => g.First())
                .ToList();

            templateChoices.AddRange(nugetTemplates.Select(t => t.Title.Length > 0 ? t.Title : t.PackageId));
        });

        if (nugetTemplates.Count == 0)
            DisplayWarning("No NuGet templates found. Install a template package (e.g. 'dotnet new install PKS.Templates.PksFullstack') or use existing .devcontainer.");

        var templateChoice = Console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Devcontainer template:[/]")
                .AddChoices(templateChoices));

        // Check devcontainer exists and node >= 18 — use grep on version string, no JS quoting issues
        var dcCheck = await RunSshCommandAsync(sshArgs, target,
            "which devcontainer >/dev/null 2>&1 && node --version 2>/dev/null | grep -qE '^v(1[89]|[2-9][0-9])'");
        if (!dcCheck.Success)
        {
            DisplayWarning("devcontainer CLI missing or incompatible Node.js — installing Node 20 + devcontainer CLI (this takes ~2 min)...");

            var installLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "logs");
            Directory.CreateDirectory(installLogDir);
            var installLog = Path.Combine(installLogDir, $"devcontainer-install-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
            Console.MarkupLine($"[dim]Log: {Markup.Escape(installLog)}[/]");

            bool installOk = false;
            string installError = string.Empty;

            await using (var installWriter = new StreamWriter(installLog, append: false) { AutoFlush = true })
            {
                await Console.Status()
                    .SpinnerStyle(Style.Parse("cyan"))
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Installing devcontainer CLI...", async ctx =>
                    {
                        var install = await RunRemoteDevcontainerAsync(sshArgs, target,
                            "sudo apt-get remove -y nodejs npm libnode-dev libnode72 2>&1 || true && curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash - 2>&1 && sudo apt-get install -y nodejs 2>&1 && sudo npm install -g @devcontainers/cli 2>&1",
                            line =>
                            {
                                installWriter.WriteLine(line);
                                var trimmed = line.Trim();
                                if (trimmed.Length > 0)
                                    ctx.Status($"[dim]{Markup.Escape(trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed)}[/]");
                            });
                        installOk = install.Success;
                        installError = install.Error ?? string.Empty;
                    });
            }

            if (!installOk)
            {
                DisplayError($"Failed to install devcontainer CLI: {Markup.Escape(installError)}");
                DisplayInfo($"Full log: {Markup.Escape(installLog)}");
                return 1;
            }
            DisplaySuccess("devcontainer CLI installed.");
        }

        // Create remote workspace
        await RunSshCommandAsync(sshArgs, target, $"mkdir -p ~/pks-workspaces/{projectName}");

        // Copy files
        DisplayInfo("Copying project files...");
        await WithSpinnerAsync("Copying files...", async () =>
        {
            var scpResult = await RunScpAsync(target, projectPath, $"~/pks-workspaces/{projectName}");
            if (!scpResult) throw new Exception("Failed to copy files to remote host");
        });

        // If a NuGet template was selected, extract it and upload .devcontainer to remote
        if (templateChoice != UseExistingOption)
        {
            var selectedTemplate = nugetTemplates.FirstOrDefault(t =>
                (t.Title.Length > 0 ? t.Title : t.PackageId) == templateChoice);

            if (selectedTemplate != null)
            {
                await WithSpinnerAsync($"Extracting template '{templateChoice}'...", async () =>
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), $"pks-template-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(tempDir);
                    try
                    {
                        var extraction = await _nugetTemplateService.ExtractTemplateAsync(
                            selectedTemplate.PackageId, selectedTemplate.Version, tempDir);

                        if (extraction.Success)
                        {
                            var devcontainerDir = Directory.GetDirectories(tempDir, ".devcontainer", SearchOption.AllDirectories)
                                .FirstOrDefault() ?? Directory.GetDirectories(tempDir, "devcontainer", SearchOption.AllDirectories)
                                .FirstOrDefault();

                            if (devcontainerDir != null)
                            {
                                var scpResult = await RunScpAsync(target, devcontainerDir, $"~/pks-workspaces/{projectName}/.devcontainer");
                                if (!scpResult)
                                    DisplayWarning("Could not upload .devcontainer from template; using any existing .devcontainer.");
                            }
                            else
                            {
                                // No .devcontainer sub-folder — copy the whole extracted content
                                var scpResult = await RunScpAsync(target, tempDir, $"~/pks-workspaces/{projectName}/.devcontainer");
                                if (!scpResult)
                                    DisplayWarning("Could not upload template content; using any existing .devcontainer.");
                            }
                        }
                        else
                        {
                            DisplayWarning($"Template extraction failed: {extraction.ErrorMessage}");
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, recursive: true); } catch { }
                    }
                });
            }
        }

        // Create a named Docker volume and pre-populate it with the workspace files.
        // VS Code on Windows can only reference devcontainers by Docker volume name — it
        // cannot bind-mount a path that lives on the SSH remote host. By using a named volume
        // and recording it in workspaceMount we avoid the "lstat C:\..." error.
        var sanitizedProjectName = Regex.Replace(projectName.ToLower(), @"[^a-z0-9]", "-");
        var volumeName = $"pks-{sanitizedProjectName}-devcontainer";
        var remoteHostPath = $"/home/{target.Username}/pks-workspaces/{projectName}";
        // VS Code mounts the volume at /workspaces and looks for /{projectName}/ inside it.
        // workspaceMount must target /workspaces; workspaceFolder is /workspaces/{projectName}.
        var volumeMountTarget = "/workspaces";
        var remoteContainerWorkspace = $"/workspaces/{projectName}";

        await WithSpinnerAsync("Preparing Docker volume...", async () =>
        {
            // Step 1: Patch host devcontainer.json BEFORE copying to volume so both copies have
            // workspaceFolder and workspaceMount set. VS Code reads the volume copy on reconnect
            // to determine the Explorer root — without workspaceFolder it defaults to /workspaces
            // and shows fabric/ as a subdirectory instead of opening at /workspaces/fabric.
            var patchScript = """
                const fs=require('fs');
                const [,p,mount,folder]=process.argv;
                try{
                  let c=fs.readFileSync(p,'utf8')
                    .replace(/\/\/[^\n]*/g,'')
                    .replace(/\/\*[\s\S]*?\*\//g,'')
                    .replace(/,(\s*[}\]])/g,'$1');
                  const d=JSON.parse(c);
                  d.workspaceMount=mount;
                  d.workspaceFolder=folder;
                  fs.writeFileSync(p,JSON.stringify(d,null,2));
                  process.stdout.write('ok');
                }catch(e){console.error(e.message);process.exit(1);}
                """;
            var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(patchScript));
            var mountSpec = $"source={volumeName},target={volumeMountTarget},type=volume";
            var dcJsonPath = $"{remoteHostPath}/.devcontainer/devcontainer.json";
            await RunSshCommandAsync(sshArgs, target,
                $"echo {scriptB64} | base64 -d | node - '{dcJsonPath}' '{mountSpec}' '{remoteContainerWorkspace}'");

            // Step 2: Remove any containers using this volume so docker volume rm can succeed.
            // VS Code labels its devcontainer containers with vsc.devcontainer.volume.name.
            // Without this, docker volume rm fails silently, the old volume (with wrong
            // structure/devcontainer.json) persists, and VS Code reuses the stale container.
            await RunSshCommandAsync(sshArgs, target,
                $"docker ps -aq --filter label=vsc.devcontainer.volume.name={volumeName} | xargs -r docker rm -f 2>/dev/null || true");

            // Step 3: Recreate the volume so stale files from a previous spawn don't persist.
            await RunSshCommandAsync(sshArgs, target,
                $"docker volume rm {volumeName} 2>/dev/null || true");
            await RunSshCommandAsync(sshArgs, target, $"docker volume create {volumeName}");

            // Step 4: Copy the already-patched files into /volume/{projectName}/ so that when
            // VS Code mounts the volume at /workspaces the files appear at
            // /workspaces/{projectName}/.devcontainer/devcontainer.json.
            // Mount at /volume (not /) to keep Alpine's own system files intact.
            await RunSshCommandAsync(sshArgs, target,
                $"docker run --rm -v {remoteHostPath}:/src:ro -v {volumeName}:/volume alpine sh -c 'mkdir -p /volume/{projectName} && cp -a /src/. /volume/{projectName}/'");
        });

        // Run devcontainer up — write full output to log file, show latest line in spinner
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"devcontainer-{projectName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        Console.MarkupLine($"[dim]Log: {Markup.Escape(logFile)}[/]");
        Console.MarkupLine($"[dim]Tail: tail -f \"{Markup.Escape(logFile)}\"[/]");

        DevcontainerRemoteResult? result = null;
        var cmd = $"cd ~/pks-workspaces/{projectName} && devcontainer up --workspace-folder . 2>&1";

        await using var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };

        await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting devcontainer...", async ctx =>
            {
                result = await RunRemoteDevcontainerAsync(sshArgs, target, cmd, line =>
                {
                    logWriter.WriteLine(line);
                    var display = ParseDevcontainerLine(line);
                    if (display != null)
                        ctx.Status($"[dim]{Markup.Escape(display.Length > 80 ? display[..80] + "…" : display)}[/]");
                });
            });

        if (result == null || !result.Success)
        {
            DisplayError($"Remote devcontainer spawn failed: {result?.Error ?? "unknown error"}");
            DisplayInfo($"Full log: {Markup.Escape(logFile)}");
            return 1;
        }

        await OnAfterRemoteSpawnAsync(
            target, sshArgs, projectName,
            result.ContainerId, result.RemoteWorkspaceFolder ?? remoteContainerWorkspace,
            volumeName, settings);

        Console.Write(new Panel($"""
            [green]Remote devcontainer spawned successfully![/]

            [cyan1]Host:[/] {target.Username}@{target.Host}
            [cyan1]Workspace:[/] ~/pks-workspaces/{projectName}

            [dim]SSH: ssh -i {target.KeyPath} {target.Username}@{target.Host}[/]
            [dim]Reconnect: pks devcontainer spawn --ssh-target {target.Label ?? target.Host}[/]
            """)
            .Border(BoxBorder.Rounded)
            .BorderStyle("green")
            .Header(" [bold green]Success[/] "));

        return 0;
    }

    protected virtual async Task OnAfterRemoteSpawnAsync(
        SshTarget target, string sshArgs, string projectName, string? containerId,
        string remoteWorkspaceFolder, string volumeName, Settings settings)
    {
        var openVsCode = !settings.NoLaunchVsCode && Console.Confirm(
            "[cyan]Open VS Code connected to remote devcontainer?[/]", defaultValue: true);

        if (openVsCode)
        {
            await LaunchVsCodeRemoteAsync(target, sshArgs, remoteWorkspaceFolder, Console,
                volumeName: volumeName, projectFolder: projectName);
        }
    }

    private async Task<bool> EnsureVmRunningAsync(SshTarget target)
    {
        var vms = await _vmMetadata.ListAsync();
        var vmRecord = vms.FirstOrDefault(v =>
            string.Equals(v.VmName, target.Label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.PublicIpAddress, target.Host, StringComparison.OrdinalIgnoreCase));

        if (vmRecord == null) return true;
        if (!await _azureAuth.IsAuthenticatedAsync()) return true;

        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token)) return true;

        string? status = null;
        await WithSpinnerAsync("Checking VM status...", async () =>
        {
            status = await _vmService.GetVmStatusAsync(token, vmRecord.SubscriptionId, vmRecord.ResourceGroup, vmRecord.VmName);
        });

        if (status == null)
        {
            DisplayError($"VM '{vmRecord.VmName}' no longer exists in Azure.");
            return false;
        }

        if (status is "running" or "starting")
            return true;

        DisplayWarning($"VM '{Markup.Escape(vmRecord.VmName)}' is {Markup.Escape(status)}.");
        var start = Console.Confirm("[cyan]Start the VM?[/]", defaultValue: true);
        if (!start) return false;

        Exception? startError = null;
        await WithSpinnerAsync("Starting VM...", async () =>
        {
            try { await _vmService.StartVmAsync(token, vmRecord.SubscriptionId, vmRecord.ResourceGroup, vmRecord.VmName); }
            catch (Exception ex) { startError = ex; }
        });

        if (startError != null)
        {
            DisplayError($"Failed to start VM: {Markup.Escape(startError.Message)}");
            return false;
        }

        var sshReady = false;
        await WithSpinnerAsync("Waiting for SSH...", async () =>
        {
            sshReady = await _vmService.WaitForSshAsync(target.Host, target.Port, TimeSpan.FromMinutes(3));
        });

        if (!sshReady)
        {
            DisplayWarning("SSH did not become available in time. The VM may still be booting.");
            return false;
        }

        DisplaySuccess("VM started.");
        return true;
    }

    // devcontainer CLI emits JSONL events. Extract the human-readable text from each line.
    private static string? ParseDevcontainerLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            if (line.TrimStart().StartsWith('{'))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                // Prefer "text" field, fall back to "message"
                if (root.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } t)
                    return t.TrimEnd();
                if (root.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } m)
                    return m.TrimEnd();
                // Skip noisy event-only lines (no human text)
                return null;
            }
        }
        catch { }
        return line.TrimEnd();
    }

    private static string BuildSshArgs(SshTarget target)
    {
        var args = "-o StrictHostKeyChecking=no -o BatchMode=yes";
        if (!string.IsNullOrEmpty(target.KeyPath))
            args += $" -i \"{target.KeyPath}\"";
        args += $" -p {target.Port}";
        return args;
    }

    /// <summary>
    /// Lists devcontainer-managed containers on the remote host that match the given project name
    /// (matched by basename of the devcontainer.local_folder label).
    /// </summary>
    private async Task<List<RemoteDevcontainerInfo>> DiscoverRemoteDevcontainersAsync(
        string sshArgs, SshTarget target, string projectName)
    {
        var result = new List<RemoteDevcontainerInfo>();
        try
        {
            var ps = await RunSshCommandAsync(sshArgs, target,
                "docker ps -a --filter label=devcontainer.local_folder --format json");
            if (!ps.Success) return result;

            foreach (var line in ps.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] != '{') continue;

                JsonElement root;
                try { root = JsonDocument.Parse(trimmed).RootElement; }
                catch { continue; }

                var id = root.TryGetProperty("ID", out var idEl) ? idEl.GetString() ?? "" : "";
                var name = root.TryGetProperty("Names", out var nEl) ? nEl.GetString() ?? "" : "";
                var status = root.TryGetProperty("Status", out var sEl) ? sEl.GetString() ?? "" : "";
                var state = root.TryGetProperty("State", out var stEl) ? stEl.GetString() ?? "" : "";
                var labelsRaw = root.TryGetProperty("Labels", out var lEl) ? lEl.GetString() ?? "" : "";

                string? localFolder = null;
                string? volumeName = null;
                foreach (var kv in labelsRaw.Split(','))
                {
                    var eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = kv[..eq];
                    var v = kv[(eq + 1)..];
                    if (k == "devcontainer.local_folder") localFolder = v;
                    else if (k == "vsc.devcontainer.volume.name") volumeName = v;
                }

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(localFolder)) continue;

                var basename = Path.GetFileName(localFolder.Replace('\\', '/').TrimEnd('/'));
                if (!string.Equals(basename, projectName, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new RemoteDevcontainerInfo(
                    Id: id,
                    Name: name,
                    Status: status,
                    IsRunning: string.Equals(state, "running", StringComparison.OrdinalIgnoreCase),
                    LocalFolder: localFolder,
                    VolumeName: volumeName));
            }
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("PKS_DEBUG") == "1")
                DisplayWarning($"Discovery failed: {ex.Message}");
        }
        return result;
    }

    protected static async Task<(bool Success, string Output)> RunSshCommandAsync(
        string sshArgs, SshTarget target, string command, int timeoutSeconds = 30)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                Arguments = $"{sshArgs} {target.Username}@{target.Host} \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, string.Empty);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
                var error = await process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);
                return (process.ExitCode == 0, output + error);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return (false, $"Command timed out after {timeoutSeconds}s");
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<bool> RunScpAsync(SshTarget target, string localPath, string remotePath)
    {
        try
        {
            var keyArg = !string.IsNullOrEmpty(target.KeyPath) ? $"-i \"{target.KeyPath}\" " : string.Empty;
            var psi = new ProcessStartInfo("scp")
            {
                Arguments = $"-r {keyArg}-P {target.Port} -o StrictHostKeyChecking=no \"{localPath}/.\" {target.Username}@{target.Host}:{remotePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<DevcontainerRemoteResult> RunRemoteDevcontainerAsync(
        string sshArgs, SshTarget target, string command, Action<string> onProgress)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                Arguments = $"{sshArgs} {target.Username}@{target.Host} \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return new DevcontainerRemoteResult(false, null, null, "Failed to start ssh process");

            string? lastJsonLine = null;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                onProgress(e.Data);
                var trimmed = e.Data.Trim();
                if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                    lastJsonLine = trimmed;
            };

            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                return new DevcontainerRemoteResult(false, null, null, $"Remote process exited with code {process.ExitCode}");

            // Parse containerId and remoteWorkspaceFolder from the devcontainer up success JSON
            string? containerId = null;
            string? remoteWorkspaceFolder = null;
            if (lastJsonLine != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lastJsonLine);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("containerId", out var cid))
                        containerId = cid.GetString();
                    if (root.TryGetProperty("remoteWorkspaceFolder", out var rwf))
                        remoteWorkspaceFolder = rwf.GetString();
                }
                catch { }
            }

            return new DevcontainerRemoteResult(true, containerId, remoteWorkspaceFolder, null);
        }
        catch (Exception ex)
        {
            return new DevcontainerRemoteResult(false, null, null, ex.Message);
        }
    }

    private static void EnsureSshConfig(SshTarget target)
    {
        // Intentionally left empty — ~/.ssh/config is never modified by pks-cli.
        // SSH key access for VS Code is provided via remote.SSH.configFile in
        // the isolated user-data-dir settings pointing to ~/.pks-cli/ssh_config.
    }

    private static void ConfigureVsCodeSshSettings(SshTarget target)
    {
        if (string.IsNullOrEmpty(target.KeyPath)) return;

        var pksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
        Directory.CreateDirectory(pksDir);

        var sshConfigPath = Path.Combine(pksDir, "ssh_config");
        var keyPath = target.KeyPath;
        File.WriteAllText(sshConfigPath,
            $"Host {target.Host}\n    HostName {target.Host}\n    User {target.Username}\n    Port {target.Port}\n    IdentityFile {keyPath}\n    IdentitiesOnly yes\n    StrictHostKeyChecking no\n");

        // Write remote.SSH.configFile into the user's real VS Code settings so VS Code finds
        // the pks key without touching ~/.ssh/config. One-time non-destructive write.
        try
        {
            var settingsPath = GetVsCodeUserSettingsPath();
            if (settingsPath == null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var existing = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
            if (string.IsNullOrWhiteSpace(existing)) existing = "{}";

            // Strip JSONC line comments and trailing commas so JsonNode.Parse works on VS Code
            // settings files that use JSONC syntax (e.g. // comments, trailing commas).
            var stripped = Regex.Replace(existing, @"//[^\n]*", "");              // line comments
            stripped = Regex.Replace(stripped, @"/\*.*?\*/", "", RegexOptions.Singleline); // block comments
            stripped = Regex.Replace(stripped, @",\s*([}\]])", "$1");             // trailing commas

            try
            {
                var node = JsonNode.Parse(stripped) as JsonObject ?? new JsonObject();
                node["remote.SSH.configFile"] = sshConfigPath;
                File.WriteAllText(settingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Last-resort: regex-replace the key if present, otherwise leave file alone.
                // Do NOT attempt string manipulation that could produce invalid JSON.
                var escapedPath = sshConfigPath.Replace("\\", "\\\\");
                var pattern = @"""remote\.SSH\.configFile""\s*:\s*""[^""]*""";
                var replacement = $"\"remote.SSH.configFile\": \"{escapedPath}\"";
                if (Regex.IsMatch(existing, pattern))
                    File.WriteAllText(settingsPath, Regex.Replace(existing, pattern, replacement));
                // If key not present and we can't safely insert, skip — settings may already
                // have a valid entry pointing to a different SSH config file.
            }
        }
        catch { }
    }

    private static string? GetVsCodeUserSettingsPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Code", "User", "settings.json");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Code", "User", "settings.json");
        }
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "Code", "User", "settings.json");
    }

    private static string ToHex(string json) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(json)).ToLower();

    private static (string Uri, string RemoteAuthority) BuildDevContainerUri(
        SshTarget target, string remoteWorkspaceFolder,
        string? volumeName = null, string? projectFolder = null,
        string? containerId = null, string? hostPath = null)
    {
        string dcJson;
        if (!string.IsNullOrEmpty(volumeName))
        {
            // Volume-based devcontainer: VS Code mounts the named Docker volume directly on the
            // SSH remote without copying any local files. This is what the working .code-workspace
            // files use (e.g. agentic-live-www.code-workspace).
            dcJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["volumeName"] = volumeName,
                ["folder"] = projectFolder ?? Path.GetFileName(remoteWorkspaceFolder.TrimEnd('/')),
                ["inspectVolume"] = false
            });
        }
        else if (!string.IsNullOrEmpty(containerId))
        {
            // Attach to an already-running container by ID (local Docker only — SSH remote
            // containers do not support the parentAuthority variant of this format).
            dcJson = JsonSerializer.Serialize(new { containerId });
        }
        else
        {
            // Last-resort: open from a host path. On a remote Docker host VS Code will attempt
            // to lstat this path locally and fail on Windows — prefer volumeName instead.
            var configObj = new Dictionary<string, object>
            {
                ["$mid"] = 1,
                ["path"] = $"{hostPath}/.devcontainer/devcontainer.json",
                ["scheme"] = "file",
                ["authority"] = ""
            };
            dcJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["hostPath"] = hostPath ?? string.Empty,
                ["localDocker"] = false,
                ["configFile"] = configObj
            });
        }

        var dcHex = ToHex(dcJson);
        // ssh-remote hex: VS Code expects {"hostName":"...","user":"..."}, NOT plain user@host
        var sshHex = ToHex(JsonSerializer.Serialize(new { hostName = target.Host, user = target.Username }));

        var remoteAuthority = $"dev-container+{dcHex}@ssh-remote+{sshHex}";
        var uri = $"vscode-remote://{remoteAuthority}{remoteWorkspaceFolder}";
        return (uri, remoteAuthority);
    }

    private static async Task LaunchVsCodeRemoteAsync(
        SshTarget target, string sshArgs, string remoteWorkspaceFolder, IAnsiConsole console,
        string? volumeName = null, string? projectFolder = null,
        string? containerId = null, string? hostPath = null)
    {
        try
        {
            ConfigureVsCodeSshSettings(target);

            PksSSHAgent? agent = null;
            if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(target.KeyPath) && File.Exists(target.KeyPath))
            {
                try { agent = new PksSSHAgent(target.KeyPath); await agent.StartAsync(); }
                catch { if (agent != null) _ = agent.DisposeAsync().AsTask(); agent = null; }
            }

            await using (agent)
            {
                var (uri, remoteAuthority) = BuildDevContainerUri(
                    target, remoteWorkspaceFolder,
                    volumeName: volumeName, projectFolder: projectFolder,
                    containerId: containerId, hostPath: hostPath);

                // Write a .code-workspace with a top-level "remoteAuthority" field.
                // VS Code reads this field before calling resolveAuthority(), which avoids the
                // "Parent authority found without ExecServer" race in Dev Containers ≥ 0.388.0
                // (vscode-remote-release#10347). Opening a workspace file bypasses --folder-uri
                // cold-start entirely — no polling or two-step approach needed.
                var vscodeDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "vscode");
                Directory.CreateDirectory(vscodeDir);
                var workspaceFile = Path.Combine(vscodeDir, "pks-devcontainer.code-workspace");

                var workspaceJson = JsonSerializer.Serialize(new
                {
                    folders = new[] { new { uri } },
                    remoteAuthority,
                    settings = new { }
                }, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(workspaceFile, workspaceJson);

                console.MarkupLine($"[dim]Opening VS Code via workspace file...[/]");
                StartVsCode($"\"{workspaceFile}\"", agent);
            }
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[yellow]Failed to launch VS Code: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static void StartVsCode(string args, PksSSHAgent? agent)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo("code") { Arguments = args, UseShellExecute = true };
        }
        else
        {
            psi = new ProcessStartInfo("code") { Arguments = args, UseShellExecute = false };
            if (agent != null) psi.Environment["SSH_AUTH_SOCK"] = agent.SocketPath;
        }
        Process.Start(psi);
    }


}
