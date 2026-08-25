using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Expo;

public class ExpoSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed output")]
    public bool Verbose { get; set; }
}
