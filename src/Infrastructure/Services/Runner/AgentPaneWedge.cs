namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Recognizes an agent pane that is alive but parked on something only a human can clear.
///
/// The runner's completion rule used to be "the agent pane exists and has gone quiet", which
/// answers "did the agent start", not "did the agent work". A pane sitting on the OAuth screen,
/// on a Press-Enter gate, or answering every turn with "Not logged in" is quiet in exactly the
/// same way a finished job is — and was reported as completed/success with nothing to show for it.
/// vibecast auto-answers the gates it knows, so anything still on screen at idle time is either a
/// gate it does not know yet or one it could not clear. Both are failures, not successes.
/// </summary>
public static class AgentPaneWedge
{
    /// <summary>Marker pairs: every string in <c>All</c> must appear for <c>Reason</c> to apply.</summary>
    private static readonly (string Reason, string[] All)[] Signatures =
    [
        ("claude is not logged in (the credentials volume holds no usable token)",
            ["Not logged in", "/login"]),
        ("claude is waiting on the login-method picker",
            ["Select login method:"]),
        ("claude is waiting for an OAuth code to be pasted",
            ["Paste code here"]),
        ("claude is waiting on the bypass-permissions confirmation",
            ["Bypass Permissions mode", "Yes, I accept"]),
        ("claude is waiting on the workspace-trust dialog",
            ["Do you trust the files in this folder"]),
        ("claude is waiting on the onboarding tour gate",
            ["Learn the moves", "Skip for now"]),
        ("claude is waiting on the theme picker",
            ["Choose the text style"]),
        ("claude is waiting on the custom-API-key gate",
            ["Detected a custom API key"]),
        ("claude is waiting on a Press-Enter screen",
            ["Press Enter to continue"]),
    ];

    /// <summary>
    /// Returns why the pane is wedged, or <c>null</c> when nothing recognizable is on screen.
    /// Feed it the VISIBLE pane only (<c>tmux capture-pane -p</c> without <c>-S</c>): scrollback
    /// would match gates the agent already cleared minutes ago.
    /// </summary>
    public static string? Detect(string? paneText)
    {
        if (string.IsNullOrWhiteSpace(paneText)) return null;

        foreach (var (reason, all) in Signatures)
        {
            if (all.All(marker => paneText.Contains(marker, StringComparison.Ordinal)))
                return reason;
        }
        return null;
    }
}
