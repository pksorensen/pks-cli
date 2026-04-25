using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

public class StorageSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
