using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure argv builder for <c>pks agentics runner claude-login</c> (docs/remote-runner-targets-plan.md
/// Phase 5, work item 2). Builds an interactive <c>docker run -it</c> that opens a Claude Code login
/// inside a one-off container, mounting the same <c>pks-claude-*</c> volume (see
/// <see cref="ClaudeCredentialVolumes"/>) a job spawn mounts at
/// <see cref="ClaudeCredentialVolumes.MountTarget"/> -- so a login here is exactly what a later
/// headless spawn needs to find. <see cref="Build"/> wraps it in <c>ssh -t</c> for a remote target;
/// <see cref="BuildLocal"/> runs the same container against the local Docker daemon, which is the
/// only seeding path available when the runner is the local process rather than an SSH handoff.
/// Kept as pure (target, volumeName, keyPath) -&gt; argv functions, separate from the interactive
/// <see cref="IInteractiveProcessLauncher"/> plumbing, so the exact argv is directly unit-testable
/// without actually launching an interactive session.
/// </summary>
public static class ClaudeLoginCommandBuilder
{
    private const string RemoteImage = "node:22"; // Node 20 pins npm to claude 2.1.197 -- see ClaudeRuntimeCheck.
    private const string ConfigDir = ClaudeCredentialVolumes.MountTarget;

    /// <summary>
    /// What runs inside the one-off container. Two things it must not get wrong:
    /// npm's global prefix is root-owned, so the install has to happen as root -- but claude then has
    /// to run as **uid 1000**, because that is the user a job's agent pane runs as and claude rewrites
    /// its credential file on every OAuth refresh. A volume seeded by root comes back as
    /// <c>Permission denied</c> on refresh, which surfaces in the job as "OAuth session expired" and
    /// sends you chasing a stale token that is in fact perfectly good.
    /// </summary>
    private const string LoginScript =
        "npm install -g @anthropic-ai/claude-code >/dev/null 2>&1 && " +
        "mkdir -p " + ConfigDir + " && chown -R node:node " + ConfigDir + " && " +
        "exec su node -c \"CLAUDE_CONFIG_DIR=" + ConfigDir + " claude\"";

    /// <summary>
    /// The <c>docker</c> argv (without the executable name) that opens the login container for
    /// <paramref name="volumeName"/>. Single source of truth for both the local and the ssh form.
    /// </summary>
    public static IReadOnlyList<string> BuildDockerArgs(string volumeName)
    {
        ArgumentNullException.ThrowIfNull(volumeName);

        return new[]
        {
            "run", "-it", "--rm",
            "-v", $"{volumeName}:{ConfigDir}",
            "-e", $"CLAUDE_CONFIG_DIR={ConfigDir}",
            RemoteImage,
            "bash", "-c", LoginScript,
        };
    }

    /// <summary>Runs the login container against the local Docker daemon (no SSH target).</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) BuildLocal(string volumeName) =>
        ("docker", BuildDockerArgs(volumeName));

    /// <summary>
    /// Builds the ssh argv. <paramref name="keyPath"/> is the caller's already-resolved key path
    /// (either <see cref="SshTarget.KeyPath"/> directly, or a <c>MaterializedKey.Path</c> for a
    /// pks-held key) -- null/empty omits <c>-i</c> and lets ssh use its own default identity
    /// resolution, matching <c>SshConnectCommand</c>'s convention.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) Build(SshTarget target, string volumeName, string? keyPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(volumeName);

        var args = new List<string>
        {
            "-t",
            "-o", "StrictHostKeyChecking=no",
            "-p", target.Port.ToString(),
        };

        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-o");
            args.Add("IdentitiesOnly=yes");
            args.Add("-i");
            args.Add(keyPath);
        }

        args.Add($"{target.Username}@{target.Host}");
        args.Add(BuildRemoteCommand(volumeName));

        return ("ssh", args);
    }

    private static string BuildRemoteCommand(string volumeName) =>
        "docker " + string.Join(" ", BuildDockerArgs(volumeName).Select(ShellQuote));

    /// <summary>
    /// Quotes one argv element for the remote shell. ssh concatenates its command words and hands the
    /// result to a shell on the far side, so the login script -- which contains spaces and its own
    /// double quotes -- has to survive that trip as a single word.
    /// </summary>
    private static string ShellQuote(string arg) =>
        Regex.IsMatch(arg, @"^[A-Za-z0-9_@%+=:,./-]+$")
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";
}
