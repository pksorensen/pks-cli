using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenCode;

public sealed class OpenCodeSettings : CommandSettings
{
    [CommandOption("-m|--model")]
    [Description("Model id (default: glm-5.2)")]
    public string Model { get; set; } = "glm-5.2";

    [CommandOption("-p|--provider")]
    [Description("Provider override; only needed when a model exists on multiple configured providers")]
    public string? Provider { get; set; }

    [CommandArgument(0, "[ARGS]")]
    [Description("Additional arguments passed to the opencode CLI")]
    public string[] Args { get; set; } = Array.Empty<string>();
}
