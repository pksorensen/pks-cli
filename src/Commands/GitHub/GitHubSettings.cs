using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub;

public class GitHubSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
