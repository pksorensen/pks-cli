using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Firecracker;

public class FirecrackerSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
