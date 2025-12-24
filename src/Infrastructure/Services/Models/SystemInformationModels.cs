using System.Runtime.InteropServices;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Comprehensive system information for PKS CLI
/// </summary>
public class SystemInformation
{
    public PksCliInfo PksCliInfo { get; set; } = new();
    public DotNetRuntimeInfo DotNetRuntimeInfo { get; set; } = new();
    public OperatingSystemInfo OperatingSystemInfo { get; set; } = new();
    public EnvironmentInfo EnvironmentInfo { get; set; } = new();
    public HardwareInfo HardwareInfo { get; set; } = new();
    public DateTime CollectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// PKS CLI version and configuration information
/// </summary>
public class PksCliInfo
{
    public string Version { get; set; } = string.Empty;
    public string AssemblyVersion { get; set; } = string.Empty;
    public string FileVersion { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string BuildConfiguration { get; set; } = string.Empty;
    public DateTime BuildDate { get; set; }
    public string InstallLocation { get; set; } = string.Empty;
    public bool IsGlobalTool { get; set; }
    public string? GitCommit { get; set; }
    public string? GitBranch { get; set; }
}

/// <summary>
/// .NET runtime environment information
/// </summary>
public class DotNetRuntimeInfo
{
    public string FrameworkVersion { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string RuntimeIdentifier { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string ClrVersion { get; set; } = string.Empty;
    public bool IsCoreClr { get; set; }
    public bool IsAot { get; set; }
    public bool IsSelfContained { get; set; }
    public bool IsReadyToRun { get; set; }
    public long WorkingSet { get; set; }
    public long GcMemory { get; set; }
    public int ProcessorCount { get; set; }
    public bool IsServerGc { get; set; }
    public string[] InstalledSdks { get; set; } = Array.Empty<string>();
    public string[] InstalledRuntimes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Operating system information
/// </summary>
public class OperatingSystemInfo
{
    public string Platform { get; set; } = string.Empty;
    public string PlatformVersion { get; set; } = string.Empty;
    public string OsDescription { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public string ProcessArchitecture { get; set; } = string.Empty;
    public bool IsWindows { get; set; }
    public bool IsLinux { get; set; }
    public bool IsMacOs { get; set; }
    public bool IsFreeBsd { get; set; }
    public bool IsWsl { get; set; }
    public string? WindowsVersion { get; set; }
    public string? LinuxDistribution { get; set; }
    public string? MacOsVersion { get; set; }
    public string KernelVersion { get; set; } = string.Empty;
    public string[] InstalledShells { get; set; } = Array.Empty<string>();
    public string CurrentShell { get; set; } = string.Empty;
    public string CurrentUser { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public DateTime SystemUptime { get; set; }
}

/// <summary>
/// Environment variables and configuration (safely collected)
/// </summary>
public class EnvironmentInfo
{
    public string CurrentDirectory { get; set; } = string.Empty;
    public string TempPath { get; set; } = string.Empty;
    public string UserPath { get; set; } = string.Empty;
    public string SystemPath { get; set; } = string.Empty;
    public Dictionary<string, string> SafeEnvironmentVariables { get; set; } = new();
    public string[] DotNetVariables { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> DevelopmentVariables { get; set; } = new();
    public bool HasDocker { get; set; }
    public bool HasKubernetes { get; set; }
    public bool HasGit { get; set; }
    public bool HasNode { get; set; }
    public string? DockerVersion { get; set; }
    public string? KubernetesVersion { get; set; }
    public string? GitVersion { get; set; }
    public string? NodeVersion { get; set; }
}

/// <summary>
/// Hardware specifications
/// </summary>
public class HardwareInfo
{
    public int LogicalCores { get; set; }
    public int PhysicalCores { get; set; }
    public string ProcessorName { get; set; } = string.Empty;
    public string ProcessorArchitecture { get; set; } = string.Empty;
    public long TotalMemoryBytes { get; set; }
    public long AvailableMemoryBytes { get; set; }
    public double MemoryUsagePercentage { get; set; }
    public long TotalDiskSpaceBytes { get; set; }
    public long AvailableDiskSpaceBytes { get; set; }
    public double DiskUsagePercentage { get; set; }
    public string[] NetworkInterfaces { get; set; } = Array.Empty<string>();
    public bool HasIntegratedGraphics { get; set; }
    public bool HasDiscreteGraphics { get; set; }
    public string[] GraphicsCards { get; set; } = Array.Empty<string>();
    public string SystemModel { get; set; } = string.Empty;
    public string SystemManufacturer { get; set; } = string.Empty;
    public bool IsVirtualMachine { get; set; }
    public string? VirtualizationTechnology { get; set; }
}

/// <summary>
/// System information collection options
/// </summary>
public class SystemInfoCollectionOptions
{
    /// <summary>
    /// Include detailed hardware information (may be slower to collect)
    /// </summary>
    public bool IncludeHardwareDetails { get; set; } = true;

    /// <summary>
    /// Include environment variables (filtered for safety)
    /// </summary>
    public bool IncludeEnvironmentVariables { get; set; } = true;

    /// <summary>
    /// Include installed .NET SDKs and runtimes
    /// </summary>
    public bool IncludeDotNetDetails { get; set; } = true;

    /// <summary>
    /// Include tool availability checks (Docker, Git, etc.)
    /// </summary>
    public bool IncludeToolAvailability { get; set; } = true;

    /// <summary>
    /// Timeout for external tool checks (in milliseconds)
    /// </summary>
    public int ToolCheckTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Environment variables to safely include (these are known to be non-sensitive)
    /// </summary>
    public HashSet<string> SafeEnvironmentVariableNames { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "COMPUTERNAME", "USERNAME", "USERDOMAIN", "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER", "NUMBER_OF_PROCESSORS", "OS", "PATHEXT",
        "TEMP", "TMP", "HOMEDRIVE", "HOMEPATH", "USERPROFILE", "APPDATA",
        "LOCALAPPDATA", "PROGRAMFILES", "PROGRAMFILES(X86)", "SYSTEMROOT",
        "WINDIR", "COMSPEC", "PWD", "HOME", "SHELL", "USER", "LOGNAME",
        "LANG", "LC_ALL", "TZ", "TERM", "DISPLAY", "XDG_SESSION_TYPE",
        "DESKTOP_SESSION", "GDMSESSION", "GNOME_DESKTOP_SESSION_ID",
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "ASPNETCORE_ENVIRONMENT",
        "ENVIRONMENT", "NODE_ENV", "DOCKER_HOST", "KUBERNETES_SERVICE_HOST"
    };

    /// <summary>
    /// Prefixes for environment variables that are safe to include
    /// </summary>
    public HashSet<string> SafeEnvironmentVariablePrefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_", "ASPNETCORE_", "NUGET_", "MSBuild", "VSAPPIDNAME", "VSAPPIDDIR",
        "VSCODE_", "npm_", "VCPKG_", "CMAKE_", "PKG_CONFIG_"
    };
}