using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

public class RunnerSettings : GitHubSettings
{
    [CommandArgument(0, "[REPO]")]
    [Description("Repository in owner/repo format")]
    public string? Repository { get; set; }
}
