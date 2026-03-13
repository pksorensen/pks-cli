using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Coolify;

public class CoolifySettings : CommandSettings
{
    [CommandArgument(0, "[URL]")]
    [Description("Coolify instance URL (e.g. https://projects.si14agents.com)")]
    public string? Url { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Show verbose output")]
    public bool Verbose { get; set; }
}
