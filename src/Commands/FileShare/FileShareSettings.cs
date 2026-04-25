using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.FileShares;

public class FileShareSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
