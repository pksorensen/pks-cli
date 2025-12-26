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
        BootstrapContainerInfo? bootstrapContainer = null;

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

            DevcontainerUpResult upResult;

            // Choose workflow based on bootstrap container option
            if (options.UseBootstrapContainer)
            {
                // Bootstrap container workflow - cross-platform bash support
                _logger.LogInformation("Using bootstrap container workflow for cross-platform compatibility");

                // Step 4: Ensure bootstrap image exists
                _logger.LogInformation("Ensuring bootstrap image...");
                result.CompletedStep = DevcontainerSpawnStep.BootstrapImageCheck;

                var imageResult = await EnsureBootstrapImageAsync();
                if (!imageResult.Success)
                {
                    result.Success = false;
                    result.Message = $"Failed to ensure bootstrap image: {imageResult.Message}";
                    result.Errors.Add(imageResult.Message);
                    await CleanupFailedSpawnAsync(volumeName, null);
                    return result;
                }

                if (imageResult.WasBuilt)
                {
                    _logger.LogInformation("Bootstrap image built in {Duration}s", imageResult.BuildDuration.TotalSeconds);
                }

                // Step 5: Start bootstrap container
                _logger.LogInformation("Starting bootstrap container...");
                result.CompletedStep = DevcontainerSpawnStep.BootstrapContainerStart;

                var bootstrapConfig = new BootstrapContainerConfig
                {
                    ProjectName = options.ProjectName,
                    VolumeName = volumeName,
                    WorkspacePath = $"/workspaces/{options.ProjectName}",
                    ImageName = "pks-devcontainer-bootstrap",
                    ImageTag = "latest",
                    ContainerNamePrefix = "pks-bootstrap",
                    MountDockerSocket = true,
                    Labels = new Dictionary<string, string>
                    {
                        { "pks.managed", "true" },
                        { "pks.bootstrap", "true" }
                    }
                };

                bootstrapContainer = await StartBootstrapContainerAsync(bootstrapConfig);
                _logger.LogInformation("Bootstrap container started: {ContainerId}", bootstrapContainer.ContainerId);

                // Step 6: Copy files to volume via bootstrap container
                if (options.CopySourceFiles)
                {
                    _logger.LogInformation("Copying source files to volume via bootstrap container...");
                    result.CompletedStep = DevcontainerSpawnStep.FileCopyToBootstrap;

                    await CopyFilesToBootstrapVolumeAsync(
                        bootstrapContainer.ContainerId,
                        options.ProjectPath,
                        $"/workspaces/{options.ProjectName}");

                    _logger.LogInformation("Files copied to volume");
                }

                // Step 7: Run devcontainer up inside bootstrap container
                _logger.LogInformation("Running devcontainer up in bootstrap container...");
                result.CompletedStep = DevcontainerSpawnStep.DevcontainerUp;

                upResult = await RunDevcontainerUpInBootstrapAsync(
                    bootstrapContainer.ContainerId,
                    $"/workspaces/{options.ProjectName}",
                    volumeName);

                if (upResult.Outcome != "success")
                {
                    result.Success = false;
                    result.Message = $"devcontainer up failed: {upResult.Outcome}";
                    result.Errors.Add($"devcontainer CLI returned outcome: {upResult.Outcome}");
                    await CleanupFailedSpawnAsync(volumeName, null);
                    return result;
                }

                _logger.LogInformation("Container created: {ContainerId}", upResult.ContainerId);
            }
            else
            {
                // Legacy direct workflow (fallback for --no-bootstrap)
                _logger.LogInformation("Using legacy direct workflow (--no-bootstrap)");

                // Step 4: Copy files to volume
                if (options.CopySourceFiles)
                {
                    _logger.LogInformation("Copying source files to volume...");
                    result.CompletedStep = DevcontainerSpawnStep.FileCopyToBootstrap;

                    await CopyToVolumeAsync(
                        options.ProjectPath,
                        volumeName,
                        $"/workspaces/{options.ProjectName}");

                    _logger.LogInformation("Files copied to volume");
                }

                // Step 5: Create bootstrap workspace
                _logger.LogInformation("Creating bootstrap workspace...");
                result.CompletedStep = DevcontainerSpawnStep.BootstrapContainerStart;

                bootstrapPath = await CreateBootstrapWorkspaceAsync(
                    options.ProjectName,
                    volumeName,
                    options.DevcontainerPath);

                _logger.LogInformation("Bootstrap workspace created at: {BootstrapPath}", bootstrapPath);

                // Step 6: Run devcontainer up
                _logger.LogInformation("Running devcontainer up...");
                result.CompletedStep = DevcontainerSpawnStep.DevcontainerUp;

                upResult = await RunDevcontainerUpAsync(bootstrapPath);
                if (upResult.Outcome != "success")
                {
                    result.Success = false;
                    result.Message = $"devcontainer up failed: {upResult.Outcome}";
                    result.Errors.Add($"devcontainer CLI returned outcome: {upResult.Outcome}");
                    await CleanupFailedSpawnAsync(volumeName, bootstrapPath);
                    return result;
                }

                _logger.LogInformation("Container created: {ContainerId}", upResult.ContainerId);
            }

            result.ContainerId = upResult.ContainerId;

            // Step 8: Launch VS Code
            if (options.LaunchVsCode)
            {
                _logger.LogInformation("Launching VS Code...");
                result.CompletedStep = DevcontainerSpawnStep.VsCodeLaunch;

                var vsCodeInfo = await CheckVsCodeInstallationAsync();
                if (vsCodeInfo.IsInstalled && vsCodeInfo.ExecutablePath != null)
                {
                    string uri;
                    if (options.UseBootstrapContainer)
                    {
                        // For bootstrap workflow, use container ID directly
                        uri = await GetContainerVsCodeUriAsync(upResult.ContainerId, upResult.RemoteWorkspaceFolder);
                    }
                    else
                    {
                        // For legacy workflow, use bootstrap path
                        uri = ConstructVsCodeUri(bootstrapPath!, upResult.RemoteWorkspaceFolder);
                    }

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

            // Capture bootstrap container logs if using bootstrap workflow
            if (bootstrapContainer != null)
            {
                try
                {
                    _logger.LogInformation("Capturing bootstrap container logs...");
                    var logsResult = await ExecuteInBootstrapAsync(
                        bootstrapContainer.ContainerId,
                        "cat /var/log/* 2>/dev/null || echo 'No logs found'",
                        timeoutSeconds: 10);

                    if (logsResult.Success && !string.IsNullOrWhiteSpace(logsResult.Output))
                    {
                        _logger.LogDebug("Bootstrap container logs: {Logs}", logsResult.Output);
                        result.Errors.Add($"Bootstrap logs: {logsResult.Output}");
                    }
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "Failed to capture bootstrap container logs");
                }
            }

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
        finally
        {
            // Always cleanup bootstrap container if it was created
            if (bootstrapContainer != null)
            {
                _logger.LogInformation("Cleaning up bootstrap container...");
                await StopBootstrapContainerAsync(bootstrapContainer.ContainerId);
            }
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
    public async Task CleanupFailedSpawnAsync(string volumeName, string? bootstrapPath = null, string? bootstrapContainerId = null)
    {
        _logger.LogInformation("Cleaning up failed spawn (volume: {VolumeName}, bootstrap: {BootstrapPath}, container: {ContainerId})",
            volumeName, bootstrapPath ?? "none", bootstrapContainerId ?? "none");

        try
        {
            // Cleanup bootstrap container first
            if (!string.IsNullOrEmpty(bootstrapContainerId))
            {
                await StopBootstrapContainerAsync(bootstrapContainerId, remove: true);
            }

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

    /// <summary>
    /// Ensures the PKS bootstrap image is available, building it if necessary
    /// </summary>
    /// <param name="imageName">Name of the bootstrap image (default: pks-devcontainer-bootstrap)</param>
    /// <param name="imageTag">Tag for the bootstrap image (default: latest)</param>
    /// <returns>Result containing success status and image ID</returns>
    private async Task<BootstrapImageResult> EnsureBootstrapImageAsync(
        string imageName = "pks-devcontainer-bootstrap",
        string imageTag = "latest")
    {
        var startTime = DateTime.UtcNow;
        var fullImageName = $"{imageName}:{imageTag}";

        try
        {
            _logger.LogDebug("Checking for bootstrap image: {ImageName}", fullImageName);

            // Step 1: Check if image already exists
            var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool>
                    {
                        [fullImageName] = true
                    }
                }
            });

            if (images.Count > 0)
            {
                var existingImage = images[0];
                _logger.LogInformation("Bootstrap image already exists: {ImageId}", existingImage.ID);

                return new BootstrapImageResult
                {
                    Success = true,
                    ImageId = existingImage.ID,
                    ImageName = fullImageName,
                    WasBuilt = false,
                    BuildDuration = TimeSpan.Zero,
                    Message = "Bootstrap image already exists"
                };
            }

            _logger.LogInformation("Bootstrap image not found, building from embedded Dockerfile...");

            // Step 2: Read embedded Dockerfile from assembly resources
            var assembly = typeof(DevcontainerSpawnerService).Assembly;
            var resourceName = "PKS.Infrastructure.Resources.bootstrap.Dockerfile";

            string dockerfileContent;
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    _logger.LogError("Embedded Dockerfile not found: {ResourceName}", resourceName);
                    return new BootstrapImageResult
                    {
                        Success = false,
                        Message = $"Embedded Dockerfile not found: {resourceName}"
                    };
                }

                using var reader = new StreamReader(stream);
                dockerfileContent = await reader.ReadToEndAsync();
            }

            _logger.LogDebug("Successfully read embedded Dockerfile ({Length} bytes)", dockerfileContent.Length);

            // Step 3: Create temporary build context directory
            var tempBuildContext = Path.Combine(
                Path.GetTempPath(),
                $"pks-bootstrap-build-{Guid.NewGuid().ToString("N")[..8]}");

            Directory.CreateDirectory(tempBuildContext);
            _logger.LogDebug("Created temporary build context: {TempPath}", tempBuildContext);

            try
            {
                // Step 4: Write Dockerfile to temp directory
                var dockerfilePath = Path.Combine(tempBuildContext, "Dockerfile");
                await File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
                _logger.LogDebug("Wrote Dockerfile to: {DockerfilePath}", dockerfilePath);

                // Step 5: Build the image using docker build
                _logger.LogInformation("Building bootstrap image: {ImageName}", fullImageName);

                var buildArgs = $"build -t {fullImageName} \"{tempBuildContext}\"";
                var buildOutput = await RunDockerCommandAsync(buildArgs);

                _logger.LogDebug("Docker build output: {Output}", buildOutput);

                // Step 6: Get the image ID using docker inspect
                var inspectOutput = await RunDockerCommandAsync($"inspect --format={{{{.Id}}}} {fullImageName}");
                var imageId = inspectOutput.Trim();

                _logger.LogInformation("Bootstrap image built successfully: {ImageId}", imageId);

                var duration = DateTime.UtcNow - startTime;

                return new BootstrapImageResult
                {
                    Success = true,
                    ImageId = imageId,
                    ImageName = fullImageName,
                    WasBuilt = true,
                    BuildDuration = duration,
                    Message = $"Bootstrap image built successfully in {duration.TotalSeconds:F1}s"
                };
            }
            finally
            {
                // Step 7: Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempBuildContext))
                    {
                        Directory.Delete(tempBuildContext, recursive: true);
                        _logger.LogDebug("Cleaned up temporary build context: {TempPath}", tempBuildContext);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup temporary build context: {TempPath}", tempBuildContext);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure bootstrap image: {ImageName}", fullImageName);

            return new BootstrapImageResult
            {
                Success = false,
                Message = $"Failed to ensure bootstrap image: {ex.Message}"
            };
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
        // On Windows, we need different handling for different commands:
        // - devcontainer: Run directly - its .cmd wrapper handles bash/Git Bash
        // - code/code-insiders: Wrap in cmd.exe for PATH resolution
        string actualFileName = fileName;
        string actualArguments = arguments;

        if (OperatingSystem.IsWindows())
        {
            // For VS Code, wrap in cmd.exe
            if (fileName.Equals("code", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("code-insiders", StringComparison.OrdinalIgnoreCase))
            {
                actualFileName = "cmd.exe";
                actualArguments = $"/c {fileName} {arguments}";
                _logger.LogDebug("Windows: Wrapping VS Code command in cmd.exe: {Command}", actualArguments);
            }
            // For devcontainer, run directly - the .cmd wrapper handles shell resolution
            // This allows initializeCommand to use Git Bash or WSL for bash scripts
            else if (fileName.Equals("devcontainer", StringComparison.OrdinalIgnoreCase))
            {
                // Add .cmd extension if not present for direct execution
                actualFileName = fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    ? fileName
                    : $"{fileName}.cmd";
                actualArguments = arguments;
                _logger.LogDebug("Windows: Running devcontainer directly: {Command} {Args}", actualFileName, actualArguments);
            }
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

    /// <summary>
    /// Starts a bootstrap container with volume mount and Docker socket access
    /// </summary>
    /// <param name="config">Bootstrap container configuration</param>
    /// <returns>Information about the started bootstrap container</returns>
    private async Task<BootstrapContainerInfo> StartBootstrapContainerAsync(BootstrapContainerConfig config)
    {
        _logger.LogDebug("Starting bootstrap container for project: {ProjectName}", config.ProjectName);

        var containerName = $"{config.ContainerNamePrefix}-{config.ProjectName}-{Guid.NewGuid().ToString("N")[..8]}";
        var imageName = $"{config.ImageName}:{config.ImageTag}";

        // Determine Docker socket path based on platform
        string dockerSocketPath;
        string dockerSocketMount;

        if (OperatingSystem.IsWindows())
        {
            // Windows uses named pipe
            dockerSocketPath = "//./pipe/docker_engine";
            dockerSocketMount = "//./pipe/docker_engine://./pipe/docker_engine";
        }
        else
        {
            // Unix systems use socket file
            dockerSocketPath = "/var/run/docker.sock";
            dockerSocketMount = "/var/run/docker.sock:/var/run/docker.sock";
        }

        _logger.LogDebug("Using Docker socket: {SocketPath}", dockerSocketPath);

        // Build labels dictionary
        var labels = new Dictionary<string, string>(config.Labels)
        {
            ["devcontainer.project"] = config.ProjectName,
            ["devcontainer.volume"] = config.VolumeName
        };

        // Create container configuration
        var createParams = new CreateContainerParameters
        {
            Image = imageName,
            Name = containerName,
            Labels = labels,
            WorkingDir = config.WorkspacePath,
            Cmd = new[] { "sleep", "infinity" },
            HostConfig = new HostConfig
            {
                Binds = new List<string>
                {
                    $"{config.VolumeName}:{config.WorkspacePath}"
                }
            }
        };

        // Add Docker socket mount if enabled
        if (config.MountDockerSocket)
        {
            createParams.HostConfig.Binds.Add(dockerSocketMount);
            _logger.LogDebug("Docker socket mounted in bootstrap container");
        }

        _logger.LogDebug("Creating bootstrap container: {ContainerName}", containerName);

        // Create the container
        var response = await _dockerClient.Containers.CreateContainerAsync(createParams);

        _logger.LogDebug("Starting bootstrap container: {ContainerId}", response.ID);

        // Start the container
        await _dockerClient.Containers.StartContainerAsync(
            response.ID,
            new ContainerStartParameters());

        _logger.LogInformation("Bootstrap container started: {ContainerId}", response.ID);

        return new BootstrapContainerInfo
        {
            ContainerId = response.ID,
            ContainerName = containerName,
            StartedAt = DateTime.UtcNow,
            VolumeName = config.VolumeName,
            ProjectName = config.ProjectName
        };
    }

    /// <summary>
    /// Executes a command inside the bootstrap container
    /// </summary>
    /// <param name="containerId">ID of the bootstrap container</param>
    /// <param name="command">Command to execute</param>
    /// <param name="workingDir">Working directory for command execution</param>
    /// <param name="timeoutSeconds">Timeout in seconds (default: 120)</param>
    /// <returns>Result of the command execution including output and exit code</returns>
    private async Task<BootstrapExecutionResult> ExecuteInBootstrapAsync(
        string containerId,
        string command,
        string? workingDir = null,
        int timeoutSeconds = 120)
    {
        var startTime = DateTime.UtcNow;
        _logger.LogDebug("Executing command in bootstrap container {ContainerId}: {Command}", containerId, command);

        try
        {
            // Create exec instance
            var execCreateParams = new ContainerExecCreateParameters
            {
                Cmd = new[] { "/bin/sh", "-c", command },
                AttachStdout = true,
                AttachStderr = true,
                WorkingDir = workingDir
            };

            var execCreateResponse = await _dockerClient.Exec.ExecCreateContainerAsync(containerId, execCreateParams);
            _logger.LogDebug("Exec created: {ExecId}", execCreateResponse.ID);

            // Start exec and attach to streams
            var stream = await _dockerClient.Exec.StartAndAttachContainerExecAsync(
                execCreateResponse.ID,
                tty: false);

            // Read output with timeout
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await ReadOutputToEndAsync(stream, outputBuilder, errorBuilder, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Command execution timed out after {Timeout} seconds", timeoutSeconds);
                return new BootstrapExecutionResult
                {
                    Success = false,
                    Output = outputBuilder.ToString(),
                    Error = $"Command timed out after {timeoutSeconds} seconds",
                    ExitCode = -1,
                    Duration = DateTime.UtcNow - startTime
                };
            }

            // Inspect exec to get exit code
            var execInspect = await _dockerClient.Exec.InspectContainerExecAsync(execCreateResponse.ID);

            var output = outputBuilder.ToString();
            var error = errorBuilder.ToString();
            var exitCode = (int)execInspect.ExitCode;

            _logger.LogDebug("Command completed with exit code: {ExitCode}", exitCode);

            return new BootstrapExecutionResult
            {
                Success = exitCode == 0,
                Output = output,
                Error = error,
                ExitCode = exitCode,
                Duration = DateTime.UtcNow - startTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing command in bootstrap container");
            return new BootstrapExecutionResult
            {
                Success = false,
                Output = string.Empty,
                Error = $"Execution failed: {ex.Message}",
                ExitCode = -1,
                Duration = DateTime.UtcNow - startTime
            };
        }
    }

    /// <summary>
    /// Reads multiplexed stream output to completion
    /// </summary>
    private async Task ReadOutputToEndAsync(
        MultiplexedStream stream,
        StringBuilder outputBuilder,
        StringBuilder errorBuilder,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);

            if (result.EOF)
            {
                break;
            }

            if (result.Count > 0)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

                if (result.Target == MultiplexedStream.TargetStream.StandardOut)
                {
                    outputBuilder.Append(text);
                }
                else if (result.Target == MultiplexedStream.TargetStream.StandardError)
                {
                    errorBuilder.Append(text);
                }
            }
        }
    }

    /// <summary>
    /// Copies files to volume via bootstrap container
    /// </summary>
    /// <param name="bootstrapContainerId">ID of the bootstrap container</param>
    /// <param name="sourcePath">Source path on host machine</param>
    /// <param name="destPath">Destination path in volume (mounted in bootstrap)</param>
    private async Task CopyFilesToBootstrapVolumeAsync(string bootstrapContainerId, string sourcePath, string destPath)
    {
        _logger.LogDebug("Copying files to bootstrap volume: {SourcePath} -> {DestPath}", sourcePath, destPath);

        // Create destination directory in bootstrap container
        var mkdirCommand = $"mkdir -p {destPath}";
        var mkdirResult = await ExecuteInBootstrapAsync(bootstrapContainerId, mkdirCommand);

        if (!mkdirResult.Success)
        {
            throw new InvalidOperationException(
                $"Failed to create destination directory in bootstrap container: {mkdirResult.Error}");
        }

        _logger.LogDebug("Created destination directory: {DestPath}", destPath);

        // Use docker cp to copy files to bootstrap container
        await RunDockerCommandAsync($"cp \"{sourcePath}/.\" {bootstrapContainerId}:{destPath}");

        _logger.LogDebug("Files copied to bootstrap container");

        // Verify copy with ls -la
        var verifyCommand = $"ls -la {destPath}";
        var verifyResult = await ExecuteInBootstrapAsync(bootstrapContainerId, verifyCommand);

        if (verifyResult.Success)
        {
            _logger.LogInformation("Files copied successfully to bootstrap volume. Contents: {Output}",
                verifyResult.Output);
        }
        else
        {
            _logger.LogWarning("Could not verify file copy: {Error}", verifyResult.Error);
        }
    }

    /// <summary>
    /// Runs devcontainer up command inside bootstrap container
    /// </summary>
    /// <param name="bootstrapContainerId">ID of the bootstrap container</param>
    /// <param name="workspaceFolder">Workspace folder path inside bootstrap container</param>
    /// <param name="volumeName">Docker volume name containing the workspace</param>
    /// <returns>Parsed result from devcontainer CLI</returns>
    private async Task<DevcontainerUpResult> RunDevcontainerUpInBootstrapAsync(
        string bootstrapContainerId,
        string workspaceFolder,
        string volumeName)
    {
        _logger.LogDebug("Running devcontainer up in bootstrap container: {WorkspaceFolder}", workspaceFolder);

        // Create override config that clears workspaceMount to prevent bind mount attempts
        // Only clear workspaceMount, keep other properties intact
        var overrideConfigPath = "/tmp/pks-devcontainer-override.json";
        var overrideConfig = "{\"workspaceMount\":\"\"}";
        var createOverrideResult = await ExecuteInBootstrapAsync(
            bootstrapContainerId,
            $"echo '{overrideConfig}' > {overrideConfigPath}",
            workingDir: null,
            timeoutSeconds: 10);

        // Build command with volume mount and workspace folder
        // Key: --override-config clears workspaceMount from devcontainer.json
        // This prevents devcontainer CLI from trying to bind mount the workspace
        var devcontainerCommand = $"devcontainer up --workspace-folder {workspaceFolder} --config {workspaceFolder}/.devcontainer/devcontainer.json --override-config {overrideConfigPath} --mount type=volume,source={volumeName},target=/workspaces,external=true --update-remote-user-uid-default off";

        // Execute using ExecuteInBootstrapAsync with 600 second timeout
        var result = await ExecuteInBootstrapAsync(
            bootstrapContainerId,
            devcontainerCommand,
            workingDir: null,
            timeoutSeconds: 600);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"devcontainer up failed in bootstrap container: {result.Error}");
        }

        _logger.LogDebug("devcontainer up output: {Output}", result.Output);

        // Parse JSON output from last line that starts with {
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var jsonLine = lines.LastOrDefault(line => line.TrimStart().StartsWith("{"));

        if (string.IsNullOrEmpty(jsonLine))
        {
            throw new InvalidOperationException(
                "Could not find JSON output from devcontainer up command");
        }

        _logger.LogDebug("Parsing JSON line: {JsonLine}", jsonLine);

        // Deserialize to DevcontainerUpResult
        try
        {
            var upResult = JsonSerializer.Deserialize<DevcontainerUpResult>(jsonLine);

            if (upResult == null)
            {
                throw new InvalidOperationException("Failed to deserialize devcontainer up result");
            }

            _logger.LogInformation("devcontainer up completed with outcome: {Outcome}, containerId: {ContainerId}",
                upResult.Outcome, upResult.ContainerId);

            return upResult;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse devcontainer up JSON output: {JsonLine}", jsonLine);
            throw new InvalidOperationException(
                $"Failed to parse devcontainer up output: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Stops and optionally removes the bootstrap container
    /// </summary>
    /// <param name="containerId">ID of the bootstrap container to stop</param>
    /// <param name="remove">Whether to remove the container after stopping (default: true)</param>
    private async Task StopBootstrapContainerAsync(string containerId, bool remove = true)
    {
        try
        {
            _logger.LogDebug("Stopping bootstrap container: {ContainerId}", containerId);

            // Stop container with timeout
            await _dockerClient.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 10
                });

            if (remove)
            {
                _logger.LogDebug("Removing bootstrap container: {ContainerId}", containerId);

                // Remove container
                await _dockerClient.Containers.RemoveContainerAsync(
                    containerId,
                    new ContainerRemoveParameters
                    {
                        Force = true
                    });

                _logger.LogInformation("Bootstrap container stopped and removed: {ContainerId}", containerId);
            }
            else
            {
                _logger.LogInformation("Bootstrap container stopped: {ContainerId}", containerId);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup - don't throw exceptions
            _logger.LogWarning(ex, "Failed to stop/remove bootstrap container: {ContainerId}", containerId);
        }
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
