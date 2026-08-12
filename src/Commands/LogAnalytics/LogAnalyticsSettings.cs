using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.LogAnalytics;

public class LogAnalyticsSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}
