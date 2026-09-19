using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

public class TypeSafeSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed output")]
    public bool Verbose { get; set; }
}
