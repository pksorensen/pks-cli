using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Where a runner keeps the vault identity it uses for its own credentials.
///
/// A file, not a Docker volume — and the difference is the whole point. <see
/// cref="VaultIdentityVolumes"/> is a volume because a station's secret is released *into a
/// container*, and ADR 0011 scopes that identity to the station for a reason worth re-reading:
/// a line-wide default would hand it to the station whose entire job is reading
/// attacker-controlled content.
///
/// The runner process is not in a container. It holds a credential no job container ever sees —
/// the GitHub App's private key — and what crosses into a container is a one-hour, repo-scoped
/// installation token minted from it. That is a second plane, not a widening of the first, and
/// ADR 0012 records the distinction so the next reader does not "fix" it.
///
/// It is still per-project rather than per-host. Two projects on one box get two identities, so
/// a grant made for one is not silently usable by the other. That much of ADR 0011's instinct
/// survives the change of plane.
/// </summary>
public static class RunnerVaultIdentity
{
    /// <summary>
    /// Absolute path to the identity file for one runner registration. Under <c>~/.pks-cli</c>,
    /// beside the local-runner records, because it belongs to the same thing they do.
    /// </summary>
    public static string ResolvePath(string owner, string project, string? home = null)
    {
        var root = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(
            root,
            ".pks-cli",
            "vault",
            $"{ClaudeCredentialVolumes.Sanitize(owner)}-{ClaudeCredentialVolumes.Sanitize(project)}",
            "identity.json");
    }
}

/// <summary>
/// The platform's answer to "which GitHub App should this runner act as". A pointer, never a
/// credential: <c>appId</c> and <c>slug</c> are public identity, and the private key is only
/// located, not carried.
/// </summary>
public sealed record GitHubAppPointer(
    string AppId,
    string Slug,
    string VaultServer,
    string VaultOwner,
    string VaultId,
    string ItemId,
    string? Field)
{
    /// <summary>The item field holding the PEM. Defaults to <c>private_key</c>.</summary>
    public string FieldOrDefault => string.IsNullOrWhiteSpace(Field) ? "private_key" : Field!;

    /// <summary>
    /// Reads the pointer out of a <c>repo-info</c> response. A malformed or partial pointer is
    /// treated as absent rather than as an error: the runner's own preflight is a better place to
    /// say "this project declares an App" than a JSON parser is.
    /// </summary>
    public static GitHubAppPointer? FromRepoInfo(JsonElement root)
    {
        if (!root.TryGetProperty("gitHubApp", out var app) || app.ValueKind != JsonValueKind.Object)
            return null;
        if (!app.TryGetProperty("vault", out var vault) || vault.ValueKind != JsonValueKind.Object)
            return null;

        var appId = Text(app, "appId");
        var slug = Text(app, "slug");
        var server = Text(vault, "server");
        var owner = Text(vault, "owner");
        var vaultId = Text(vault, "vaultId");
        var itemId = Text(vault, "itemId");
        var field = Text(vault, "field");

        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(server)
            || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(vaultId) || string.IsNullOrEmpty(itemId))
            return null;

        return new GitHubAppPointer(appId, slug, server, owner, vaultId, itemId,
            string.IsNullOrEmpty(field) ? null : field);
    }

    private static string Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() ?? ""
            : "";
}

/// <summary>
/// Turns the platform's App pointer into a usable <see cref="GitHubAppConfig"/> by fetching the
/// private key from the vault — so <c>pks agentics runner run</c> is the whole command again,
/// instead of something a person has to remember to wrap.
///
/// Two rules govern every path through here, and both are the same rule as
/// <see cref="GitHubAppConfigResolver"/>'s: <b>never silently do something other than what was
/// declared</b>. If the project declares an App and the key cannot be fetched, this throws with
/// the missing step spelled out; it does not fall back to the operator's personal token, because
/// that would attribute the App's work to a human and hand the job far more access than it asked
/// for. And if the environment declares one App while the platform declares another, it refuses
/// rather than picking a winner.
/// </summary>
public sealed class GitHubAppProvisioner
{
    private readonly IVaultCliService _vault;

    public GitHubAppProvisioner(IVaultCliService vault) => _vault = vault;

    /// <summary>
    /// How long to block on a human approving the release. Generous because it is a startup cost
    /// paid once, and a runner that gives up after seconds would be unusable with any grant whose
    /// consent mode is not <c>never</c>.
    /// </summary>
    public static readonly TimeSpan ApprovalWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves the App this runner should act as. Environment first — an operator who exported
    /// <c>GITHUB_APP_*</c> did so deliberately, and the old wrapper form has to keep working —
    /// then the platform's pointer.
    /// </summary>
    /// <param name="pointer">The project's pointer, or null when it declares no App.</param>
    /// <param name="identityPath">This runner's vault identity file.</param>
    /// <param name="onNotice">Relay for approval progress; the wait can be minutes.</param>
    public async Task<GitHubAppConfig?> ResolveAsync(
        GitHubAppPointer? pointer,
        string identityPath,
        Action<string>? onNotice = null,
        Func<string, string?>? readVariable = null,
        CancellationToken ct = default)
    {
        var fromEnvironment = GitHubAppConfigResolver.Resolve(readVariable);

        if (fromEnvironment != null && pointer != null && fromEnvironment.AppId != pointer.AppId)
        {
            // Deliberately not "environment wins". Two different Apps means two different bots
            // and two different sets of repository permissions, and the operator believes one of
            // them is in force. Guessing which would produce commits attributed to a bot nobody
            // configured — exactly the failure App mode exists to prevent.
            throw new InvalidOperationException(
                $"This runner is told to act as two different GitHub Apps: the environment says App "
                + $"{fromEnvironment.AppId} ({fromEnvironment.Slug}) and the project on the platform says App "
                + $"{pointer.AppId} ({pointer.Slug}).\n\n"
                + "Refusing to guess. Clear GITHUB_APP_ID from this runner's environment to use the "
                + "project's App, or change the project's setting to match.");
        }

        if (fromEnvironment != null) return fromEnvironment;
        if (pointer == null) return null;

        var identity = await RequireEnrolledIdentityAsync(pointer, identityPath, ct);
        await RequireGrantAsync(pointer, identityPath, onNotice, ct);

        var pem = await _vault.ReadFieldAsync(
            identityPath, pointer.VaultId, pointer.ItemId, pointer.FieldOrDefault,
            ApprovalWait, onNotice, ct);

        // Held only here and in GitHubAppTokenService's parsed key, both in this process. It is
        // never written to disk, never exported into a child's environment, and never reaches a
        // job container — what crosses that boundary is an installation token, minted per request.
        _ = identity;

        return new GitHubAppConfig(pointer.AppId, pointer.Slug, pem);
    }

