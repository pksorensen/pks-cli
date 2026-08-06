using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

public class ConsentSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
