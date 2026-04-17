using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.MsGraph;

public class MsGraphSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed output")]
    public bool Verbose { get; set; }
}
