using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Shared settings for Foundry commands.
/// </summary>
public class FoundrySettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
