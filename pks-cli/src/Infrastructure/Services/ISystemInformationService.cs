using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for collecting comprehensive system information for PKS CLI
/// </summary>
public interface ISystemInformationService
{
    /// <summary>
    /// Collect all system information with default options
    /// </summary>
    /// <returns>Complete system information</returns>
    Task<SystemInformation> GetSystemInformationAsync();

    /// <summary>
    /// Collect system information with custom options
    /// </summary>
    /// <param name="options">Collection options to customize what information is gathered</param>
    /// <returns>System information based on the provided options</returns>
    Task<SystemInformation> GetSystemInformationAsync(SystemInfoCollectionOptions options);

    /// <summary>
    /// Get only PKS CLI version and build information
    /// </summary>
    /// <returns>PKS CLI information</returns>
    Task<PksCliInfo> GetPksCliInfoAsync();

    /// <summary>
    /// Get only .NET runtime information
    /// </summary>
    /// <returns>.NET runtime information</returns>
    Task<DotNetRuntimeInfo> GetDotNetRuntimeInfoAsync();

    /// <summary>
    /// Get only operating system information
    /// </summary>
    /// <returns>Operating system information</returns>
    Task<OperatingSystemInfo> GetOperatingSystemInfoAsync();

    /// <summary>
    /// Get only environment information (safely filtered)
    /// </summary>
    /// <returns>Environment information</returns>
    Task<EnvironmentInfo> GetEnvironmentInfoAsync();

    /// <summary>
    /// Get only hardware information
    /// </summary>
    /// <returns>Hardware information</returns>
    Task<HardwareInfo> GetHardwareInfoAsync();

    /// <summary>
    /// Check if a specific tool is available in the system PATH
    /// </summary>
    /// <param name="toolName">Name of the tool to check</param>
    /// <param name="timeoutMs">Timeout for the check in milliseconds</param>
    /// <returns>True if the tool is available, false otherwise</returns>
    Task<bool> IsToolAvailableAsync(string toolName, int timeoutMs = 5000);

    /// <summary>
    /// Get version of a specific tool if available
    /// </summary>
    /// <param name="toolName">Name of the tool</param>
    /// <param name="versionFlag">Flag to get version (default: --version)</param>
    /// <param name="timeoutMs">Timeout for the check in milliseconds</param>
    /// <returns>Tool version or null if not available</returns>
    Task<string?> GetToolVersionAsync(string toolName, string versionFlag = "--version", int timeoutMs = 5000);

    /// <summary>
    /// Format system information as a readable string for display
    /// </summary>
    /// <param name="systemInfo">System information to format</param>
    /// <param name="includeDetails">Include detailed information</param>
    /// <returns>Formatted system information</returns>
    string FormatSystemInformation(SystemInformation systemInfo, bool includeDetails = false);

    /// <summary>
    /// Export system information to JSON format
    /// </summary>
    /// <param name="systemInfo">System information to export</param>
    /// <param name="indent">Whether to format with indentation</param>
    /// <returns>JSON representation of system information</returns>
    string ExportToJson(SystemInformation systemInfo, bool indent = true);
}