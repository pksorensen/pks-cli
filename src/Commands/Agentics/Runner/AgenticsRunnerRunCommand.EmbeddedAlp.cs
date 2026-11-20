using System.Text.Json;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Embedded-ALP support: the pieces of a job that change when the runner is driven by a
/// self-contained ALP server (the sandbox controller) instead of agentics.dk.
///
/// Every helper here is opt-in through fields agentics.dk never sends
/// (<c>operatorConfig.broadcast</c>, <c>operatorConfig.serverUrl</c>, a commit-back token without a
/// credential kind). With those absent the runner's output is byte-for-byte what it was before —
/// the tests in <c>AgenticsRunnerEmbeddedAlpTests</c> pin that.
/// </summary>
public partial class AgenticsRunnerRunCommand
{
    /// <summary>Upper bound on the <c>logs</c> text an embedded completion PATCH carries.</summary>
    internal const int EmbeddedCompletionLogsMaxChars = 16_000;

    /// <summary>
    /// True when the job's operator config carries one of the embedded-ALP fields. This is the gate
    /// for the richer completion PATCH: agentics.dk stores <c>logs</c> on the job and reads
    /// <c>sessionId</c> on completion, so it keeps getting exactly <c>{status, conclusion}</c>.
    /// </summary>
    internal static bool IsEmbeddedOperatorConfig(RunnerOperatorConfig? config) =>
        config != null &&
        (!string.IsNullOrWhiteSpace(config.Broadcast) || !string.IsNullOrWhiteSpace(config.ServerUrl));

    /// <summary>
    /// The only shape <c>operatorConfig.serverUrl</c> may take: <c>http(s)://host[:port]</c> with an
    /// optional path prefix of plain segments and optional trailing slashes. Host names may carry
    /// <c>_</c> (compose-style container names). No user info, query,
    /// fragment, percent-escapes, whitespace or shell metacharacters — the value is written into
    /// start.sh and into the <c>bash -c</c> reachability check, so anything a shell could interpret
    /// is refused rather than escaped. vibecast honours a path prefix (its <c>BasePath</c>), which is
    /// why a path is allowed at all.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex OperatorServerUrlShape = new(
        @"^https?://(?:[A-Za-z0-9_](?:[A-Za-z0-9_.\-]*[A-Za-z0-9_])?|\[[0-9A-Fa-f:.]+\])(?::[0-9]{1,5})?(?:/[A-Za-z0-9._~\-]+)*/*\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// <c>operatorConfig.serverUrl</c> when it is an absolute http(s) URL of the shape
    /// <see cref="OperatorServerUrlShape"/> allows; otherwise null. The value is passed on as
    /// written except that trailing slashes are removed (the controller strips them too, and
    /// vibecast appends <c>/api/…</c> to it). Anything else (a bare host, a typo, another scheme,
    /// a query, characters a shell would interpret) is ignored rather than exported, so a bad value
    /// degrades to the runner's own derived address — with a warning, see
    /// <see cref="RejectedOperatorServerUrlWarning"/>.
    /// </summary>
    internal static string? ValidOperatorServerUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        var trimmed = serverUrl.Trim();
        if (!OperatorServerUrlShape.IsMatch(trimmed)) return null;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;
        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// A console warning when <c>operatorConfig.serverUrl</c> is set but was refused by
    /// <see cref="ValidOperatorServerUrl"/>, or null when there is nothing to say. Without it the
    /// job silently falls back to the bare derived <c>host:port</c>, which vibecast treats as
    /// https/wss for any non-localhost host — so an http-only controller becomes unreachable with
    /// no hint why.
    /// </summary>
    internal static string? RejectedOperatorServerUrlWarning(string? serverUrl, string fallback) =>
        !string.IsNullOrWhiteSpace(serverUrl) && ValidOperatorServerUrl(serverUrl) == null
            ? $"operatorConfig.serverUrl \"{serverUrl.Trim()}\" is not an http(s)://host[:port][/path] URL; ignoring it and using AGENTICS_SERVER={fallback} (a bare non-localhost host means https/wss to vibecast)."
            : null;

    /// <summary>
    /// The value vibecast receives as <c>AGENTICS_SERVER</c>: <c>operatorConfig.serverUrl</c>
    /// as written (less trailing slashes) when valid, else the bare <c>host:port</c> the runner derived from its registration.
    /// </summary>
    internal static string ResolveContainerAgenticsServer(string derivedHostPort, string? serverUrl) =>
        ValidOperatorServerUrl(serverUrl) ?? derivedHostPort;

    /// <summary>
    /// The start.sh lines for <c>AGENTICS_SERVER</c> and its deprecated alias. Without a valid
    /// <paramref name="serverUrl"/> these are exactly the two lines the runner has always written.
    /// </summary>
    internal static IReadOnlyList<string> BuildAgenticsServerExportLines(string derivedHostPort, string? serverUrl)
    {
        var url = ValidOperatorServerUrl(serverUrl);
        if (url == null)
        {
            return new[]
            {
                $"export AGENTICS_SERVER={derivedHostPort}",
                $"export AGENTIC_SERVER={derivedHostPort}",
            };
        }
        var quoted = ShellSingleQuote(url);
        return new[]
        {
            $"export AGENTICS_SERVER={quoted}",
            $"export AGENTIC_SERVER={quoted}",
        };
    }

    /// <summary>
    /// The value for <c>VIBECAST_BROADCAST</c>, or null to leave it unset. Only an explicit
    /// <c>"off"</c> produces a value; absent, <c>"on"</c> and anything unrecognised keep vibecast's
    /// default (broadcast on).
    /// </summary>
    internal static string? VibecastBroadcastValue(RunnerOperatorConfig? config) =>
        string.Equals(config?.Broadcast?.Trim(), "off", StringComparison.OrdinalIgnoreCase) ? "off" : null;

