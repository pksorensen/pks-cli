using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Acs;

public class AcsSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
