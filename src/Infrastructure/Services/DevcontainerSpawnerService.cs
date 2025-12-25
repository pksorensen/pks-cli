using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for spawning devcontainers in Docker volumes
/// </summary>
public class DevcontainerSpawnerService : IDevcontainerSpawnerService
{
    private readonly ILogger<DevcontainerSpawnerService> _logger;
    private readonly IDockerClient _dockerClient;
    private readonly IAnsiConsole? _console;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevcontainerSpawnerService"/> class
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="dockerClient">Docker client for container operations</param>
    /// <param name="console">Optional console for progress indicators</param>
    public DevcontainerSpawnerService(
        ILogger<DevcontainerSpawnerService> logger,
        IDockerClient dockerClient,
        IAnsiConsole? console = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dockerClient = dockerClient ?? throw new ArgumentNullException(nameof(dockerClient));
        _console = console;
    }

    /// <inheritdoc/>
    public async Task<DevcontainerSpawnResult> SpawnLocalAsync(DevcontainerSpawnOptions options)
    {
        var startTime = DateTime.UtcNow;
        var result = new DevcontainerSpawnResult
        {
            CompletedStep = DevcontainerSpawnStep.None
        };

        string? volumeName = null;
        string? bootstrapPath = null;

        try
        {
            _logger.LogInformation("Starting devcontainer spawn for project: {ProjectName}", options.ProjectName);

            // Step 1: Pre-flight checks - Docker availability
            _logger.LogInformation("Checking Docker availability...");
            result.CompletedStep = DevcontainerSpawnStep.DockerCheck;

            var dockerCheck = await CheckDockerAvailabilityAsync();
            if (!dockerCheck.IsAvailable || !dockerCheck.IsRunning)
            {
                result.Success = false;
                result.Message = "Docker is not available or not running";
                result.Errors.Add(dockerCheck.Message);
                return result;
            }

            _logger.LogInformation("Docker is available (version: {Version})", dockerCheck.Version);

            // Step 2: Check devcontainer CLI
            _logger.LogInformation("Checking devcontainer CLI...");
            result.CompletedStep = DevcontainerSpawnStep.DevcontainerCliCheck;

            var cliInstalled = await IsDevcontainerCliInstalledAsync();
            if (!cliInstalled)
            {
                result.Success = false;
                result.Message = "devcontainer CLI is not installed";
                result.Errors.Add("Please install the devcontainer CLI: npm install -g @devcontainers/cli");
                return result;
            }

            _logger.LogInformation("devcontainer CLI is installed");

            // Check for existing container if reuse is enabled
            if (options.ReuseExisting)
            {
                var existing = await FindExistingContainerAsync(options.ProjectPath);
                if (existing != null)
                {
                    _logger.LogInformation("Found existing container: {ContainerId}", existing.ContainerId);
                    result.Success = true;
                    result.ContainerId = existing.ContainerId;
                    result.VolumeName = existing.VolumeName;
                    result.Message = "Reusing existing devcontainer";
                    result.CompletedStep = DevcontainerSpawnStep.Completed;
                    result.Duration = DateTime.UtcNow - startTime;

                    // Launch VS Code if requested
                    if (options.LaunchVsCode)
                    {
                        var vsCodeInfo = await CheckVsCodeInstallationAsync();
                        if (vsCodeInfo.IsInstalled && vsCodeInfo.ExecutablePath != null)
                        {
                            // TODO: Construct proper VS Code URI for existing container
                            _logger.LogInformation("VS Code launch requested for existing container");
                        }
                    }

                    return result;
                }
            }

            // Step 3: Create Docker volume
            _logger.LogInformation("Creating Docker volume...");
            result.CompletedStep = DevcontainerSpawnStep.VolumeCreation;

            volumeName = options.VolumeName ?? GenerateVolumeName(options.ProjectName);
            await CreateVolumeAsync(volumeName, new Dictionary<string, string>
            {
                { "devcontainer.project", options.ProjectName },
                { "pks.managed", "true" },
                { "devcontainer.created", DateTime.UtcNow.ToString("o") },
                { "vsch.local.repository.volume", volumeName }
            });

            result.VolumeName = volumeName;
            _logger.LogInformation("Created volume: {VolumeName}", volumeName);

            // Step 4: Copy files to volume
            if (options.CopySourceFiles)
            {
                _logger.LogInformation("Copying source files to volume...");
                result.CompletedStep = DevcontainerSpawnStep.FileCopy;

                await CopyToVolumeAsync(
                    options.ProjectPath,
                    volumeName,
                    $"/workspaces/{options.ProjectName}");

                _logger.LogInformation("Files copied to volume");
            }

            // Step 5: Create bootstrap workspace
            _logger.LogInformation("Creating bootstrap workspace...");
            result.CompletedStep = DevcontainerSpawnStep.BootstrapCreation;

            bootstrapPath = await CreateBootstrapWorkspaceAsync(
                options.ProjectName,
                volumeName,
                options.DevcontainerPath);

            _logger.LogInformation("Bootstrap workspace created at: {BootstrapPath}", bootstrapPath);

            // Step 6: Run devcontainer up
            _logger.LogInformation("Running devcontainer up...");
            result.CompletedStep = DevcontainerSpawnStep.DevcontainerUp;

            var upResult = await RunDevcontainerUpAsync(bootstrapPath);
            if (upResult.Outcome != "success")
            {
                result.Success = false;
                result.Message = $"devcontainer up failed: {upResult.Outcome}";
                result.Errors.Add($"devcontainer CLI returned outcome: {upResult.Outcome}");
                await CleanupFailedSpawnAsync(volumeName, bootstrapPath);
                return result;
            }

            result.ContainerId = upResult.ContainerId;
            _logger.LogInformation("Container created: {ContainerId}", upResult.ContainerId);

            // Step 7: Launch VS Code
            if (options.LaunchVsCode)
            {
                _logger.LogInformation("Launching VS Code...");
                result.CompletedStep = DevcontainerSpawnStep.VsCodeLaunch;

                var vsCodeInfo = await CheckVsCodeInstallationAsync();
                if (vsCodeInfo.IsInstalled && vsCodeInfo.ExecutablePath != null)
                {
                    var uri = ConstructVsCodeUri(bootstrapPath, upResult.RemoteWorkspaceFolder);
                    result.VsCodeUri = uri;

                    await LaunchVsCodeAsync(uri, vsCodeInfo.ExecutablePath);
                    _logger.LogInformation("VS Code launched with URI: {Uri}", uri);
                }
                else
                {
                    result.Warnings.Add("VS Code is not installed, skipping launch");
                }
            }

            result.Success = true;
            result.Message = "Devcontainer spawned successfully";
            result.CompletedStep = DevcontainerSpawnStep.Completed;
            result.Duration = DateTime.UtcNow - startTime;

            _logger.LogInformation("Devcontainer spawn completed successfully in {Duration}ms",
                result.Duration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Devcontainer spawn failed at step {Step}", result.CompletedStep);

            // Cleanup on failure
            if (volumeName != null)
            {
                await CleanupFailedSpawnAsync(volumeName, bootstrapPath);
            }

            result.Success = false;
            result.Message = $"Devcontainer spawn failed: {ex.Message}";
            result.Errors.Add(ex.Message);
            result.Duration = DateTime.UtcNow - startTime;

            return result;
        }
    }

    /// <inheritdoc/>
    public async Task<DockerAvailabilityResult> CheckDockerAvailabilityAsync()
    {
        try
        {
            _logger.LogDebug("Pinging Docker daemon...");

            // Try to ping Docker
            await _dockerClient.System.PingAsync();
            var pingSuccessful = true;

            if (pingSuccessful)
            {
                // Get Docker version
                var version = await _dockerClient.System.GetVersionAsync();

                return new DockerAvailabilityResult
                {
                    IsAvailable = true,
                    IsRunning = true,
                    Version = version.Version,
                    Message = $"Docker is running (version {version.Version})"
                };
            }

            return new DockerAvailabilityResult
            {
                IsAvailable = false,
                IsRunning = false,
                Message = "Docker ping failed"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker availability check failed");

            return new DockerAvailabilityResult
            {
                IsAvailable = false,
                IsRunning = false,
                Message = $"Docker is not available: {ex.Message}"
            };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsDevcontainerCliInstalledAsync()
    {
        try
        {
            _logger.LogDebug("Checking for devcontainer CLI...");

            // Try different approaches to detect devcontainer CLI
            // On Windows, we need to be more flexible with shell execution

            // Approach 1: Direct execution
            try
            {
                var output = await RunCommandAsync("devcontainer", "--version");
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogDebug("devcontainer CLI found (direct): {Output}", output);
                    return true;
                }
            }
            catch (Exception ex1)
            {
                _logger.LogDebug(ex1, "Direct devcontainer CLI check failed, trying shell execution");
            }

            // Approach 2: Shell execution (Windows PowerShell/CMD)
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c devcontainer --version",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    var output = await process.StandardOutput.ReadToEndAsync();
                    var error = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync();

                    if (!string.IsNullOrEmpty(output) && (output.Contains("devcontainer") || output.Contains("@devcontainers")))
                    {
                        _logger.LogDebug("devcontainer CLI found (shell): {Output}", output);
                        return true;
                    }
                }
                catch (Exception ex2)
                {
                    _logger.LogDebug(ex2, "Shell devcontainer CLI check failed");
                }
            }

            // Approach 3: Check if it's in PATH using 'where' (Windows) or 'which' (Unix)
            try
            {
                var whereCommand = OperatingSystem.IsWindows() ? "where" : "which";
                var output = await RunCommandAsync(whereCommand, "devcontainer");
                if (!string.IsNullOrEmpty(output))
                {
                    _logger.LogDebug("devcontainer CLI found in PATH: {Output}", output);
                    return true;
                }
            }
            catch (Exception ex3)
            {
                _logger.LogDebug(ex3, "PATH check for devcontainer CLI failed");
            }

            _logger.LogDebug("devcontainer CLI not found");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "devcontainer CLI check failed completely");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<VsCodeInstallationInfo> CheckVsCodeInstallationAsync()
    {
        try
        {
            _logger.LogDebug("Checking for VS Code installation...");

            // Try 'code' first (stable)
            try
            {
                var output = await RunCommandAsync("code", "--version");
                if (!string.IsNullOrEmpty(output) && !output.Contains("not found"))
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var version = lines.Length > 0 ? lines[0].Trim() : "unknown";

                    return new VsCodeInstallationInfo
                    {
                        IsInstalled = true,
                        ExecutablePath = "code",
                        Version = version,
                        Edition = VsCodeEdition.Stable
                    };
                }
            }
            catch
            {
                // Try insiders
            }

            // Try 'code-insiders'
            try
            {
                var output = await RunCommandAsync("code-insiders", "--version");
                if (!string.IsNullOrEmpty(output) && !output.Contains("not found"))
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var version = lines.Length > 0 ? lines[0].Trim() : "unknown";

                    return new VsCodeInstallationInfo
                    {
                        IsInstalled = true,
                        ExecutablePath = "code-insiders",
                        Version = version,
                        Edition = VsCodeEdition.Insiders
                    };
                }
            }
            catch
            {
                // Not installed
            }

            return new VsCodeInstallationInfo
            {
                IsInstalled = false
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VS Code installation check failed");

            return new VsCodeInstallationInfo
            {
                IsInstalled = false
            };
        }
    }

    /// <inheritdoc/>
    public string GenerateVolumeName(string projectName)
    {
        // Sanitize project name: lowercase, alphanumeric + dash/underscore only
        var sanitized = Regex.Replace(projectName.ToLowerInvariant(), @"[^a-z0-9-_]", "");

        // Remove consecutive dashes/underscores
        sanitized = Regex.Replace(sanitized, @"[-_]+", "-");

        // Trim leading/trailing dashes/underscores
        sanitized = sanitized.Trim('-', '_');

        // Generate 8-character GUID suffix
        var guidSuffix = Guid.NewGuid().ToString("N")[..8];

        return $"devcontainer-{sanitized}-{guidSuffix}";
    }

    /// <inheritdoc/>
    public async Task CleanupFailedSpawnAsync(string volumeName, string? bootstrapPath = null)
    {
        _logger.LogInformation("Cleaning up failed spawn (volume: {VolumeName}, bootstrap: {BootstrapPath})",
            volumeName, bootstrapPath ?? "none");

        try
        {
            // Remove Docker volume
            if (!string.IsNullOrEmpty(volumeName))
            {
                try
                {
                    _logger.LogDebug("Removing Docker volume: {VolumeName}", volumeName);
                    await _dockerClient.Volumes.RemoveAsync(volumeName, force: true);
                    _logger.LogInformation("Removed Docker volume: {VolumeName}", volumeName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove Docker volume: {VolumeName}", volumeName);
                }
            }

            // Delete bootstrap directory
            if (!string.IsNullOrEmpty(bootstrapPath) && Directory.Exists(bootstrapPath))
            {
                try
                {
                    _logger.LogDebug("Deleting bootstrap directory: {BootstrapPath}", bootstrapPath);
                    Directory.Delete(bootstrapPath, recursive: true);
                    _logger.LogInformation("Deleted bootstrap directory: {BootstrapPath}", bootstrapPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete bootstrap directory: {BootstrapPath}", bootstrapPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cleanup");
        }
    }

    /// <inheritdoc/>
    public async Task<ExistingDevcontainerInfo?> FindExistingContainerAsync(string projectPath)
    {
        try
        {
            _logger.LogDebug("Searching for existing container for project: {ProjectPath}", projectPath);

            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            [$"devcontainer.local_folder={projectPath}"] = true
                        }
                    }
                });

            var container = containers.FirstOrDefault();
            if (container == null)
            {
                _logger.LogDebug("No existing container found for project: {ProjectPath}", projectPath);
                return null;
            }

            var volumeName = container.Labels.TryGetValue("vsch.local.repository.volume", out var vol)
                ? vol
                : string.Empty;

            // container.Created is DateTime type in Docker.DotNet
            var createdDate = container.Created;

            var info = new ExistingDevcontainerInfo
            {
                ContainerId = container.ID,
                VolumeName = volumeName,
                Created = createdDate,
                IsRunning = container.State == "running"
            };

            _logger.LogInformation("Found existing container: {ContainerId} (running: {IsRunning})",
                info.ContainerId, info.IsRunning);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding existing container");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<List<DevcontainerVolumeInfo>> ListManagedVolumesAsync()
    {
        try
        {
            _logger.LogDebug("Listing managed devcontainer volumes...");

            var volumes = await _dockerClient.Volumes.ListAsync(new VolumesListParameters());

            var managedVolumes = volumes.Volumes
                .Where(v => v.Labels != null &&
                           v.Labels.ContainsKey("pks.managed") &&
                           v.Labels["pks.managed"] == "true")
                .Select(v => new DevcontainerVolumeInfo
                {
                    Name = v.Name,
                    ProjectName = v.Labels.TryGetValue("devcontainer.project", out var proj) ? proj : "unknown",
                    Created = v.Labels.TryGetValue("devcontainer.created", out var created) &&
                              DateTime.TryParse(created, out var createdDate)
                        ? createdDate
                        : DateTime.MinValue,
                    Labels = v.Labels != null ? new Dictionary<string, string>(v.Labels) : new Dictionary<string, string>()
                })
                .ToList();

            _logger.LogInformation("Found {Count} managed volumes", managedVolumes.Count);

            return managedVolumes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing managed volumes");
            return new List<DevcontainerVolumeInfo>();
        }
    }

    /// <inheritdoc/>
    public async Task<List<DevcontainerContainerInfo>> ListManagedContainersAsync()
    {
        try
        {
            _logger.LogDebug("Listing managed devcontainer containers...");

            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["label"] = new Dictionary<string, bool>
                        {
                            ["pks.managed=true"] = true
                        }
                    }
                });

            var managedContainers = containers
                .Select(c => new DevcontainerContainerInfo
                {
                    ContainerId = c.ID,
                    ContainerName = c.Names?.FirstOrDefault()?.TrimStart('/') ?? string.Empty,
                    ProjectName = c.Labels?.TryGetValue("devcontainer.project", out var proj) == true ? proj : "unknown",
                    VolumeName = c.Labels?.TryGetValue("vsch.local.repository.volume", out var vol) == true ? vol : string.Empty,
                    Status = c.State,
                    CreatedDate = c.Created,
                    Labels = c.Labels != null ? new Dictionary<string, string>(c.Labels) : new Dictionary<string, string>(),
                    WorkspaceFolder = c.Labels?.TryGetValue("devcontainer.workspace_folder", out var ws) == true ? ws : string.Empty
                })
                .ToList();

            _logger.LogInformation("Found {Count} managed containers", managedContainers.Count);

            return managedContainers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing managed containers");
            return new List<DevcontainerContainerInfo>();
        }
    }

    /// <inheritdoc/>
    public async Task<string> GetContainerVsCodeUriAsync(string containerId, string workspaceFolder)
    {
        try
        {
            _logger.LogDebug("Constructing VS Code URI for container {ContainerId}", containerId);

            // Hex encode the container ID for VS Code remote URI
            var hexEncodedContainerId = Convert.ToHexString(Encoding.UTF8.GetBytes(containerId)).ToLowerInvariant();

            // Format: vscode-remote://attached-container+{hexEncodedContainerId}/{workspaceFolder}
            var uri = $"vscode-remote://attached-container+{hexEncodedContainerId}{workspaceFolder}";

            _logger.LogDebug("Constructed VS Code URI: {Uri}", uri);

            await Task.CompletedTask;
            return uri;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error constructing VS Code URI for container {ContainerId}", containerId);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<DevcontainerSpawnResult> SpawnRemoteAsync(
        DevcontainerSpawnOptions options,
        RemoteHostConfig remoteHost)
    {
        throw new NotImplementedException("Remote spawning will be implemented in Phase 2");
    }

    #region Private Helper Methods

    /// <summary>
    /// Creates a Docker volume with specified labels
    /// </summary>
    private async Task<string> CreateVolumeAsync(string volumeName, Dictionary<string, string> labels)
    {
        _logger.LogDebug("Creating Docker volume: {VolumeName}", volumeName);

        var volumeResponse = await _dockerClient.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = volumeName,
            Labels = labels
        });

        return volumeResponse.Name;
    }

    /// <summary>
    /// Copies files from local path to Docker volume using a temporary container
    /// </summary>
    private async Task CopyToVolumeAsync(string localPath, string volumeName, string containerPath)
    {
        var tempContainerName = $"devcontainer-copy-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            _logger.LogDebug("Creating temporary container for file copy: {TempContainer}", tempContainerName);

            // Create temporary Alpine container with volume mounted
            await RunDockerCommandAsync(
                $"container create --name {tempContainerName} -v {volumeName}:{containerPath} alpine:latest");

            // Copy files, excluding common development artifacts
            _logger.LogDebug("Copying files from {LocalPath} to volume {VolumeName}", localPath, volumeName);

            // Use docker cp to copy files
            await RunDockerCommandAsync($"cp \"{localPath}/.\" {tempContainerName}:{containerPath}");

            _logger.LogInformation("Files copied to volume successfully");
        }
        finally
        {
            // Clean up temporary container
            try
            {
                await RunDockerCommandAsync($"container rm {tempContainerName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove temporary container: {TempContainer}", tempContainerName);
            }
        }
    }

    /// <summary>
    /// Creates a bootstrap workspace directory with volume-aware devcontainer.json
    /// </summary>
    private async Task<string> CreateBootstrapWorkspaceAsync(
        string projectName,
        string volumeName,
        string originalDevcontainerPath)
    {
        var bootstrapPath = Path.Combine(
            Path.GetTempPath(),
            $"devcontainer-bootstrap-{projectName}-{Guid.NewGuid().ToString("N")[..8]}");

        _logger.LogDebug("Creating bootstrap workspace at: {BootstrapPath}", bootstrapPath);

        var devcontainerDir = Path.Combine(bootstrapPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);

        // Copy all files from original .devcontainer folder
        _logger.LogDebug("Copying all files from {Original} to {Bootstrap}", originalDevcontainerPath, devcontainerDir);
        foreach (var file in Directory.GetFiles(originalDevcontainerPath))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(devcontainerDir, fileName);
            File.Copy(file, destFile, overwrite: true);
            _logger.LogDebug("Copied file: {FileName}", fileName);
        }

        // Now read and modify the devcontainer.json
        var originalConfigPath = Path.Combine(devcontainerDir, "devcontainer.json");
        if (!File.Exists(originalConfigPath))
        {
            throw new FileNotFoundException($"devcontainer.json not found at: {originalConfigPath}");
        }

        var originalContent = await File.ReadAllTextAsync(originalConfigPath);
        var originalConfig = JsonSerializer.Deserialize<Dictionary<string, object>>(originalContent)
            ?? new Dictionary<string, object>();

        // Merge with volume-aware configuration
        originalConfig["workspaceMount"] = $"source={volumeName},target=/workspaces/{projectName},type=volume";
        originalConfig["workspaceFolder"] = $"/workspaces/{projectName}";

        // Add post-create command to fix permissions
        if (!originalConfig.ContainsKey("postCreateCommand"))
        {
            originalConfig["postCreateCommand"] = $"sudo chown -R vscode:vscode /workspaces/{projectName}";
        }

        // Write merged configuration back
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var mergedContent = JsonSerializer.Serialize(originalConfig, options);
        await File.WriteAllTextAsync(originalConfigPath, mergedContent);

        _logger.LogInformation("Bootstrap workspace created at: {BootstrapPath}", bootstrapPath);

        return bootstrapPath;
    }

    /// <summary>
    /// Runs devcontainer up command and parses JSON output
    /// </summary>
    private async Task<DevcontainerUpResult> RunDevcontainerUpAsync(string workspaceFolder)
    {
        _logger.LogDebug("Running devcontainer up for workspace: {WorkspaceFolder}", workspaceFolder);

        // Run devcontainer up from the workspace directory to ensure proper path resolution
        var output = await RunCommandAsync(
            "devcontainer",
            $"up --workspace-folder \"{workspaceFolder}\"",
            timeoutSeconds: 300, // 5 minute timeout
            workingDirectory: workspaceFolder);

        // Parse JSON output
        try
        {
            var result = JsonSerializer.Deserialize<DevcontainerUpResult>(output);
            if (result == null)
            {
                throw new InvalidOperationException("Failed to parse devcontainer up output");
            }

            _logger.LogInformation("devcontainer up completed with outcome: {Outcome}", result.Outcome);

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse devcontainer up output: {Output}", output);
            throw new InvalidOperationException($"Failed to parse devcontainer up output: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Constructs VS Code URI for remote connection
    /// </summary>
    private string ConstructVsCodeUri(string bootstrapPath, string remoteWorkspaceFolder)
    {
        // Hex encode bootstrap path
        var hexEncodedPath = Convert.ToHexString(Encoding.UTF8.GetBytes(bootstrapPath)).ToLowerInvariant();

        // Format: vscode-remote://dev-container+{hexEncodedPath}{remoteWorkspaceFolder}
        var uri = $"vscode-remote://dev-container+{hexEncodedPath}{remoteWorkspaceFolder}";

        _logger.LogDebug("Constructed VS Code URI: {Uri}", uri);

        return uri;
    }

    /// <summary>
    /// Launches VS Code with the specified URI
    /// </summary>
    private async Task<bool> LaunchVsCodeAsync(string uri, string vsCodePath)
    {
        try
        {
            _logger.LogDebug("Launching VS Code: {VsCodePath} --folder-uri \"{Uri}\"", vsCodePath, uri);

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

            _logger.LogInformation("VS Code launched successfully");

            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch VS Code");
            return false;
        }
    }

    /// <summary>
    /// Runs a docker CLI command
    /// </summary>
    private async Task<string> RunDockerCommandAsync(string arguments)
    {
        return await RunCommandAsync("docker", arguments);
    }

    /// <summary>
    /// Runs a command and returns its output
    /// </summary>
    private async Task<string> RunCommandAsync(string fileName, string arguments, int timeoutSeconds = 120, string? workingDirectory = null)
    {
        // On Windows, wrap npm/node commands in cmd.exe for proper PATH resolution
        // This ensures npm global binaries like 'devcontainer' are found
        string actualFileName = fileName;
        string actualArguments = arguments;

        if (OperatingSystem.IsWindows() &&
            (fileName.Equals("devcontainer", StringComparison.OrdinalIgnoreCase) ||
             fileName.Equals("code", StringComparison.OrdinalIgnoreCase) ||
             fileName.Equals("code-insiders", StringComparison.OrdinalIgnoreCase)))
        {
            actualFileName = "cmd.exe";
            actualArguments = $"/c {fileName} {arguments}";
            _logger.LogDebug("Windows: Wrapping command in cmd.exe: {Command}", actualArguments);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = actualFileName,
            Arguments = actualArguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set working directory if specified
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
            _logger.LogDebug("Setting working directory: {WorkingDirectory}", workingDirectory);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = await process.WaitForExitAsync(TimeSpan.FromSeconds(timeoutSeconds));

        if (!completed)
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Ignore kill errors
            }

            throw new TimeoutException($"Command timed out after {timeoutSeconds} seconds: {fileName} {arguments}");
        }

        var output = outputBuilder.ToString().Trim();
        var error = errorBuilder.ToString().Trim();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Command failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }

    #endregion
}

/// <summary>
/// Extension methods for process timeout
/// </summary>
internal static class ProcessExtensions
{
    public static async Task<bool> WaitForExitAsync(this Process process, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>();

        void ProcessExited(object? sender, EventArgs e)
        {
            tcs.TrySetResult(true);
        }

        process.Exited += ProcessExited;
        process.EnableRaisingEvents = true;

        if (process.HasExited)
        {
            return true;
        }

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));

        return await tcs.Task;
    }
}
