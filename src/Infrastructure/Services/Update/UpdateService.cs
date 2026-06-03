using System.Reflection;
using NuGet.Versioning;

namespace PKS.Infrastructure.Services.Update;

/// <summary>Release channel, mirroring aspire's stable/daily.</summary>
public enum UpdateChannel
{
    /// <summary>nuget.org stable releases of pks-cli.</summary>
    Stable,
    /// <summary>Preview packages (<c>X.Y.Z-preview.N</c>) published to NuGet on every push to main.</summary>
    Daily,
}

public interface IUpdateService
{
    string PackageId { get; }
    string CurrentVersion { get; }
    InstallMethod InstallMethod { get; }

    Task<UpdateChannel?> GetChannelAsync();
    Task SetChannelAsync(UpdateChannel channel);

    /// <summary>Latest version available on the channel, or null if the feed couldn't be queried.</summary>
    Task<string?> GetLatestVersionAsync(UpdateChannel channel, CancellationToken ct = default);

    /// <summary>True if <paramref name="candidate"/> is a strictly newer version than the running one.</summary>
    bool IsNewer(string candidate);
}

/// <summary>
/// Channel persistence + latest-version discovery for <c>pks update --self</c>. Discovery reuses
/// the existing NuGet client (<see cref="INuGetTemplateDiscoveryService"/>); the chosen channel is
/// persisted at config key <c>cli.update.channel</c>.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const string ChannelKey = "cli.update.channel";

    private readonly INuGetTemplateDiscoveryService _nuget;
    private readonly IConfigurationService _config;
    private readonly IInstallMethodDetector _detector;

    public UpdateService(INuGetTemplateDiscoveryService nuget, IConfigurationService config, IInstallMethodDetector detector)
    {
        _nuget = nuget;
        _config = config;
        _detector = detector;
    }

    public string PackageId => "pks-cli";

    public string CurrentVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Strip any build metadata after '+' (e.g. "6.14.0+abc123").
            if (!string.IsNullOrEmpty(info))
                return info.Split('+')[0];
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    public InstallMethod InstallMethod => _detector.Detect();

    public async Task<UpdateChannel?> GetChannelAsync()
    {
        var raw = await _config.GetAsync(ChannelKey);
        return Enum.TryParse<UpdateChannel>(raw, ignoreCase: true, out var ch) ? ch : null;
    }

    public Task SetChannelAsync(UpdateChannel channel)
        => _config.SetAsync(ChannelKey, channel.ToString().ToLowerInvariant(), global: true);

    public Task<string?> GetLatestVersionAsync(UpdateChannel channel, CancellationToken ct = default)
        => _nuget.GetLatestVersionAsync(PackageId, includePrerelease: channel == UpdateChannel.Daily, ct);

    public bool IsNewer(string candidate) => IsNewerThan(candidate, CurrentVersion);

    /// <summary>True if <paramref name="candidate"/> is a strictly newer semver than <paramref name="current"/>.</summary>
    internal static bool IsNewerThan(string candidate, string current)
    {
        if (!NuGetVersion.TryParse(candidate, out var c)) return false;
        if (!NuGetVersion.TryParse(current, out var cur)) return true; // unknown current → allow
        return c > cur;
    }
}