    private async Task<VaultAgentIdentity> RequireEnrolledIdentityAsync(
        GitHubAppPointer pointer, string identityPath, CancellationToken ct)
    {
        var identity = await _vault.WhoAmIAsync(identityPath, ct);
        if (identity == null)
        {
            throw new InvalidOperationException(
                $"This project uses GitHub App {pointer.Slug}, whose private key lives in the vault at "
                + $"{pointer.VaultServer}. This runner has no vault identity yet.\n\n"
                + "Enrolment cannot be self-served: it needs a one-shot token that only the vault's "
                + "owner can mint, on their own machine. Ask them to run:\n\n"
                + $"  vault agent add --owner {pointer.VaultOwner} --host-label \"<this host>\"\n\n"
                + "then run the `vault agent enrol …` line it prints, here, with:\n\n"
                + $"  --identity {identityPath} --protection none\n\n"
                + "`--protection none` is deliberate for a headless runner: a passphrase-protected "
                + "identity blocks on a prompt nobody is there to answer. Anyone who can read that "
                + "file can be this agent until the owner revokes it, so keep the host accordingly.");
        }

        if (!ServerMatches(identity.Server, pointer.VaultServer))
        {
            throw new InvalidOperationException(
                $"This runner's vault identity is enrolled at {identity.Server}, but the project's App key "
                + $"is at {pointer.VaultServer}.\n\n"
                + "An identity file pins its server, so this cannot be pointed at the other vault. Enrol a "
                + $"second identity for that server at a different --identity path, or fix the project's "
                + "GitHub App settings on the platform.");
        }

        if (!identity.HasServiceAccount)
        {
            throw new InvalidOperationException(
                $"This runner's vault identity ({identity.AgentId}) has no service account, so every call to "
                + $"{identity.Server} is refused with 401 before any grant is consulted.\n\n"
                + "Ask the vault's owner for this host's service account, then store it once:\n\n"
                + "  vault agent credentials --client-id <id> --client-secret <secret> "
                + $"--identity {identityPath}\n\n"
                + "Do not re-enrol to fix this — that mints a NEW agent id and orphans every existing grant.");
        }

        return identity;
    }

    private async Task RequireGrantAsync(
        GitHubAppPointer pointer, string identityPath, Action<string>? onNotice, CancellationToken ct)
    {
        var grants = await _vault.GrantsAsync(identityPath, ct);
        var match = grants.FirstOrDefault(g =>
            string.Equals(g.VaultId, pointer.VaultId, StringComparison.Ordinal)
            && string.Equals(g.ItemId, pointer.ItemId, StringComparison.Ordinal));

        if (match == null)
        {
            var identity = await _vault.WhoAmIAsync(identityPath, ct);
            throw new InvalidOperationException(
                $"This runner's vault agent has no grant on {pointer.ItemId}, which holds GitHub App "
                + $"{pointer.Slug}'s private key.\n\n"
                + "Ask the vault's owner to run, on their own machine:\n\n"
                + $"  vault agent grant {pointer.VaultId} {pointer.ItemId} --agent {identity?.AgentId ?? "<this agent>"} \\\n"
                + $"      --purpose \"GitHub App {pointer.Slug} for the runner\" --consent never --expires 8760h\n\n"
                + "`--consent never` is the right choice here: the key is read at startup, and a grant that "
                + "pages a human means every restart waits for someone to tap approve.");
        }

        if (!match.Usable)
        {
            throw new InvalidOperationException(
                $"This runner's grant on {pointer.ItemId} (GitHub App {pointer.Slug}'s private key) is not "
                + $"usable: {(string.IsNullOrEmpty(match.Reason) ? "no reason given" : match.Reason)}.\n\n"
                + $"It expires {match.ExpiresAt} and has {match.UsesLeft} of {match.MaxUses} reads left. "
                + "Ask the vault's owner to re-grant it.");
        }

        // Said out loud rather than discovered. A grant whose consent mode is not `never` pages a
        // human on every read, and the read happens at startup -- so an unattended restart at 2am
        // waits for someone to tap approve. That may be exactly what the owner wants for a key this
        // valuable; it should be a stated choice.
        onNotice?.Invoke(match.NeedsApproval
            ? $"grant on {pointer.ItemId} needs approval on every read (consent {match.ConsentMode}) — "
              + "this and every restart will wait for a human"
            : $"grant on {pointer.ItemId} is standing (consent {match.ConsentMode}), expires {match.ExpiresAt}");
    }

    /// <summary>
    /// Compares two vault URLs for the purposes of "is this the same vault". Trailing slashes and
    /// case in the host are noise; anything else is a real difference.
    /// </summary>
    internal static bool ServerMatches(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
