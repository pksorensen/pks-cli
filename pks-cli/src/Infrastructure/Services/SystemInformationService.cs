using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Cross-platform system information service for PKS CLI
/// </summary>
public class SystemInformationService : ISystemInformationService
{
    private readonly ILogger<SystemInformationService> _logger;

    public SystemInformationService(ILogger<SystemInformationService> logger)
    {
        _logger = logger;
    }

    public async Task<SystemInformation> GetSystemInformationAsync()
    {
        return await GetSystemInformationAsync(new SystemInfoCollectionOptions());
    }

    public async Task<SystemInformation> GetSystemInformationAsync(SystemInfoCollectionOptions options)
    {
        try
        {
            var systemInfo = new SystemInformation();

            // Collect information in parallel for better performance
            var tasks = new List<Task>
            {
                Task.Run(async () => systemInfo.PksCliInfo = await GetPksCliInfoAsync()),
                Task.Run(async () => systemInfo.OperatingSystemInfo = await GetOperatingSystemInfoAsync())
            };

            if (options.IncludeDotNetDetails)
            {
                tasks.Add(Task.Run(async () => systemInfo.DotNetRuntimeInfo = await GetDotNetRuntimeInfoAsync()));
            }

            if (options.IncludeEnvironmentVariables)
            {
                tasks.Add(Task.Run(async () => systemInfo.EnvironmentInfo = await GetEnvironmentInfoAsync()));
            }

            if (options.IncludeHardwareDetails)
            {
                tasks.Add(Task.Run(async () => systemInfo.HardwareInfo = await GetHardwareInfoAsync()));
            }

            await Task.WhenAll(tasks);

            systemInfo.CollectedAt = DateTime.UtcNow;
            return systemInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to collect system information");
            throw;
        }
    }

    public async Task<PksCliInfo> GetPksCliInfoAsync()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyName = assembly.GetName();
            var location = assembly.Location;

            var info = new PksCliInfo
            {
                Version = assemblyName.Version?.ToString() ?? "Unknown",
                AssemblyVersion = assemblyName.Version?.ToString() ?? "Unknown",
                InstallLocation = string.IsNullOrEmpty(location) ? "Unknown" : Path.GetDirectoryName(location) ?? "Unknown"
            };

