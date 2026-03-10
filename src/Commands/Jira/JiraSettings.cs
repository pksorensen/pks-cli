using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Jira;

/// <summary>
/// Shared settings for Jira commands.
/// </summary>
public class JiraSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
