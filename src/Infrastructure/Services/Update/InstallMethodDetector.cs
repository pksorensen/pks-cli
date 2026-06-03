namespace PKS.Infrastructure.Services.Update;

/// <summary>How this pks executable was installed — determines how (and whether) it can self-replace.</summary>
public enum InstallMethod
{
    /// <summary>Self-contained binary baked into the devcontainer image at /usr/local/bin/pks, run as the
    /// pks user, who cannot write /usr/local/bin. Updates are delegated to a host-root command.</summary>
    Baked,
    /// <summary>`dotnet tool install -g pks-cli` — can self-replace via `dotnet tool update`.</summary>
    DotnetTool,
    /// <summary>npm `@pks-cli/cli` wrapper.</summary>
    Npm,
    /// <summary>A standalone binary the current user owns and can overwrite.</summary>
    StandaloneBinary,
    /// <summary>`dotnet run` from source, or an unrecognized layout.</summary>
    Unknown,
}

public interface IInstallMethodDetector
{
    InstallMethod Detect();
    /// <summary>Absolute path to the running pks executable (best effort).</summary>
    string? ExecutablePath { get; }
}

/// <summary>
/// Detects the install method from the running process path (and write access). Mirrors the
/// detect-and-delegate approach of `aspire update --self`.
/// </summary>
public sealed class InstallMethodDetector : IInstallMethodDetector
{
    public string? ExecutablePath => Environment.ProcessPath;

    public InstallMethod Detect()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return InstallMethod.Unknown;

        var normalized = path.Replace('\\', '/');

        // `dotnet run` / `dotnet pks.dll` → the host muxer, not a packaged tool.
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
            return InstallMethod.Unknown;

        if (normalized.Contains("/.dotnet/tools/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/.store/pks-cli/", StringComparison.OrdinalIgnoreCase))
            return InstallMethod.DotnetTool;

        if (normalized.Contains("/node_modules/@pks-cli/", StringComparison.OrdinalIgnoreCase))
            return InstallMethod.Npm;

        // Baked image: lives under a system path the running user can't write.
        if (normalized.StartsWith("/usr/local/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/usr/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("/opt/", StringComparison.OrdinalIgnoreCase))
        {
            return CanWrite(path) ? InstallMethod.StandaloneBinary : InstallMethod.Baked;
        }

        return CanWrite(path) ? InstallMethod.StandaloneBinary : InstallMethod.Unknown;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