            // Try to get file version information
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                var fileVersionInfo = FileVersionInfo.GetVersionInfo(location);
                info.FileVersion = fileVersionInfo.FileVersion ?? "Unknown";
                info.ProductVersion = fileVersionInfo.ProductVersion ?? "Unknown";
            }

            // Detect if running as global tool
            info.IsGlobalTool = await DetectGlobalToolInstallationAsync();

            // Try to get build configuration
            var configAttribute = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
            info.BuildConfiguration = configAttribute?.Configuration ?? "Unknown";

            // Try to get build date from file creation time
            if (!string.IsNullOrEmpty(location) && File.Exists(location))
            {
                info.BuildDate = File.GetCreationTimeUtc(location);
            }

            // Try to get Git information (if available)
            await TryGetGitInformationAsync(info);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect PKS CLI information");
            return new PksCliInfo { Version = "Error collecting version information" };
        }
    }

    public async Task<DotNetRuntimeInfo> GetDotNetRuntimeInfoAsync()
    {
        try
        {
            var info = new DotNetRuntimeInfo
            {
                FrameworkVersion = Environment.Version.ToString(),
                RuntimeVersion = RuntimeInformation.FrameworkDescription,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                TargetFramework = Assembly.GetExecutingAssembly().GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName ?? "Unknown",
                ProcessorCount = Environment.ProcessorCount,
                WorkingSet = Environment.WorkingSet,
                IsServerGc = GCSettings.IsServerGC
            };

            // Get CLR version
            info.ClrVersion = Environment.Version.ToString();
            info.IsCoreClr = RuntimeInformation.FrameworkDescription.Contains(".NET Core") ||
                           RuntimeInformation.FrameworkDescription.Contains(".NET 5") ||
                           RuntimeInformation.FrameworkDescription.Contains(".NET 6") ||
                           RuntimeInformation.FrameworkDescription.Contains(".NET 7") ||
                           RuntimeInformation.FrameworkDescription.Contains(".NET 8") ||
                           RuntimeInformation.FrameworkDescription.Contains(".NET 9");

            // Get GC memory
            info.GcMemory = GC.GetTotalMemory(false);

            // Try to get installed SDKs and runtimes
            var sdksTask = GetInstalledDotNetSdksAsync();
            var runtimesTask = GetInstalledDotNetRuntimesAsync();

            await Task.WhenAll(sdksTask, runtimesTask);

            info.InstalledSdks = await sdksTask;
            info.InstalledRuntimes = await runtimesTask;

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect .NET runtime information");
            return new DotNetRuntimeInfo { FrameworkVersion = "Error collecting runtime information" };
        }
    }

    public async Task<OperatingSystemInfo> GetOperatingSystemInfoAsync()
    {
        try
        {
            var info = new OperatingSystemInfo
            {
                Platform = Environment.OSVersion.Platform.ToString(),
                PlatformVersion = Environment.OSVersion.Version.ToString(),
                OsDescription = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                IsMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                IsFreeBsd = RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD),
                CurrentUser = Environment.UserName,
                HostName = Environment.MachineName,
                TimeZone = TimeZoneInfo.Local.DisplayName,
                Culture = CultureInfo.CurrentCulture.Name,
                SystemUptime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64)
            };

            // Detect WSL
            if (info.IsLinux)
            {
                info.IsWsl = await DetectWslAsync();
                if (info.IsWsl)
                {
                    info.LinuxDistribution = await GetWslDistributionAsync();
                }
                else
                {
                    info.LinuxDistribution = await GetLinuxDistributionAsync();
                }
            }

            // Get platform-specific information
            if (info.IsWindows)
            {
                info.WindowsVersion = await GetWindowsVersionAsync();
            }
            else if (info.IsMacOs)
            {
                info.MacOsVersion = await GetMacOsVersionAsync();
            }

            // Get kernel version
            info.KernelVersion = await GetKernelVersionAsync();

            // Get shell information
            info.CurrentShell = GetCurrentShell();
            info.InstalledShells = await GetInstalledShellsAsync();

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect operating system information");
            return new OperatingSystemInfo { Platform = "Error collecting OS information" };
        }
    }

    public async Task<EnvironmentInfo> GetEnvironmentInfoAsync()
    {
        try
        {
            var options = new SystemInfoCollectionOptions();
            var info = new EnvironmentInfo
            {
                CurrentDirectory = Environment.CurrentDirectory,
                TempPath = Path.GetTempPath(),
                UserPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SystemPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty
            };

            // Safely collect environment variables
            var envVars = Environment.GetEnvironmentVariables();
            foreach (System.Collections.DictionaryEntry envVar in envVars)
            {
                var key = envVar.Key?.ToString();
                var value = envVar.Value?.ToString();

                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                    continue;

                // Check if it's a safe variable to include
                if (IsSafeEnvironmentVariable(key, options))
                {
                    info.SafeEnvironmentVariables[key] = value;
                }

                // Collect .NET specific variables
                if (key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase))
                {
                    info.DevelopmentVariables[key] = value;
                }
            }

            // Check tool availability
            var toolChecks = new[]
            {
                ("docker", "HasDocker"),
                ("kubectl", "HasKubernetes"),
                ("git", "HasGit"),
                ("node", "HasNode")
            };

            var toolTasks = toolChecks.Select(async tool =>
            {
                var (toolName, propertyName) = tool;
                var isAvailable = await IsToolAvailableAsync(toolName, options.ToolCheckTimeoutMs);

                switch (propertyName)
                {
                    case "HasDocker":
                        info.HasDocker = isAvailable;
                        if (isAvailable) info.DockerVersion = await GetToolVersionAsync("docker", "--version", options.ToolCheckTimeoutMs);
                        break;
                    case "HasKubernetes":
                        info.HasKubernetes = isAvailable;
                        if (isAvailable) info.KubernetesVersion = await GetToolVersionAsync("kubectl", "version --client", options.ToolCheckTimeoutMs);
                        break;
                    case "HasGit":
                        info.HasGit = isAvailable;
                        if (isAvailable) info.GitVersion = await GetToolVersionAsync("git", "--version", options.ToolCheckTimeoutMs);
                        break;
                    case "HasNode":
                        info.HasNode = isAvailable;
                        if (isAvailable) info.NodeVersion = await GetToolVersionAsync("node", "--version", options.ToolCheckTimeoutMs);
                        break;
                }
            });

            await Task.WhenAll(toolTasks);

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect environment information");
            return new EnvironmentInfo { CurrentDirectory = "Error collecting environment information" };
        }
    }

    public async Task<HardwareInfo> GetHardwareInfoAsync()
    {
        try
        {
            var info = new HardwareInfo
            {
                LogicalCores = Environment.ProcessorCount,
                ProcessorArchitecture = RuntimeInformation.ProcessArchitecture.ToString()
            };

            // Get memory information
            await GetMemoryInformationAsync(info);

            // Get disk information
            await GetDiskInformationAsync(info);

            // Get processor information
            await GetProcessorInformationAsync(info);

            // Get system information
            await GetSystemManufacturerInfoAsync(info);

            // Detect virtualization
            info.IsVirtualMachine = await DetectVirtualizationAsync();
            if (info.IsVirtualMachine)
            {
                info.VirtualizationTechnology = await DetectVirtualizationTechnologyAsync();
            }

            // Get network interfaces
            info.NetworkInterfaces = await GetNetworkInterfacesAsync();

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect hardware information");
            return new HardwareInfo { LogicalCores = Environment.ProcessorCount };
        }
    }

    public async Task<bool> IsToolAvailableAsync(string toolName, int timeoutMs = 5000)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                Arguments = toolName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            using var cts = new CancellationTokenSource(timeoutMs);
            var exitTask = process.WaitForExitAsync(cts.Token);

            await exitTask;
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check tool availability for {ToolName}", toolName);
            return false;
        }
    }

    public async Task<string?> GetToolVersionAsync(string toolName, string versionFlag = "--version", int timeoutMs = 5000)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = toolName,
                Arguments = versionFlag,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            using var cts = new CancellationTokenSource(timeoutMs);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync(cts.Token);

            await exitTask;

            if (process.ExitCode == 0)
            {
                var output = await outputTask;
                return output.Split('\n')[0].Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get version for tool {ToolName}", toolName);
            return null;
        }
    }

    public string FormatSystemInformation(SystemInformation systemInfo, bool includeDetails = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine("PKS CLI System Information");
        sb.AppendLine("=".PadRight(50, '='));
        sb.AppendLine();

        // PKS CLI Info
        sb.AppendLine("PKS CLI:");
        sb.AppendLine($"  Version: {systemInfo.PksCliInfo.Version}");
        sb.AppendLine($"  Build: {systemInfo.PksCliInfo.BuildConfiguration}");
        sb.AppendLine($"  Location: {systemInfo.PksCliInfo.InstallLocation}");
        sb.AppendLine($"  Global Tool: {systemInfo.PksCliInfo.IsGlobalTool}");
        sb.AppendLine();

        // Operating System
        sb.AppendLine("Operating System:");
        sb.AppendLine($"  OS: {systemInfo.OperatingSystemInfo.OsDescription}");
        sb.AppendLine($"  Architecture: {systemInfo.OperatingSystemInfo.OsArchitecture}");
        sb.AppendLine($"  Platform: {systemInfo.OperatingSystemInfo.Platform}");
        if (systemInfo.OperatingSystemInfo.IsWsl)
            sb.AppendLine($"  WSL Distribution: {systemInfo.OperatingSystemInfo.LinuxDistribution}");
        sb.AppendLine();

        // .NET Runtime
        sb.AppendLine(".NET Runtime:");
        sb.AppendLine($"  Version: {systemInfo.DotNetRuntimeInfo.RuntimeVersion}");
        sb.AppendLine($"  Target Framework: {systemInfo.DotNetRuntimeInfo.TargetFramework}");
        sb.AppendLine($"  Runtime ID: {systemInfo.DotNetRuntimeInfo.RuntimeIdentifier}");
        sb.AppendLine();

        // Hardware
        sb.AppendLine("Hardware:");
        sb.AppendLine($"  Processor: {systemInfo.HardwareInfo.ProcessorName}");
        sb.AppendLine($"  Cores: {systemInfo.HardwareInfo.LogicalCores} logical, {systemInfo.HardwareInfo.PhysicalCores} physical");
        sb.AppendLine($"  Memory: {systemInfo.HardwareInfo.TotalMemoryBytes / (1024 * 1024 * 1024):F1} GB total, {systemInfo.HardwareInfo.AvailableMemoryBytes / (1024 * 1024 * 1024):F1} GB available");
        sb.AppendLine();

        if (includeDetails)
        {
            // Environment Tools
            sb.AppendLine("Development Tools:");
            sb.AppendLine($"  Docker: {(systemInfo.EnvironmentInfo.HasDocker ? systemInfo.EnvironmentInfo.DockerVersion : "Not available")}");
            sb.AppendLine($"  Git: {(systemInfo.EnvironmentInfo.HasGit ? systemInfo.EnvironmentInfo.GitVersion : "Not available")}");
            sb.AppendLine($"  Node.js: {(systemInfo.EnvironmentInfo.HasNode ? systemInfo.EnvironmentInfo.NodeVersion : "Not available")}");
            sb.AppendLine($"  Kubernetes: {(systemInfo.EnvironmentInfo.HasKubernetes ? systemInfo.EnvironmentInfo.KubernetesVersion : "Not available")}");
            sb.AppendLine();
        }

        sb.AppendLine($"Collected at: {systemInfo.CollectedAt:yyyy-MM-dd HH:mm:ss} UTC");

        return sb.ToString();
    }

    public string ExportToJson(SystemInformation systemInfo, bool indent = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indent,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        return JsonSerializer.Serialize(systemInfo, options);
    }

    // Private helper methods
    private async Task<bool> DetectGlobalToolInstallationAsync()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var location = assembly.Location;

            if (string.IsNullOrEmpty(location))
                return false;

            // Check if the path contains .dotnet/tools which indicates global tool installation
            return location.Contains(Path.Combine(".dotnet", "tools"), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task TryGetGitInformationAsync(PksCliInfo info)
    {
        try
        {
            // Try to get git commit hash
            info.GitCommit = await GetToolVersionAsync("git", "rev-parse HEAD", 3000);

            // Try to get git branch
            info.GitBranch = await GetToolVersionAsync("git", "rev-parse --abbrev-ref HEAD", 3000);
        }
        catch
        {
            // Git information is optional
        }
    }

    private async Task<string[]> GetInstalledDotNetSdksAsync()
    {
        try
        {
            var sdks = await GetToolVersionAsync("dotnet", "--list-sdks", 10000);
            if (string.IsNullOrEmpty(sdks))
                return Array.Empty<string>();

            return sdks.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                      .Select(line => line.Trim())
                      .Where(line => !string.IsNullOrEmpty(line))
                      .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<string[]> GetInstalledDotNetRuntimesAsync()
    {
        try
        {
            var runtimes = await GetToolVersionAsync("dotnet", "--list-runtimes", 10000);
            if (string.IsNullOrEmpty(runtimes))
                return Array.Empty<string>();

            return runtimes.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(line => line.Trim())
                          .Where(line => !string.IsNullOrEmpty(line))
                          .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private bool IsSafeEnvironmentVariable(string key, SystemInfoCollectionOptions options)
    {
        if (options.SafeEnvironmentVariableNames.Contains(key))
            return true;

        return options.SafeEnvironmentVariablePrefixes.Any(prefix =>
            key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> DetectWslAsync()
    {
        try
        {
            // Check for WSL indicators
            return File.Exists("/proc/version") &&
                   (await File.ReadAllTextAsync("/proc/version")).Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetWslDistributionAsync()
    {
        try
        {
            // Try to get WSL distribution name
            if (File.Exists("/etc/os-release"))
            {
                var osRelease = await File.ReadAllTextAsync("/etc/os-release");
                var nameMatch = System.Text.RegularExpressions.Regex.Match(osRelease, @"NAME=""?([^""\n]+)""?");
                if (nameMatch.Success)
                    return nameMatch.Groups[1].Value;
            }
            return "Unknown WSL Distribution";
        }
        catch
        {
            return "Unknown WSL Distribution";
        }
    }

    private async Task<string> GetLinuxDistributionAsync()
    {
        try
        {
            if (File.Exists("/etc/os-release"))
            {
                var osRelease = await File.ReadAllTextAsync("/etc/os-release");
                var nameMatch = System.Text.RegularExpressions.Regex.Match(osRelease, @"PRETTY_NAME=""?([^""\n]+)""?");
                if (nameMatch.Success)
                    return nameMatch.Groups[1].Value;
            }
            return "Unknown Linux Distribution";
        }
        catch
        {
            return "Unknown Linux Distribution";
        }
    }

    private async Task<string> GetWindowsVersionAsync()
    {
        try
        {
            // Use registry or WMI to get detailed Windows version
            // For now, return OS version
            return Environment.OSVersion.Version.ToString();
        }
        catch
        {
            return "Unknown Windows Version";
        }
    }

    private async Task<string> GetMacOsVersionAsync()
    {
        try
        {
            var version = await GetToolVersionAsync("sw_vers", "-productVersion", 5000);
            return version ?? "Unknown macOS Version";
        }
        catch
        {
            return "Unknown macOS Version";
        }
    }

    private async Task<string> GetKernelVersionAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Environment.OSVersion.Version.ToString();
            }
            else
            {
                var version = await GetToolVersionAsync("uname", "-r", 5000);
                return version ?? "Unknown Kernel Version";
            }
        }
        catch
        {
            return "Unknown Kernel Version";
        }
    }

    private string GetCurrentShell()
    {
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shell))
                return Path.GetFileName(shell);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "cmd.exe";

            return "Unknown Shell";
        }
        catch
        {
            return "Unknown Shell";
        }
    }

    private async Task<string[]> GetInstalledShellsAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var shells = new List<string> { "cmd.exe" };
                if (await IsToolAvailableAsync("powershell", 2000))
                    shells.Add("powershell.exe");
                if (await IsToolAvailableAsync("pwsh", 2000))
                    shells.Add("pwsh.exe");
                return shells.ToArray();
            }
            else
            {
                if (File.Exists("/etc/shells"))
                {
                    var shellsContent = await File.ReadAllTextAsync("/etc/shells");
                    return shellsContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                      .Where(line => !line.StartsWith("#") && !string.IsNullOrWhiteSpace(line))
                                      .Select(shell => Path.GetFileName(shell.Trim()))
                                      .ToArray();
                }
                return new[] { "bash", "sh" };
            }
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task GetMemoryInformationAsync(HardwareInfo info)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use GlobalMemoryStatusEx on Windows
                // For now, use available .NET APIs
                var gcInfo = GC.GetGCMemoryInfo();
                info.TotalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
                info.AvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes - gcInfo.HeapSizeBytes;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Parse /proc/meminfo
                if (File.Exists("/proc/meminfo"))
                {
                    var meminfo = await File.ReadAllTextAsync("/proc/meminfo");
                    var totalMatch = System.Text.RegularExpressions.Regex.Match(meminfo, @"MemTotal:\s+(\d+)\s+kB");
                    var availableMatch = System.Text.RegularExpressions.Regex.Match(meminfo, @"MemAvailable:\s+(\d+)\s+kB");

                    if (totalMatch.Success)
                        info.TotalMemoryBytes = long.Parse(totalMatch.Groups[1].Value) * 1024;
                    if (availableMatch.Success)
                        info.AvailableMemoryBytes = long.Parse(availableMatch.Groups[1].Value) * 1024;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Use vm_stat or sysctl on macOS
                var vmStat = await GetToolVersionAsync("vm_stat", "", 5000);
                // Parse vm_stat output (simplified)
                if (!string.IsNullOrEmpty(vmStat))
                {
                    // This is a simplified implementation
                    // In production, you'd want to properly parse vm_stat output
                    var gcInfo = GC.GetGCMemoryInfo();
                    info.TotalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
                    info.AvailableMemoryBytes = gcInfo.TotalAvailableMemoryBytes - gcInfo.HeapSizeBytes;
                }
            }

            // Calculate memory usage percentage
            if (info.TotalMemoryBytes > 0)
            {
                info.MemoryUsagePercentage = ((double)(info.TotalMemoryBytes - info.AvailableMemoryBytes) / info.TotalMemoryBytes) * 100;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get memory information");
        }
    }

    private async Task GetDiskInformationAsync(HardwareInfo info)
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
            info.TotalDiskSpaceBytes = drives.Sum(d => d.TotalSize);
            info.AvailableDiskSpaceBytes = drives.Sum(d => d.AvailableFreeSpace);

            if (info.TotalDiskSpaceBytes > 0)
            {
                info.DiskUsagePercentage = ((double)(info.TotalDiskSpaceBytes - info.AvailableDiskSpaceBytes) / info.TotalDiskSpaceBytes) * 100;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get disk information");
        }
    }

    private async Task GetProcessorInformationAsync(HardwareInfo info)
    {
        try
        {
            // Try to get processor name from various sources
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                info.ProcessorName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown Processor";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                if (File.Exists("/proc/cpuinfo"))
                {
                    var cpuInfo = await File.ReadAllTextAsync("/proc/cpuinfo");
                    var nameMatch = System.Text.RegularExpressions.Regex.Match(cpuInfo, @"model name\s*:\s*(.+)");
                    if (nameMatch.Success)
                        info.ProcessorName = nameMatch.Groups[1].Value.Trim();

                    // Try to get physical core count
                    var coresMatch = System.Text.RegularExpressions.Regex.Match(cpuInfo, @"cpu cores\s*:\s*(\d+)");
                    if (coresMatch.Success)
                        info.PhysicalCores = int.Parse(coresMatch.Groups[1].Value);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var cpuBrand = await GetToolVersionAsync("sysctl", "-n machdep.cpu.brand_string", 5000);
                if (!string.IsNullOrEmpty(cpuBrand))
                    info.ProcessorName = cpuBrand;

                var physicalCpus = await GetToolVersionAsync("sysctl", "-n hw.physicalcpu", 5000);
                if (!string.IsNullOrEmpty(physicalCpus) && int.TryParse(physicalCpus, out var cores))
                    info.PhysicalCores = cores;
            }

            // Fallback for physical cores
            if (info.PhysicalCores == 0)
                info.PhysicalCores = info.LogicalCores / 2; // Rough estimate
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get processor information");
            info.ProcessorName = "Unknown Processor";
        }
    }

    private async Task GetSystemManufacturerInfoAsync(HardwareInfo info)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Use WMI to get system information (simplified)
                info.SystemManufacturer = "Unknown Manufacturer";
                info.SystemModel = "Unknown Model";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Try DMI information
                var manufacturer = await TryReadFileAsync("/sys/class/dmi/id/sys_vendor");
                var model = await TryReadFileAsync("/sys/class/dmi/id/product_name");

                info.SystemManufacturer = manufacturer ?? "Unknown Manufacturer";
                info.SystemModel = model ?? "Unknown Model";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                info.SystemManufacturer = "Apple Inc.";
                var model = await GetToolVersionAsync("sysctl", "-n hw.model", 5000);
                info.SystemModel = model ?? "Unknown Mac Model";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get system manufacturer information");
        }
    }

    private async Task<bool> DetectVirtualizationAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Check common virtualization indicators
                var indicators = new[]
                {
                    "/proc/xen", "/proc/vz", "/dev/vmmon", "/dev/vboxdrv",
                    "/sys/bus/acpi/devices/VBOX0000:00", "/sys/bus/acpi/devices/VMWA0001:00"
                };

                if (indicators.Any(File.Exists))
                    return true;

                // Check DMI information
                var vendor = await TryReadFileAsync("/sys/class/dmi/id/sys_vendor");
                if (vendor != null && (vendor.Contains("VMware") || vendor.Contains("VirtualBox") ||
                                     vendor.Contains("Microsoft Corporation") || vendor.Contains("Xen")))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> DetectVirtualizationTechnologyAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var vendor = await TryReadFileAsync("/sys/class/dmi/id/sys_vendor");
                if (vendor != null)
                {
                    if (vendor.Contains("VMware")) return "VMware";
                    if (vendor.Contains("VirtualBox")) return "VirtualBox";
                    if (vendor.Contains("Microsoft Corporation")) return "Hyper-V";
                    if (vendor.Contains("Xen")) return "Xen";
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string[]> GetNetworkInterfacesAsync()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(ni => $"{ni.Name} ({ni.NetworkInterfaceType})")
                .ToArray();

            return interfaces;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<string?> TryReadFileAsync(string path)
    {
        try
        {
            if (File.Exists(path))
                return (await File.ReadAllTextAsync(path)).Trim();
            return null;
        }
        catch
        {
            return null;
        }
    }
}