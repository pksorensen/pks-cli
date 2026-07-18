using System.Diagnostics;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Launches an interactive child process with an inherited console (no stdout/stderr
/// redirection), for flows like <c>ssh -t</c> that need a real pty -- distinct from
/// <c>IProcessRunner</c>, which captures output and is unsuitable here. Mirrors the raw
/// <see cref="Process"/> pattern already used directly in <c>SshConnectCommand</c>, extracted to an
/// interface so callers (e.g. <c>pks agentics runner claude-login</c>) can be unit-tested against a
/// mock rather than actually spawning ssh.
/// </summary>
public interface IInteractiveProcessLauncher
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> attached to the
    /// current console, blocking until it exits. Returns the process's exit code, or -1 if the
    /// process could not be started.</summary>
    Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

public sealed class InteractiveProcessLauncher : IInteractiveProcessLauncher
{
    public Task<int> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc == null)
            return Task.FromResult(-1);

        proc.WaitForExit();
        return Task.FromResult(proc.ExitCode);
    }
}
