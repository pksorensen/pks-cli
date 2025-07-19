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
}