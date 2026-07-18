namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Pure argv builder for <c>pks agentics runner claude-login</c> (docs/remote-runner-targets-plan.md
/// Phase 5, work item 2). Builds an <c>ssh -t ... -- docker run -it ...</c> invocation that opens an
/// interactive Claude Code login session inside a one-off container on the target, mounting the same
/// <c>pks-claude-*</c> volume (see <see cref="ClaudeCredentialVolumes"/>) that devcontainer spawns
/// mount at <c>/home/node/.claude</c> (templates/devcontainer/content/.devcontainer/devcontainer.json)
/// -- so a login here is exactly what a later headless spawn needs to find. Kept as a pure
/// (target, volumeName, keyPath) -&gt; argv function, separate from the interactive
/// <see cref="IInteractiveProcessLauncher"/> plumbing, so the exact argv is directly unit-testable
/// without actually launching an interactive ssh session.
/// </summary>
public static class ClaudeLoginCommandBuilder
{
    private const string RemoteImage = "node:20";

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
        "docker run -it --rm " +
        $"-v {volumeName}:/home/node/.claude " +
        "-e CLAUDE_CONFIG_DIR=/home/node/.claude " +
        $"{RemoteImage} " +
        "bash -c 'npm install -g @anthropic-ai/claude-code >/dev/null 2>&1 && claude'";
}
