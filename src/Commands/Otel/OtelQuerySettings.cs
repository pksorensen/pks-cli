using System.ComponentModel;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

public class OtelQuerySettings : OtelSettings
{
    [CommandArgument(0, "[app]")]
    [Description("Application name filter (optional)")]
    public string? AppName { get; set; }

    [CommandOption("--since <DURATION>")]
    [Description("Time window: 1h, 6h, 24h, 7d (default: 1h)")]
    [DefaultValue("1h")]
    public string Since { get; set; } = "1h";

    [CommandOption("--limit <COUNT>")]
    [Description("Maximum results to return (default: 20)")]
    [DefaultValue(20)]
    public int Limit { get; set; } = 20;

    [CommandOption("--format <FORMAT>")]
    [Description("Output format: Table or Json (default: Table)")]
    [DefaultValue("Table")]
    public string Format { get; set; } = "Table";

    public TimeSpan ParsedSince => Since?.ToLowerInvariant() switch
    {
        "6h" => TimeSpan.FromHours(6),
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1)
    };
}
