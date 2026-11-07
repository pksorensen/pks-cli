using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// What we wrote, and where, per connected profile — <c>~/.pks-cli/codex-profiles.json</c>.
///
/// <para>
/// <b>Why a ledger at all.</b> <c>~/.codex/config.toml</c> is written by at least four programs,
/// so "which tables are ours" cannot be inferred from the file. The only robust answer is to
/// record the names we wrote at the moment we wrote them, and remove by name later. When the
/// ledger and the file disagree the **file wins** — someone edited it by hand, which is their
/// right — and the ledger is corrected on the next connect.
/// </para>
///
/// <para>
/// No credential value is ever stored here, only the name of the environment variable that holds
/// one. The ledger is a plain JSON file; a secret in it would be a secret on disk.
/// </para>
/// </summary>
public sealed class CodexProfileLedger
{
    public int V { get; set; } = 1;

    /// <summary>Connected sources, keyed by their origin (e.g. <c>https://agentics.dk</c>).</summary>
    public Dictionary<string, CodexLedgerSource> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The namespace whose tables currently sit in <c>config.toml</c> — what a bare <c>codex</c>
    /// gets. Null when nothing is active, which is the clean state for <c>-p</c> switching.
    /// </summary>
    public string? Active { get; set; }

    // ---- persistence ----

    private const string FileName = "codex-profiles.json";

    /// <summary>
    /// Used for BOTH directions. Reading with different options than writing is how a state file
    /// becomes unreadable to the program that wrote it: the naming policy only applies to the
    /// serializer it is handed to, so a policy added for writing would silently start returning
    /// empty ledgers on read, and an empty ledger looks exactly like "nothing is connected".
    ///
    /// <para>
    /// Case-insensitive reading is what makes that impossible rather than merely unlikely — it
    /// accepts both this file's camelCase and the PascalCase earlier builds wrote, so an upgrade
    /// does not lose anybody's profiles. <c>DictionaryKeyPolicy</c> stays unset deliberately:
    /// the keys here are origins and profile ids, and camel-casing <c>https://agentics.dk</c>
    /// would corrupt them.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string Path() => SecurityFiles.PathFor(FileName);

    /// <summary>
    /// Loads the ledger, or an empty one.
    ///
    /// <para>
    /// A corrupt ledger returns empty rather than throwing. It is a cache of our own bookkeeping,
    /// not a source of truth: the worst case is that one orphaned table has to be removed by hand,
    /// which is a strictly better outcome than a CLI that cannot run at all.
    /// </para>
    /// </summary>
    public static CodexProfileLedger Load()
    {
        var path = Path();
        if (!File.Exists(path)) return new CodexProfileLedger();

        try
        {
            return JsonSerializer.Deserialize<CodexProfileLedger>(File.ReadAllText(path), JsonOpts) ?? new CodexProfileLedger();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Move it aside rather than let it be overwritten. Returning empty is the right
            // behaviour — a CLI that cannot run is worse than one that has forgotten — but the
            // next Save() would then write {} over the reader's only record of every profile
            // they have ever connected. Keeping the bytes costs nothing and is the difference
            // between "re-run connect" and "re-run connect, and good luck remembering which
            // projects were in there".
            try
            {
                var aside = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
                if (!File.Exists(aside)) File.Move(path, aside);
            }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                // Nothing further to try; an unreadable ledger already behaves as empty.
            }

            return new CodexProfileLedger();
        }
    }

    public void Save()
    {
        var path = Path();
        SecurityFiles.EnsureDirectory(path);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        SecurityFiles.Restrict(path);
    }

    // ---- queries ----

    /// <summary>Every connected profile across every source, as flat rows for listing.</summary>
    public IEnumerable<(string Origin, CodexLedgerSource Source, CodexLedgerProfile Profile)> AllProfiles()
    {
        foreach (var (origin, source) in Sources)
            foreach (var profile in source.Profiles.Values)
                yield return (origin, source, profile);
    }

    public CodexLedgerProfile? FindByNamespace(string ns) =>
        AllProfiles().FirstOrDefault(row =>
            string.Equals(row.Profile.Namespace, ns, StringComparison.OrdinalIgnoreCase)).Profile;
}

/// <summary>One platform we have connected to.</summary>
public sealed class CodexLedgerSource
{
    /// <summary>The platform's own short slug, as its profile list claims it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The slug actually used for namespaces on THIS machine, assigned at first connect and
    /// never changed afterwards.
    ///
    /// <para>
    /// Usually identical to <see cref="Id"/>. It differs when two origins claim the same one,
    /// which is not a hypothetical: a platform's local dev instance and its production instance
    /// run the same code, so they publish the same source id AND the same profile ids, and the
    /// client cannot tell them apart from the document alone. Trusting the claimed id would mean
    /// the second connect silently overwrites the first's layer file — the precise failure
    /// namespacing exists to prevent. Keeping it pinned here matters as much as deriving it:
    /// a slug that moved between runs would orphan every table written under the old one.
    /// </para>
    /// </summary>
    public string? EffectiveId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>The issuer we signed in against, so a re-run can tell an issuer change from a token expiry.</summary>
    public string? Issuer { get; set; }

    /// <summary>The <c>client_id</c> the platform handed out — a CIMD URL for Keycloak, a GUID for Entra.</summary>
    public string? ClientId { get; set; }

    public DateTimeOffset? LastConnected { get; set; }

    /// <summary>Profiles from this source, keyed by the platform's profile id.</summary>
    public Dictionary<string, CodexLedgerProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One profile, and the exact names it owns on this machine.</summary>
public sealed class CodexLedgerProfile
{
    /// <summary>The platform's id for it, unqualified.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// <c>&lt;source-id&gt;-&lt;profile-id&gt;</c>: what <c>codex -p</c> is given and what the layer
    /// file is named after.
    ///
    /// <para>
    /// Namespacing is not cosmetic. The local Aspire platform and production run the same code and
    /// therefore publish the same profile ids by construction, so without it connecting to one
    /// silently overwrites the other and the symptom is a 401 against the wrong host.
    /// </para>
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>The layer file, relative to <c>CODEX_HOME</c> (e.g. <c>agentics-my-project.config.toml</c>).</summary>
    public string? LayerFile { get; set; }

    /// <summary>Table names we wrote into the layer file, fully qualified (<c>mcp_servers.x</c>).</summary>
    public List<string> LayerTables { get; set; } = [];

    /// <summary>
    /// Table names we wrote into <c>config.toml</c> itself. Non-empty only for the active profile
    /// and for marketplaces, which cannot live in a layer file because <c>-p</c> is refused by
    /// <c>codex plugin</c>.
    /// </summary>
    public List<string> BaseTables { get; set; } = [];

    /// <summary>Bare top-level keys we set in <c>config.toml</c>, so <c>use</c> can hand them back.</summary>
    public List<string> BaseKeys { get; set; } = [];

    /// <summary>
    /// The name — never the value — of the environment variable holding this profile's credential.
    /// </summary>
    public string? CredentialEnvVar { get; set; }

    public DateTimeOffset? CredentialExpiresAt { get; set; }
}
