using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Registry;

public class RegistrySettings : CommandSettings
{
    [CommandArgument(0, "[hostname]")]
    [Description("Registry hostname (e.g. registry.kjeldager.io)")]
    public string? Hostname { get; set; }
}
