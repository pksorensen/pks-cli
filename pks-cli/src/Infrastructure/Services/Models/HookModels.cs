namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Definition of a hook that can be executed
/// </summary>
public class HookDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public string EventType { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Category { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public List<string> SupportedEvents { get; set; } = new();
    public Dictionary<string, object> DefaultConfiguration { get; set; } = new();
}

/// <summary>
/// Context information passed to a hook during execution
/// </summary>
public class HookContext
{
    public Dictionary<string, object> Parameters { get; set; } = new();
    public string WorkingDirectory { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of a hook execution
/// </summary>
public class HookResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Output { get; set; } = new();
    public int ExitCode { get; set; }
    public TimeSpan ExecutionTime { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Result of a hook installation operation
/// </summary>
public class HookInstallResult
{
    public bool Success { get; set; }
    public string HookName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string InstalledPath { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = new();
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
    public List<string> InstalledHooks { get; set; } = new();
    public string? ConfigurationFile { get; set; }
}

/// <summary>
/// Configuration for Git hooks installation
/// </summary>
public class HooksConfiguration
{
    public List<string> HookTypes { get; set; } = new();
    public string? Template { get; set; }
    public bool Force { get; set; } = false;
    public bool CommitValidation { get; set; } = true;
    public bool PrePushChecks { get; set; } = true;
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

/// <summary>
/// Configuration for Git hooks uninstallation
/// </summary>
public class HooksUninstallConfiguration
{
    public List<string> HookTypes { get; set; } = new();
    public bool KeepBackup { get; set; } = true;
    public string? BackupLocation { get; set; }
}

/// <summary>
/// Configuration for Git hooks updates
/// </summary>
public class HooksUpdateConfiguration
{
    public List<string> HookTypes { get; set; } = new();
    public string? Template { get; set; }
    public bool PreserveCustomizations { get; set; } = true;
    public Dictionary<string, object> UpdateOptions { get; set; } = new();
}

/// <summary>
/// Result of a hook uninstallation operation
/// </summary>
public class HookUninstallResult
{
    public bool Success { get; set; }
    public string HookName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public DateTime UninstalledAt { get; set; } = DateTime.UtcNow;
    public List<string> UninstalledHooks { get; set; } = new();
    public string? BackupLocation { get; set; }
}

/// <summary>
/// Result of a hook update operation
/// </summary>
public class HookUpdateResult
{
    public bool Success { get; set; }
    public string HookName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<string> UpdatedHooks { get; set; } = new();
    public string? Changes { get; set; }
}

/// <summary>
/// Represents an installed hook in the repository
/// </summary>
public class InstalledHook
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public DateTime InstalledAt { get; set; }
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool IsExecutable { get; set; } = true;
}

/// <summary>
/// Result of testing a hook
/// </summary>
public class HookTestResult
{
    public string HookName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TimeSpan ExecutionTime { get; set; }
    public TimeSpan Duration => ExecutionTime; // Alias for ExecutionTime
    public List<string> Output { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? Error => Errors?.FirstOrDefault(); // First error for convenience
    public int ExitCode { get; set; } = 0;
}