using System;

namespace PKS.Infrastructure.Attributes;

/// <summary>
/// Indicates that a command should skip the first-time warning display.
/// Used for automated scenarios, MCP server operations, and hook commands.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class SkipFirstTimeWarningAttribute : Attribute
{
    /// <summary>
    /// Optional reason for skipping the warning
    /// </summary>
    public string? Reason { get; set; }

    public SkipFirstTimeWarningAttribute()
    {
    }

    public SkipFirstTimeWarningAttribute(string reason)
    {
        Reason = reason;
    }
}