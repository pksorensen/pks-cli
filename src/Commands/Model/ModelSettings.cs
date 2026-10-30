using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

public class ModelSettings : CommandSettings
{
    [CommandOption("-y|--yes")]
    [Description("Skip the confirmation prompt (scripts, non-interactive shells)")]
    public bool Yes { get; init; }
}