    /// <summary>The start.sh lines for the broadcast switch — none unless broadcast is off.</summary>
    internal static IReadOnlyList<string> BuildBroadcastExportLines(RunnerOperatorConfig? config) =>
        VibecastBroadcastValue(config) is { } value
            ? new[] { $"export VIBECAST_BROADCAST={value}" }
            : Array.Empty<string>();

    /// <summary>
    /// The base URL the pre-flight reachability check curls from inside the container. Today's
    /// value is <c>{registration scheme}://{derived host:port}</c>; a valid serverUrl replaces it,
    /// because that is the address vibecast will actually use.
    /// </summary>
    internal static string ContainerReachCheckBase(string registrationScheme, string derivedHostPort, string? serverUrl) =>
        ValidOperatorServerUrl(serverUrl) ?? $"{registrationScheme}://{derivedHostPort}";

    /// <summary>
    /// The credential kind exported to vibecast for a commit-back repository. vibecast only uses
    /// the commit-back token when the kind is <c>agentics-repo</c> (see its
    /// <c>planAutoGitCredentials</c>); a token sent without a kind — the embedded controller's
    /// <c>commitBackRepository {url, token}</c>, e.g. a GitHub App installation token — would
    /// otherwise be exported and then ignored. agentics.dk always sends a kind, so its jobs are
    /// unaffected.
    /// </summary>
    internal static string? EffectiveCommitBackCredentialKind(string? credentialKind, string? token) =>
        string.IsNullOrWhiteSpace(credentialKind) && !string.IsNullOrWhiteSpace(token)
            ? "agentics-repo"
            : credentialKind;

    /// <summary>
    /// The start.sh lines for a commit-back repository. Kept identical to what the runner wrote
    /// before for every input agentics.dk produces; the only difference is the defaulted kind from
    /// <see cref="EffectiveCommitBackCredentialKind"/>.
    /// </summary>
    internal static IReadOnlyList<string> BuildCommitBackExportLines(CommitBackRepositoryDefinition commitBack)
    {
        var lines = new List<string>();
        var escapedUrl = commitBack.Url?.Replace("'", "'\\''");
        var escapedKind = EffectiveCommitBackCredentialKind(commitBack.CredentialKind, commitBack.Token)?.Replace("'", "'\\''");
        lines.Add($"export AGENTICS_COMMIT_BACK_URL='{escapedUrl}'");
        lines.Add($"export AGENTICS_COMMIT_BACK_CREDENTIAL_KIND='{escapedKind}'");
        if (!string.IsNullOrWhiteSpace(commitBack.Token))
        {
            var escapedToken = commitBack.Token.Replace("'", "'\\''");
            lines.Add($"export AGENTICS_COMMIT_BACK_TOKEN='{escapedToken}'");
        }
        return lines;
    }

    /// <summary>
    /// The conclusion of an embedded-ALP spawn job: <c>cancelled</c> when the runner's wait was
    /// cancelled, <c>failure</c> when the run recorded a failure reason, else <c>success</c>. This is
    /// only the runner's view — the controller still never treats a job without a session-event
    /// <c>end</c> as answered.
    /// </summary>
    internal static string EmbeddedSpawnConclusion(string? waitEndReason, string? failureReason) =>
        waitEndReason == "cancelled" ? "cancelled"
        : failureReason != null ? "failure"
        : "success";

    /// <summary>
    /// The final spawn-mode PATCH body for an embedded-ALP job. Additive over today's
    /// <c>{status, conclusion}</c>: it adds why the wait ended, the session it ran, and a bounded
    /// log excerpt, so a job that never got a session-event <c>end</c> still has something on it.
    /// The runner never holds the agent's answer itself — that arrives via vibecast's
    /// session-event — so there is deliberately no <c>result</c> member. Null members are omitted.
    /// </summary>
    internal static string BuildEmbeddedSpawnCompletionBody(
        string conclusion,
        string? completionReason,
        string? sessionId,
        string? broadcastId,
        string? logs)
    {
        var body = new Dictionary<string, string>
        {
            ["status"] = "completed",
            ["conclusion"] = conclusion,
        };
        if (!string.IsNullOrEmpty(completionReason)) body["completionReason"] = completionReason;
        if (!string.IsNullOrEmpty(sessionId)) body["sessionId"] = sessionId;
        if (!string.IsNullOrEmpty(broadcastId)) body["broadcastId"] = broadcastId;
        if (!string.IsNullOrEmpty(logs)) body["logs"] = logs;
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Composes the <c>logs</c> text of an embedded completion PATCH: the failure reason, the
    /// contents of the <c>.task-complete</c> signal file when the agent wrote one, and the tail of
    /// vibecast.log — capped at <see cref="EmbeddedCompletionLogsMaxChars"/> by keeping the end,
    /// where the reason a run stopped is written.
    /// </summary>
    internal static string? ComposeEmbeddedCompletionLogs(string? failureReason, string? taskCompleteContent, string? vibecastLogTail)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(failureReason))
            sb.Append("failure: ").Append(failureReason.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(taskCompleteContent))
            sb.Append("task-complete:\n").Append(taskCompleteContent.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(vibecastLogTail))
            sb.Append("vibecast.log (tail):\n").Append(vibecastLogTail.TrimEnd()).Append('\n');
        if (sb.Length == 0) return null;
        var text = sb.ToString();
        return text.Length <= EmbeddedCompletionLogsMaxChars
            ? text
            : "…\n" + text[^(EmbeddedCompletionLogsMaxChars - 2)..];
    }

    internal static string ShellSingleQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
