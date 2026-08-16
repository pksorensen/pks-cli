namespace PKS.Infrastructure.Services.Runner;

/// <summary>How a runner process gets (re)invoked on the machine that will host it.</summary>
public enum RunnerLauncherKind
{
    /// <summary>This very executable, by absolute path. Only meaningful locally.</summary>
    Self,
    /// <summary>The self-contained <c>pks</c> binary on the target's PATH.</summary>
    Pks,
    /// <summary><c>dnx pks-cli --</c> (requires a working dotnet install).</summary>
    Dnx,
    /// <summary><c>npx -y @pks-cli/cli@latest</c> (requires node).</summary>
    Npx,
}

/// <summary>
/// The argv prefix that starts a new pks process, plus how it was chosen.
/// <see cref="Prefix"/> is already shell-ready: append the pks arguments and run it.
/// </summary>
public sealed record RunnerLauncherCommand(RunnerLauncherKind Kind, string Prefix)
{
    public string BuildCommandLine(string arguments) =>
        string.IsNullOrWhiteSpace(arguments) ? Prefix : $"{Prefix} {arguments}";
}

/// <summary>
/// Decides how to start a runner process, locally and remotely.
///
/// This exists because the SSH handoff used to hardcode <c>dnx pks-cli --</c>, which assumes a
/// working .NET SDK on the target. That assumption is wrong on at least one of our own machines:
/// <c>projects.si14agents.com</c> has a <c>/usr/lib/dotnet</c> with no <c>host/fxr</c>, so every
/// <c>dnx</c> invocation fails before it reaches pks -- while a perfectly good self-contained
/// <c>pks</c> binary sits in <c>/usr/local/bin</c>. Probe, then pick, rather than assume.
/// </summary>
public static class RunnerLauncher
{
    /// <summary>
    /// How to re-invoke *this* process. Used by a local detached start, so the background runner is
    /// byte-for-byte the same build as the foreground command the operator just typed -- no version
    /// skew between what they tested interactively and what ends up in tmux.
    ///
    /// Framework-dependent deployments run as <c>dotnet some.dll</c>, where ProcessPath is the
    /// dotnet host rather than the app; that case is detected and the assembly path is put back.
    /// </summary>
    public static RunnerLauncherCommand ResolveSelf()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(processPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(processPath);
            if (!string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
                return new RunnerLauncherCommand(RunnerLauncherKind.Self, Quote(processPath));

            var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(assembly))
                return new RunnerLauncherCommand(RunnerLauncherKind.Self, $"{Quote(processPath)} {Quote(assembly)}");
        }

        // Last resort: whatever `pks` resolves to on PATH. Only reachable in exotic hosts where
        // ProcessPath is empty (single-file trimmed edge cases, some test hosts).
        return new RunnerLauncherCommand(RunnerLauncherKind.Pks, "pks");
    }

    /// <summary>
    /// Pick a launcher for a remote target from what the probe found. Preference order is
    /// availability-first, not elegance-first: the installed binary needs no download and no
    /// runtime, dnx needs a working SDK, npx needs node and pulls a package.
    ///
    /// The npx form pins <c>@latest</c> deliberately. npx resolves a bare package name against
    /// whatever it already has in <c>~/.npm/_npx</c> and will happily reuse a months-old cached
    /// version without ever asking the registry -- which is exactly how a box ends up running
    /// 6.15.0 while the registry says 6.25.0.
    /// </summary>
    public static RunnerLauncherCommand? ResolveRemote(SshProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.PksAvailable) return new RunnerLauncherCommand(RunnerLauncherKind.Pks, "pks");
        if (probe.DnxAvailable) return new RunnerLauncherCommand(RunnerLauncherKind.Dnx, "dnx pks-cli --");
        if (probe.NpxAvailable) return new RunnerLauncherCommand(RunnerLauncherKind.Npx, "npx -y @pks-cli/cli@latest");
        return null;
    }

    /// <summary>Single-quote a path for a POSIX shell / tmux command string.</summary>
    public static string Quote(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '$' or '`')
            ? "'" + value.Replace("'", "'\\''") + "'"
            : value;
}
