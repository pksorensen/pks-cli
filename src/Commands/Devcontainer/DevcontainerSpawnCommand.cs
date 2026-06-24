using Microsoft.Extensions.DependencyInjection;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Claude;
using PKS.Infrastructure.Services.Security;
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
    private readonly IClaudeMarketplaceConfigurationService? _claudeMarketplaceConfigService;
    private readonly IClaudeManagedSettingsRenderer? _claudeManagedSettingsRenderer;
    private readonly IAzureFoundryAuthService? _foundryAuthService;
    private readonly VmInitCommand? _vmInitCommand;
    // Gates billable/remote actions behind a second factor. Set via the primary (subclass) ctor;
    // null on the legacy overloads, in which case gating is skipped (those aren't the runtime path).
    private readonly IActionGuard? _guard;

    public DevcontainerSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshTargetService = sshTargetService ?? throw new ArgumentNullException(nameof(sshTargetService));
        _nugetTemplateService = nugetTemplateService ?? throw new ArgumentNullException(nameof(nugetTemplateService));
        _vmMetadata = vmMetadata ?? throw new ArgumentNullException(nameof(vmMetadata));
        _azureAuth = azureAuth ?? throw new ArgumentNullException(nameof(azureAuth));
        _vmService = vmService ?? throw new ArgumentNullException(nameof(vmService));
        _vmInitCommand = vmInitCommand;
    }

    public DevcontainerSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IClaudeMarketplaceConfigurationService claudeMarketplaceConfigService,
        IClaudeManagedSettingsRenderer claudeManagedSettingsRenderer,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshTargetService = sshTargetService ?? throw new ArgumentNullException(nameof(sshTargetService));
        _nugetTemplateService = nugetTemplateService ?? throw new ArgumentNullException(nameof(nugetTemplateService));
        _vmMetadata = vmMetadata ?? throw new ArgumentNullException(nameof(vmMetadata));
        _azureAuth = azureAuth ?? throw new ArgumentNullException(nameof(azureAuth));
        _vmService = vmService ?? throw new ArgumentNullException(nameof(vmService));
        _vmInitCommand = vmInitCommand;
        _claudeMarketplaceConfigService = claudeMarketplaceConfigService;
        _claudeManagedSettingsRenderer = claudeManagedSettingsRenderer;
    }

    public DevcontainerSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IClaudeMarketplaceConfigurationService? claudeMarketplaceConfigService,
        IClaudeManagedSettingsRenderer? claudeManagedSettingsRenderer,
        IAzureFoundryAuthService? foundryAuthService,
        IActionGuard? actionGuard,
        IAnsiConsole console)
        : base(console)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
        _sshTargetService = sshTargetService ?? throw new ArgumentNullException(nameof(sshTargetService));
        _nugetTemplateService = nugetTemplateService ?? throw new ArgumentNullException(nameof(nugetTemplateService));
        _vmMetadata = vmMetadata ?? throw new ArgumentNullException(nameof(vmMetadata));
        _azureAuth = azureAuth ?? throw new ArgumentNullException(nameof(azureAuth));
        _vmService = vmService ?? throw new ArgumentNullException(nameof(vmService));
        _vmInitCommand = vmInitCommand;
        _claudeMarketplaceConfigService = claudeMarketplaceConfigService;
        _claudeManagedSettingsRenderer = claudeManagedSettingsRenderer;
        _foundryAuthService = foundryAuthService;
        _guard = actionGuard;
    }

    /// <summary>Require a second factor for <paramref name="request"/>. Returns false if denied
    /// (the command should abort). No-op pass when no guard was injected (legacy ctors).</summary>
    protected async Task<bool> TryRequireAsync(ActionRequest request)
    {
        if (_guard == null) return true;
        try { await _guard.RequireAsync(request); return true; }
        catch (ActionGuardDeniedException ex)
        {
            DisplayError($"Denied: {ex.Message}");
            return false;
        }
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

        [CommandOption("--env <ENV>")]
        [Description("Extra environment variables (KEY=VALUE) forwarded into the container")]
        public string[]? EnvironmentVariables { get; set; }

        [CommandOption("--server <URL>")]
        [Description("Agentic server URL forwarded as AGENTIC_SERVER into the container (e.g. https://my-tunnel.devtunnels.ms)")]
        public string? AgenticServer { get; set; }

        [CommandOption("--inline")]
        [Description("Run inline on this machine (no devcontainer). Only honored by commands that support it, e.g. 'pks claude'.")]
        public bool Inline { get; set; }
    }

    /// <summary>
    /// Hook invoked before any spawn flow runs. Lets a subcommand offer an alternate, non-devcontainer
    /// launch (e.g. <c>pks claude --inline</c> runs claude in the current shell). Return a non-null exit
    /// code to short-circuit the normal devcontainer flow; return null to continue as usual.
    /// </summary>
    protected virtual Task<int?> TryPreLaunchAsync(CommandContext context, Settings settings)
        => Task.FromResult<int?>(null);

    /// <summary>
    /// Extra entries appended to the "Where would you like to spawn?" location prompt. Default: none.
    /// </summary>
    protected virtual IEnumerable<string> GetExtraLaunchChoices() => Array.Empty<string>();

    /// <summary>
    /// Handles a location-prompt entry returned by <see cref="GetExtraLaunchChoices"/>.
    /// Return a non-null exit code to short-circuit the normal flow.
    /// </summary>
    protected virtual Task<int?> HandleExtraLaunchChoiceAsync(string choice, CommandContext context, Settings settings)
        => Task.FromResult<int?>(null);

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

            // Alternate launch (e.g. `pks claude --inline` runs claude in this shell, no container).
            var preLaunch = await TryPreLaunchAsync(context, settings);
            if (preLaunch.HasValue) return preLaunch.Value;

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
                {
                    const string LocalOption = "Local (this machine)";
                    const string NewVmOption = "Spawn new VM...";
                    var extraChoices = GetExtraLaunchChoices().ToList();
                    var choices = extraChoices
                        .Append(LocalOption)
                        .Concat(targets.Select(t => t.Label ?? t.Host))
                        .Append(NewVmOption)
                        .ToList();
                    var choice = Console.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[cyan]Where would you like to spawn the devcontainer?[/]")
                            .AddChoices(choices));

                    if (extraChoices.Contains(choice))
                    {
                        var handled = await HandleExtraLaunchChoiceAsync(choice, context, settings);
                        if (handled.HasValue) return handled.Value;
                    }
                    else if (choice == NewVmOption)
                    {
                        if (_vmInitCommand == null)
                        {
                            DisplayError("VM provisioning not available in this context.");
                            return 1;
                        }
                        _vmInitCommand.Execute(context, new VmInitCommand.Settings());

                        // Re-read targets and pick the newest one (last registered)
                        var updated = await _sshTargetService.ListTargetsAsync();
                        var newTarget = updated.OrderByDescending(t => t.RegisteredAt)
                                               .FirstOrDefault(t => !targets.Any(o => o.Id == t.Id));
                        if (newTarget != null)
                            remoteTarget = newTarget;
                        else
                            return 0; // vm init was cancelled or failed — exit cleanly
                    }
                    else if (choice != LocalOption)
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
            var projectName = AdjustProjectName(Path.GetFileName(projectPath), settings);

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
        // Spectre.Console can bind a subcommand's positional arg (e.g. gameId) to the inherited
        // ProjectPath when both sit at index 0. Fall back to CWD if the resolved path doesn't exist.
        if (!Directory.Exists(projectPath))
            projectPath = Directory.GetCurrentDirectory();
        var projectName = AdjustProjectName(Path.GetFileName(projectPath), settings);

        DisplayInfo($"Spawning on remote: {target.Label ?? target.Host} ({target.Username}@{target.Host})");

        // Spawning a devcontainer on a remote VM runs code there (and may auto-start it) — gate it.
        // Composes vm.start, so if both are on the user is prompted once.
        if (!await TryRequireAsync(new ActionRequest(ActionIds.DevcontainerSpawnRemote,
                $"Spawn a devcontainer on remote '{target.Label ?? target.Host}'")))
            return 1;

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

                // Inject managed settings into the running container (bind mount only applies at creation
                // time via devcontainer up; for reattach we write directly via docker exec).
                await InjectManagedSettingsIntoContainerAsync(sshArgs, target, picked.Id);

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
            if (!scpResult.Success) throw new Exception($"Failed to copy files to remote host: {scpResult.Error}");
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
                                if (!scpResult.Success)
                                    DisplayWarning($"Could not upload .devcontainer from template: {scpResult.Error}");
                            }
                            else
                            {
                                // No .devcontainer sub-folder — copy the whole extracted content
                                var scpResult = await RunScpAsync(target, tempDir, $"~/pks-workspaces/{projectName}/.devcontainer");
                                if (!scpResult.Success)
                                    DisplayWarning($"Could not upload template content: {scpResult.Error}");
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
        var managedSettingsMountArg = await BuildClaudeManagedSettingsMountAsync(target, sshArgs, volumeName);
        var cmd = $"cd ~/pks-workspaces/{projectName} && devcontainer up --workspace-folder .{managedSettingsMountArg ?? ""} 2>&1";

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

        // Inject managed settings into the container (bind mount via devcontainer up --mount is
        // unreliable when reusing an existing volume/container; docker exec is the reliable path).
        if (!string.IsNullOrEmpty(result.ContainerId))
            await InjectManagedSettingsIntoContainerAsync(sshArgs, target, result.ContainerId);

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

    /// <summary>
    /// Override in subclasses to produce a unique project name per invocation.
    /// The default returns the path basename unchanged.
    /// </summary>
    protected virtual string AdjustProjectName(string projectName, Settings settings) => projectName;

    protected virtual async Task OnAfterRemoteSpawnAsync(
        SshTarget target, string sshArgs, string projectName, string? containerId,
        string remoteWorkspaceFolder, string volumeName, Settings settings)
    {
        var openVsCode = !settings.NoLaunchVsCode && Console.Confirm(
            "[cyan]Open VS Code connected to remote devcontainer?[/]", defaultValue: true);

        if (openVsCode)
        {
            var (detectedHostPath, detectedVolumeName) = await DetectDevContainerMountAsync(
                sshArgs, target, containerId, projectName);
            await LaunchVsCodeRemoteAsync(target, sshArgs, remoteWorkspaceFolder, Console,
                hostPath: detectedHostPath, volumeName: detectedVolumeName, projectFolder: projectName);
        }
    }

    /// <summary>
    /// Inspects a running devcontainer to determine how its workspace was mounted so
    /// VS Code can open it via the correct URI form (hostPath vs volumeName).
    /// Returns (hostPath, null) for bind-mount containers or (null, volumeName) for
    /// volume-backed containers. Falls back to hostPath derived from the project name.
    /// </summary>
    protected async Task<(string? hostPath, string? volumeName)> DetectDevContainerMountAsync(
        string sshArgs, SshTarget target, string? containerId, string projectName)
    {
        if (!string.IsNullOrEmpty(containerId))
        {
            // Dump labels and mounts as JSON — avoids quoting issues with label keys that
            // contain dots when passing Go template string arguments through SSH.
            var inspectResult = await RunSshCommandAsync(sshArgs, target,
                $"docker inspect --format '{{{{json .}}}}' {containerId} 2>/dev/null",
                timeoutSeconds: 10);

            if (inspectResult.Success && !string.IsNullOrWhiteSpace(inspectResult.Output))
            {
                try
                {
                    using var doc = JsonDocument.Parse(inspectResult.Output.Trim());
                    var root = doc.RootElement;

                    // devcontainer.local_folder label is set by VS Code on all devcontainers
                    if (root.TryGetProperty("Config", out var config) &&
                        config.TryGetProperty("Labels", out var labels) &&
                        labels.TryGetProperty("devcontainer.local_folder", out var localFolderEl))
                    {
                        var localFolder = localFolderEl.GetString();
                        if (!string.IsNullOrEmpty(localFolder))
                            return (localFolder, null);
                    }

                    // No local_folder label — look for a named volume at /workspaces
                    if (root.TryGetProperty("Mounts", out var mounts))
                    {
                        foreach (var mount in mounts.EnumerateArray())
                        {
                            if (mount.TryGetProperty("Type", out var t) && t.GetString() == "volume" &&
                                mount.TryGetProperty("Destination", out var dest) &&
                                mount.TryGetProperty("Name", out var name) &&
                                dest.GetString()?.StartsWith("/workspaces") == true)
                            {
                                return (null, name.GetString());
                            }
                        }
                    }
                }
                catch { /* fall through to default */ }
            }
        }

        // Fallback: derive host path from convention used by pks devcontainer spawn
        return ($"/home/{target.Username}/pks-workspaces/{projectName}", null);
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

        // Gate the (billable) start — the Confirm above is agent-answerable; this is not.
        // Skipped if devcontainer.spawn.remote already satisfied it this run.
        if (!await TryRequireAsync(new ActionRequest(ActionIds.VmStart,
                $"Start VM '{vmRecord.VmName}' (Azure) to spawn the devcontainer")))
            return false;

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

    /// <summary>
    /// Injects the current managed-settings.json directly into a running container via docker exec.
    /// Used when reattaching to an existing container (bind mount only applies at devcontainer up time).
    /// </summary>
    private async Task InjectManagedSettingsIntoContainerAsync(string sshArgs, SshTarget target, string containerId)
    {
        if (_claudeMarketplaceConfigService == null || _claudeManagedSettingsRenderer == null)
            return;

        var config = await _claudeMarketplaceConfigService.LoadAsync();
        if (config.Marketplaces.Count == 0)
            return;

        var json = _claudeManagedSettingsRenderer.Render(config);
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        // --user root: /etc/ requires root; docker exec default user may be non-root in some images.
        var cmd = $"docker exec --user root {containerId} sh -c 'mkdir -p /etc/claude-code && echo {b64} | base64 -d > /etc/claude-code/managed-settings.json'";
        var result = await RunSshCommandAsync(sshArgs, target, cmd, timeoutSeconds: 15);
        if (result.Success)
            Console.MarkupLine("[dim]Marketplace settings injected into container.[/]");
        else
            Console.MarkupLine($"[yellow]Warning: could not inject marketplace settings into container: {Markup.Escape(result.Output.Trim())}[/]");
    }

    /// <summary>
    /// Builds the Claude managed-settings bind mount argument for devcontainer up.
    /// Returns null if there are no marketplaces configured.
    /// </summary>
    protected virtual async Task<string?> BuildClaudeManagedSettingsMountAsync(
        SshTarget target,
        string sshArgs,
        string scopeId,
        Func<string, string, Task<string>>? sshSetupOverride = null)
    {
        if (_claudeMarketplaceConfigService == null || _claudeManagedSettingsRenderer == null)
            return null;

        var config = await _claudeMarketplaceConfigService.LoadAsync();
        if (config.Marketplaces.Count == 0)
            return null;

        var json = _claudeManagedSettingsRenderer.Render(config);

        string absoluteRemoteDir;
        if (sshSetupOverride != null)
        {
            absoluteRemoteDir = await sshSetupOverride(scopeId, json);
        }
        else
        {
            // Single SSH round-trip: mkdir + write (base64-decoded to dodge quote escaping)
            // + chmod (read-only at source side, since `devcontainer up --mount` doesn't accept
            // `,readonly`) + echo absolute path so we can use $HOME in the bind source.
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var dirVar = $"$HOME/.pks-cli/managed-settings/{scopeId}";
            var fileVar = $"{dirVar}/managed-settings.json";
            var script = $"mkdir -p {dirVar} && echo {b64} | base64 -d > {fileVar} && chmod 0444 {fileVar} && chmod 0555 {dirVar} && echo {dirVar}";
            var (success, output) = await RunSshCommandAsync(sshArgs, target, script);
            if (!success || string.IsNullOrWhiteSpace(output))
                return null;
            absoluteRemoteDir = output.Trim().Split('\n').Last().Trim();
        }

        return $" --mount type=bind,source={absoluteRemoteDir},target=/etc/claude-code";
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

    private static async Task<(bool Success, string Error)> RunScpAsync(SshTarget target, string localPath, string remotePath)
    {
        try
        {
            var keyArg = !string.IsNullOrEmpty(target.KeyPath) ? $"-i \"{target.KeyPath}\" " : string.Empty;
            // Normalize to forward slashes for scp (required on Windows)
            var normalizedPath = localPath.Replace('\\', '/');
            // Append /. to copy directory contents (not the directory itself)
            var sourcePath = normalizedPath.TrimEnd('/') + "/.";
            var psi = new ProcessStartInfo("scp")
            {
                Arguments = $"-r {keyArg}-P {target.Port} -o StrictHostKeyChecking=no \"{sourcePath}\" {target.Username}@{target.Host}:{remotePath}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, "Failed to start scp process");

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
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
            // hostPath + localDocker:false — VS Code resolves the devcontainer.json relative to
            // hostPath ON the SSH remote host. Do NOT include configFile here: the configFile
            // authority:""  means "local machine", which causes VS Code on Windows to try to
            // lstat the path locally rather than over the SSH remote connection.
            dcJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["hostPath"] = hostPath ?? string.Empty,
                ["localDocker"] = false
            });
        }

        var dcHex = ToHex(dcJson);
        // ssh-remote hex: VS Code expects {"hostName":"...","user":"..."}, NOT plain user@host
        var sshHex = ToHex(JsonSerializer.Serialize(new { hostName = target.Host, user = target.Username }));

        var remoteAuthority = $"dev-container+{dcHex}@ssh-remote+{sshHex}";
        var uri = $"vscode-remote://{remoteAuthority}{remoteWorkspaceFolder}";
        return (uri, remoteAuthority);
    }

    protected static async Task LaunchVsCodeRemoteAsync(
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
                var safeHost = Regex.Replace(target.Host, @"[^a-zA-Z0-9._-]", "-");
                var safeFolder = Regex.Replace(Path.GetFileName(remoteWorkspaceFolder.TrimEnd('/')), @"[^a-zA-Z0-9._-]", "-");
                var workspaceFile = Path.Combine(vscodeDir, $"{safeHost}-{safeFolder}.code-workspace");

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

    /// <summary>
    /// If Azure AI Foundry credentials are configured, prompts the user to choose between
    /// Foundry models and Anthropic direct, deploys the MSI token server on the remote VM,
    /// and returns a string of "-e VAR=VAL" docker exec flags. Returns empty string otherwise.
    /// </summary>
    protected async Task<string> BuildFoundryEnvArgsAsync(string sshArgs, SshTarget target)
    {
        if (_foundryAuthService == null) return "";
        if (!await _foundryAuthService.IsAuthenticatedAsync()) return "";

        const string UseFoundry = "Use Foundry models (Azure AI)";
        const string UseDirect = "Use Anthropic direct";
        var launchMode = Console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Azure AI Foundry is configured. Launch mode:[/]")
                .AddChoices(UseFoundry, UseDirect));

        if (launchMode != UseFoundry) return "";

        var creds = await _foundryAuthService.GetStoredCredentialsAsync();
        var enabledModels = creds!.EnabledModels.Count > 0 ? creds.EnabledModels : new List<string> { creds.DefaultModel };

        const int msiPort = 40342;
        const string credsFile = "~/.pks-cli/foundry-credentials.json";
        const string scriptFile = "/tmp/pks-msi-server.py";
        const string secretFile = "~/.pks-cli/msi-server-secret";
        const string logFile = "/tmp/pks-msi-server.log";

        // The MSI token server is a shared VM-level service — one instance serves all containers.
        // Check if it is already running; if yes, just read the existing secret and reuse the server.
        var checkResult = await RunSshCommandAsync(sshArgs, target,
            $"ss -tlnp 2>/dev/null | grep -q :{msiPort} && cat {secretFile} 2>/dev/null || echo ''",
            timeoutSeconds: 5);
        var existingSecret = checkResult.Success ? checkResult.Output.Trim() : "";

        string msiSecret;
        if (!string.IsNullOrEmpty(existingSecret))
        {
            msiSecret = existingSecret;
            Console.MarkupLine($"[dim]Reusing existing MSI token server on VM port {msiPort}.[/]");
        }
        else
        {
            // Server not running — (re)deploy it. Always refresh credentials so the stored token
            // is up-to-date even if a previous server was killed externally.
            msiSecret = Guid.NewGuid().ToString("N");

            var credsPayload = JsonSerializer.Serialize(new { creds!.TenantId, creds.RefreshToken });
            var credsB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credsPayload));
            await RunSshCommandAsync(sshArgs, target,
                $"mkdir -p ~/.pks-cli && echo {credsB64} | base64 -d > {credsFile} && chmod 600 {credsFile} && echo {msiSecret} > {secretFile} && chmod 600 {secretFile}",
                timeoutSeconds: 10);

            var pythonScript = $@"#!/usr/bin/env python3
import http.server, json, urllib.request, urllib.parse, os, datetime, time
CREDS = os.path.expanduser('{credsFile}')
ALLOWED = 'https://cognitiveservices.azure.com'
CLIENT_ID = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'
PORT = {msiPort}
SECRET = open(os.path.expanduser('{secretFile}')).read().strip()
LOG = '{logFile}'
def log(msg):
    with open(LOG, 'a') as f:
        f.write(datetime.datetime.utcnow().isoformat() + ' ' + msg + '\n')
def get_token(tenant_id, refresh_tok):
    data = urllib.parse.urlencode({{'grant_type': 'refresh_token', 'client_id': CLIENT_ID,
        'refresh_token': refresh_tok, 'scope': 'https://cognitiveservices.azure.com/.default'}}).encode()
    req = urllib.request.Request('https://login.microsoftonline.com/' + tenant_id + '/oauth2/v2.0/token',
        data=data, method='POST')
    req.add_header('Content-Type', 'application/x-www-form-urlencoded')
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read())
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.headers.get('X-IDENTITY-HEADER', '') != SECRET:
            log('REJECT bad header')
            self.send_response(401); self.end_headers(); return
        params = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        resource = params.get('resource', [None])[0]
        if resource != ALLOWED:
            log('REJECT resource=' + repr(resource))
            self.send_response(403); self.end_headers()
            self.wfile.write(b'{{""error"":""resource_not_allowed""}}'); return
        try:
            with open(CREDS) as f: c = json.load(f)
            tok = get_token(c['TenantId'], c['RefreshToken'])
            if 'refresh_token' in tok:
                c['RefreshToken'] = tok['refresh_token']
                with open(CREDS, 'w') as f: json.dump(c, f)
            expires_in = tok.get('expires_in', 3600)
            body = json.dumps({{'access_token': tok['access_token'],
                'expires_in': str(expires_in),
                'expires_on': str(int(time.time()) + int(expires_in)),
                'resource': ALLOWED, 'token_type': 'Bearer'}}).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers(); self.wfile.write(body)
            log('TOKEN issued ok')
        except Exception as e:
            log('ERROR ' + str(e))
            self.send_response(500); self.end_headers()
            self.wfile.write(json.dumps({{'error': str(e)}}).encode())
    def log_message(self, *a): pass
with open('/tmp/pks-msi-server.pid', 'w') as f: f.write(str(os.getpid()))
log('MSI token server starting port=' + str(PORT))
http.server.HTTPServer(('0.0.0.0', PORT), H).serve_forever()
";
            var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pythonScript));
            await RunSshCommandAsync(sshArgs, target,
                $"echo {scriptB64} | base64 -d > {scriptFile} && nohup python3 {scriptFile} >{logFile}.out 2>&1 &",
                timeoutSeconds: 10);
            await Task.Delay(800);
            Console.MarkupLine($"[dim]MSI token server started on VM port {msiPort}. Log: {logFile}[/]");
        }

        var envVars = new StringBuilder();
        envVars.Append($"-e CLAUDE_CODE_USE_FOUNDRY=1 ");
        envVars.Append($"-e ANTHROPIC_FOUNDRY_RESOURCE={creds.SelectedResourceName} ");
        envVars.Append($"-e IDENTITY_ENDPOINT=http://172.17.0.1:{msiPort} ");
        envVars.Append($"-e IDENTITY_HEADER={msiSecret} ");

        if (!string.IsNullOrEmpty(creds.ApiKey))
            envVars.Append($"-e ANTHROPIC_FOUNDRY_API_KEY={creds.ApiKey} ");

        // Only map Claude deployments to a tier — never let a non-Claude deployment (tts, image,
        // embeddings, gpt-*) clobber the Sonnet default, which would launch claude on the wrong model.
        foreach (var model in enabledModels)
        {
            var lower = model.ToLowerInvariant();
            if (lower.Contains("sonnet")) envVars.Append($"-e ANTHROPIC_DEFAULT_SONNET_MODEL={model} ");
            else if (lower.Contains("opus")) envVars.Append($"-e ANTHROPIC_DEFAULT_OPUS_MODEL={model} ");
            else if (lower.Contains("haiku")) envVars.Append($"-e ANTHROPIC_DEFAULT_HAIKU_MODEL={model} ");
        }

        return envVars.ToString().TrimEnd();
    }
}
