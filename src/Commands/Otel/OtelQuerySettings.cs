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

    [CommandOption("--verbose|-v")]
    [Description("Show query details: app ID, time window, KQL sent")]
    public bool Verbose { get; set; }

    public TimeSpan ParsedSince
    {
        get
        {
            var s = Since?.Trim().ToLowerInvariant() ?? "1h";
            if (s.EndsWith('d') && int.TryParse(s[..^1], out var days) && days > 0)
                return TimeSpan.FromDays(days);
            if (s.EndsWith('h') && int.TryParse(s[..^1], out var hours) && hours > 0)
                return TimeSpan.FromHours(hours);
            if (s.EndsWith('m') && int.TryParse(s[..^1], out var mins) && mins > 0)
                return TimeSpan.FromMinutes(mins);
            return TimeSpan.FromHours(1);
        }
    }
}
