using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Email;

public class EmailSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed output")]
    public bool Verbose { get; set; }
}
