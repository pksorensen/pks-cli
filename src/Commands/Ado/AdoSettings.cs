using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

public class AdoSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
