using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Agentics;

public class AgenticsSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
