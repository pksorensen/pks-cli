namespace PKS.Infrastructure.Services.Security;

/// <summary>Helpers for the on-disk security state under <c>~/.pks-cli/</c>.</summary>
internal static class SecurityFiles
{
    /// <summary>Default path for a security file in the pks config home (honors the running user's HOME).</summary>
    public static string PathFor(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", fileName);

    /// <summary>Restrict a file to owner read/write (0600). Best-effort; no-op on Windows.</summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
    }

    /// <summary>Restrict a directory to owner read/write/execute (0700). Best-effort; no-op on Windows.</summary>
    public static void RestrictDir(string dir)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* best effort */ }
    }

    public static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
}
