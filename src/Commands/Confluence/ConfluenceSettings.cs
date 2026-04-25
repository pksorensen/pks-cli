using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Confluence;

/// <summary>Shared settings for Confluence commands.</summary>
public class ConfluenceSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }

    [CommandOption("--debug")]
    [Description("Show HTTP request/response details for troubleshooting")]
    public bool Debug { get; set; }
}
