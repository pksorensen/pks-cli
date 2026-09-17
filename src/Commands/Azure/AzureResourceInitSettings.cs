using PKS.Infrastructure.Services.Azure;
using Spectre.Console.Cli;

namespace PKS.Commands.Azure;

/// <summary>
/// The options <c>pks loganalytics init</c>, <c>pks appinsights init</c> and <c>pks fileshare init</c>
/// share. Each command's settings class declares them itself (the branches are typed, so the
/// settings must derive from their own vertical's base) and maps them here.
/// </summary>
public interface IAzureResourceInitSettings
{
    /// <summary>Kept for backward compatibility; identical to a plain init. Never clears credentials.</summary>
    bool Force { get; }

    /// <summary><c>--reauth [TENANT]</c>: sign in again, optionally to one tenant id or email.</summary>
    FlagValue<string>? Reauth { get; }

    string? Tenant { get; }
    string? Subscription { get; }
    string[] Enable { get; }
    string[] Disable { get; }
    bool List { get; }
}

public static class AzureResourceInitSettingsExtensions
{
    public static AzureResourceInitOptions ToOptions(this IAzureResourceInitSettings settings)
    {
        var reauth = settings.Reauth?.IsSet == true;
        var reauthTenant = reauth && !string.IsNullOrWhiteSpace(settings.Reauth!.Value) ? settings.Reauth.Value.Trim() : null;
        var tenant = string.IsNullOrWhiteSpace(settings.Tenant) ? null : settings.Tenant.Trim();

        return new AzureResourceInitOptions
        {
            Reauth = reauth,
            Tenant = reauthTenant ?? tenant,
            Subscription = string.IsNullOrWhiteSpace(settings.Subscription) ? null : settings.Subscription.Trim(),
            Enable = Clean(settings.Enable),
            Disable = Clean(settings.Disable),
            List = settings.List,
        };
    }

    private static List<string> Clean(string[]? values)
        => (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
}
