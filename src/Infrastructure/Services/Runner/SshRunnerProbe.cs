using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Readiness snapshot of a remote SSH target for hosting an agentics runner
/// (docs/remote-runner-targets-plan.md Phase 4, work item 3). Docker/tmux/dotnet/dnx are each
/// probed with a single best-effort remote shell command -- a missing tool never throws, it just
/// reports as unavailable so the operator gets one readable summary instead of an exception.
/// </summary>
public sealed record SshProbeResult(
    bool DockerAvailable,
    bool TmuxAvailable,
    string? TmuxVersion,
    bool DotnetAvailable,
    string? DotnetVersion,
    bool DnxAvailable)
{
    /// <summary>
    /// Tmux is the hard dependency (vibecast requires it -- see CLAUDE.md), Docker is the whole
    /// point of handing off (this flow only triggers when local Docker is unavailable), and dnx is
    /// how the remote runner process itself gets launched (<c>dnx pks-cli -- agentics runner start
    /// ...</c>). Dotnet's own version is informational only -- dnx already implies a working dotnet
    /// install, it's surfaced separately purely for the readiness summary.
    /// </summary>
    public bool IsReady => DockerAvailable && TmuxAvailable && DnxAvailable;
}

/// <summary>
/// Builds and parses the single-shot remote probe command. Kept as static, pure methods (no SSH
/// dependency) so the output parsing is unit-testable without a live connection or a mocked
/// <see cref="ISshCommandRunner"/> -- only <see cref="ProbeAsync"/> itself needs one.
/// </summary>
public static class SshRunnerProbe
{
    private const string DockerMarker = "PKS_PROBE_DOCKER=";
    private const string TmuxMarker = "PKS_PROBE_TMUX=";
    private const string DotnetMarker = "PKS_PROBE_DOTNET=";
    private const string DnxMarker = "PKS_PROBE_DNX=";
    private const string MissingSentinel = "MISSING";

    /// <summary>
    /// The remote command run over SSH. Deliberately uses only single quotes for nesting (never
    /// double quotes) -- <c>SshCommandRunner.ExecuteProcessAsync</c> naively wraps any space-
    /// containing argument in an unescaped outer <c>"..."</c> pair for the local ssh invocation, so
    /// an embedded double quote here would corrupt that wrapping. Matches the established
    /// <c>docker info --format '{{.ServerVersion}}'</c> precedent elsewhere in this codebase.
    /// </summary>
    public static string BuildProbeCommand() =>
        "sh -c 'echo " + DockerMarker + "$(docker info >/dev/null 2>&1 && echo ok || echo " + MissingSentinel + "); " +
        "echo " + TmuxMarker + "$(tmux -V 2>/dev/null || echo " + MissingSentinel + "); " +
        "echo " + DotnetMarker + "$(dotnet --version 2>/dev/null || echo " + MissingSentinel + "); " +
        "echo " + DnxMarker + "$(command -v dnx >/dev/null 2>&1 && echo ok || echo " + MissingSentinel + ")'";

    /// <summary>Runs the probe over SSH and parses the result. Never throws on tool-not-found --
    /// only on a connection-level failure (non-zero exit / empty output), which is surfaced as an
    /// all-unavailable result rather than an exception, since "couldn't even connect" and "connected
    /// but nothing is installed" both mean the same thing to a caller deciding whether to hand off.</summary>
    public static async Task<SshProbeResult> ProbeAsync(
        ISshCommandRunner runner, RemoteHostConfig host, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(host);

        var result = await runner.RunAsync(host, BuildProbeCommand(), ct);
        return ParseProbeOutput(result.StdOut);
    }

    /// <summary>Pure parser over the probe command's stdout -- unit-testable without SSH.</summary>
    internal static SshProbeResult ParseProbeOutput(string stdout)
    {
        var docker = ExtractMarker(stdout, DockerMarker);
        var tmux = ExtractMarker(stdout, TmuxMarker);
        var dotnet = ExtractMarker(stdout, DotnetMarker);
        var dnx = ExtractMarker(stdout, DnxMarker);

        return new SshProbeResult(
            DockerAvailable: docker is "ok",
            TmuxAvailable: tmux != null && tmux != MissingSentinel,
            TmuxVersion: tmux != null && tmux != MissingSentinel ? tmux : null,
            DotnetAvailable: dotnet != null && dotnet != MissingSentinel,
            DotnetVersion: dotnet != null && dotnet != MissingSentinel ? dotnet : null,
            DnxAvailable: dnx is "ok");
    }

    private static string? ExtractMarker(string stdout, string marker)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                var value = line[marker.Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }
}
