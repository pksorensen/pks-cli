using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

public class OtelSettings : CommandSettings
{
    [CommandOption("--resource <NAME_OR_APPID>")]
    [Description("Query only this Application Insights resource (registered name or application id). Repeatable; default: every enabled resource.")]
    public string[]? Resources { get; set; }
}

/// <summary>The fan-out steps every <c>otel</c> command shares: which resources, and how failures are told.</summary>
internal static class OtelFanOut
{
    public const string InitCommand = "pks appinsights init";

    /// <summary>Enabled App Insights entries, narrowed by <c>--resource</c>; null after printing why not.</summary>
    public static Task<IReadOnlyList<AzureResourceEntry>?> ResolveAsync(
        IAzureResourceRegistry registry, OtelSettings settings, IAnsiConsole console)
        => AzureQueryTargets.ResolveAsync(
            registry, AzureResourceKind.AppInsights, settings.Resources, console,
            "Application Insights resources", InitCommand);

    /// <summary>Report each failed resource on stderr; exit 1 only when nothing answered.</summary>
    public static int ReportFailures<T>(OtelQueryResult<T> result, IAnsiConsole errorConsole) where T : IOtelRecord
    {
        foreach (var failure in result.Errors)
            AzureQueryTargets.ReportFailure(errorConsole, "Resource", failure.Resource, failure.Error, InitCommand);
        return result.AllFailed ? 1 : 0;
    }
}
