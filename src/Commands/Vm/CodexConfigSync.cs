using System.Text;
using System.Text.RegularExpressions;

namespace PKS.Commands.Vm;

/// <summary>What a discovered file is, which decides both how it is rewritten and how it reads in
/// the confirmation table.</summary>
public enum CodexSyncKind
{
    /// <summary><c>config.toml</c>. The one required file: it names the provider, the header and the
    /// catalogue.</summary>
    Config,

    /// <summary><c>&lt;customer&gt;.config.toml</c> — what <c>codex -p &lt;customer&gt;</c> layers on
    /// top. Carries a <c>model_catalog_json</c> of its own, so it needs the same path rewrite.</summary>
    Profile,

    /// <summary><c>models.&lt;customer&gt;.azure.json</c>. Copied verbatim: it holds model names and
    /// gateway routes, no credential and no local path.</summary>
    Catalogue,
}

/// <summary>One file to move, as the confirmation table shows it.</summary>
/// <param name="LocalPath">Absolute path on this machine.</param>
/// <param name="Name">File name, which is also what it is called under the remote account's
/// <c>.codex</c>. The directory is per-account and never copied from here.</param>
/// <param name="Kind">Which of the three kinds it is.</param>
/// <param name="Bytes">Size, for the table.</param>
public sealed record CodexSyncItem(string LocalPath, string Name, CodexSyncKind Kind, long Bytes);

/// <summary>
/// The outcome of <see cref="CodexConfigSync.Rewrite"/>. Variable <em>names</em> are reported;
/// values never are, so that neither the console nor a log can carry the key.
/// </summary>
/// <param name="Text">The file as it should land on the box: LF, no BOM.</param>
/// <param name="ResolvedVariables">Variables that were found and inlined, by name.</param>
/// <param name="MissingVariables">Variables the file names that this machine does not have. A
/// non-empty list is fatal: inlining an empty value produces the same 401 as changing nothing.</param>
/// <param name="Refusals">Reasons this file must not be sent. Non-empty is fatal.</param>
/// <param name="Warnings">Things worth saying that do not stop the transfer.</param>
public sealed record CodexRewriteResult(
    string Text,
    IReadOnlyList<string> ResolvedVariables,
    IReadOnlyList<string> MissingVariables,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Whether this file may be sent.</summary>
    public bool Ok => MissingVariables.Count == 0 && Refusals.Count == 0;
}

/// <summary>
/// Moves a working local Codex configuration onto a remote box, with the one mechanical change that
/// a remote box requires.
///
/// <para><b>This authors nothing.</b> The gateway's base URLs, the customer ids, the model names and
/// the per-customer catalogues are all decided by the ContextOne installer
/// (<c>Install-FoundryCodex.ps1</c>), which signs the reader in, asks Home which customers they are
/// on and writes the files. Duplicating any of that here would mean maintaining gateway naming in
/// two places. So this reads what that installer already produced and transports it.</para>
///
/// <para><b>The one change.</b> The installer puts the credential in a user environment variable and
/// the file names it with <c>env_http_headers</c>. That cannot work on a box: Codex Desktop starts
/// <c>codex app-server</c> over ssh, the shell on the other end reads no profile, and the variable
/// resolves to empty — so the gateway answers 401, which reads as a bad key rather than a missing
/// variable. Measured 2026-11-02. The remote copy therefore carries the value literally in
/// <c>http_headers</c>, in a <c>0600</c> file owned by the account.</para>
///
/// <para>Pure on purpose, in the shape of <see cref="LocalSshConfig"/>: the rewrite rules are the
/// part that can be wrong in a way no amount of ssh debugging would reveal, so they are testable
/// without a box, a home directory or a credential.</para>
/// </summary>
public static class CodexConfigSync
{
    /// <summary>The config file every sync needs; without it there is nothing to point at a gateway.</summary>
    public const string ConfigFileName = "config.toml";

    /// <summary>Suffix of a per-customer profile, as the installer names it.</summary>
    public const string ProfileSuffix = ".config.toml";

    /// <summary>Catalogue names, as <c>codexModelCatalogue.ts</c> defines them.</summary>
    public const string CataloguePrefix = "models.";
    public const string CatalogueSuffix = ".azure.json";

    /// <summary>
    /// An inline table with one or more quoted key/value pairs, which is the only shape the
    /// installer emits. Anything else is refused rather than guessed at — see
    /// <see cref="Rewrite"/>.
    /// </summary>
    private static readonly Regex EnvHeaderLine = new(
        @"^(?<indent>[ \t]*)env_http_headers[ \t]*=[ \t]*\{(?<body>[^}]*)\}[ \t]*$",
        RegexOptions.Compiled);

    private static readonly Regex HeaderPair = new(
        @"""(?<name>[^""]+)""[ \t]*=[ \t]*""(?<var>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex CatalogueLine = new(
        @"^(?<indent>[ \t]*)model_catalog_json[ \t]*=[ \t]*""(?<path>[^""]*)""[ \t]*$",
        RegexOptions.Compiled);

    private static readonly Regex BaseUrlLine = new(
        @"^[ \t]*base_url[ \t]*=[ \t]*""(?<url>[^""]*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Where this machine keeps its Codex configuration. <c>CODEX_HOME</c> wins, which is the same
    /// precedence the installer's own scripts use, so a reader who moved theirs is not told it is
    /// missing.
    /// </summary>
    public static string DefaultLocalHome()
    {
        var overridden = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>
    /// The files worth moving, in the order they should be written: catalogues and profiles first,
    /// then <c>config.toml</c>, so a box is never left pointing at a catalogue that has not arrived.
    ///
    /// Everything else in the directory is deliberately left behind. <c>auth.json</c> above all: it
    /// is an OpenAI credential, and copying it would sign the box into a ChatGPT account and clear
    /// the app's "logged out" banner for a reason that has nothing to do with whether the gateway
    /// works. <c>history.jsonl</c>, <c>sessions/</c> and <c>log/</c> are this machine's transcripts,
    /// which are not configuration and may be the most sensitive thing in the folder.
    /// </summary>
    public static IReadOnlyList<CodexSyncItem> Discover(string localHome)
    {
        if (!Directory.Exists(localHome)) return Array.Empty<CodexSyncItem>();

        var items = new List<CodexSyncItem>();
        foreach (var path in Directory.EnumerateFiles(localHome))
        {
            var name = Path.GetFileName(path);
            var kind = Classify(name);
            if (kind is null) continue;

            long bytes;
            try { bytes = new FileInfo(path).Length; } catch { bytes = 0; }
            items.Add(new CodexSyncItem(path, name, kind.Value, bytes));
        }

        // Catalogue, then profile, then config. config.toml names the others, so it lands last.
        return items
            .OrderBy(i => i.Kind switch { CodexSyncKind.Catalogue => 0, CodexSyncKind.Profile => 1, _ => 2 })
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Which kind a file name is, or null when it is not ours to move.</summary>
    public static CodexSyncKind? Classify(string name)
    {
        if (string.Equals(name, ConfigFileName, StringComparison.OrdinalIgnoreCase)) return CodexSyncKind.Config;

        // Ordered after the exact match so "config.toml" cannot be read as a profile with an empty
        // customer id.
        if (name.EndsWith(ProfileSuffix, StringComparison.OrdinalIgnoreCase)
            && name.Length > ProfileSuffix.Length)
            return CodexSyncKind.Profile;

        if (name.StartsWith(CataloguePrefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(CatalogueSuffix, StringComparison.OrdinalIgnoreCase)
            && name.Length > CataloguePrefix.Length + CatalogueSuffix.Length)
            return CodexSyncKind.Catalogue;

        return null;
    }

    /// <summary>
    /// The file as it has to land on the box.
    ///
    /// Two rewrites and one refusal:
    /// <list type="bullet">
    /// <item><c>env_http_headers</c> becomes <c>http_headers</c> with the value resolved here. Every
    /// occurrence, because a multi-customer config has a block per customer and the installer also
    /// emits one inside <c>[mcp_servers.home]</c>.</item>
    /// <item><c>model_catalog_json</c> is repointed at <paramref name="remoteCodexDir"/>. The local
    /// value is a Windows path, which on the box names nothing, and Codex then has an empty model
    /// picker and a <c>model</c> line that resolves to no model.</item>
    /// <item>An <c>env_http_headers</c> this does not understand is a <b>refusal</b>, never a
    /// pass-through. Passing one on produces a box that looks configured and answers 401 — the exact
    /// failure this whole command exists to avoid.</item>
    /// </list>
    /// </summary>
    /// <param name="text">The local file's contents.</param>
    /// <param name="lookupVariable">Resolves an environment variable name to its value, or null.</param>
    /// <param name="remoteCodexDir">The account's <c>.codex</c> directory on the box, e.g.
    /// <c>/home/codex/.codex</c>. Read from the box rather than assumed.</param>
    public static CodexRewriteResult Rewrite(string text, Func<string, string?> lookupVariable, string remoteCodexDir)
    {
        var resolved = new List<string>();
        var missing = new List<string>();
        var refusals = new List<string>();
        var warnings = new List<string>();

        // A local file may well be UTF-8 with a BOM -- PowerShell 5.1's Set-Content writes one, and
        // so does Notepad -- and Codex reads config.toml as plain UTF-8, so those three bytes land
        // inside the first key name and the file fails to parse. Stripped here rather than at write
        // time so the rule is covered by a test.
        var body = text.TrimStart('﻿').Replace("\r\n", "\n").Replace("\r", "\n");
        var remote = remoteCodexDir.TrimEnd('/');

        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            var header = EnvHeaderLine.Match(line);
            if (header.Success)
            {
                var pairs = HeaderPair.Matches(header.Groups["body"].Value);
                if (pairs.Count == 0)
                {
                    refusals.Add($"line {i + 1}: env_http_headers with no \"header\" = \"VARIABLE\" pair in it");
                    continue;
                }

                var rendered = new List<string>();
                foreach (Match pair in pairs)
                {
                    var headerName = pair.Groups["name"].Value;
                    var variable = pair.Groups["var"].Value;
                    var value = lookupVariable(variable);
                    if (string.IsNullOrEmpty(value))
                    {
                        missing.Add(variable);
                        continue;
                    }

                    resolved.Add(variable);
                    rendered.Add($"\"{headerName}\" = \"{Escape(value)}\"");
                }

                // Left as it was when a value is missing, so the fatal report below is what the
                // caller acts on rather than a half-rewritten file.
                lines[i] = rendered.Count == pairs.Count
                    ? $"{header.Groups["indent"].Value}http_headers = {{ {string.Join(", ", rendered)} }}"
                    : line;
                continue;
            }

            var catalogue = CatalogueLine.Match(line);
            if (catalogue.Success)
            {
                var name = LastSegment(catalogue.Groups["path"].Value);
                lines[i] = string.IsNullOrEmpty(name)
                    ? line
                    : $"{catalogue.Groups["indent"].Value}model_catalog_json = \"{remote}/{name}\"";
                if (string.IsNullOrEmpty(name))
                    refusals.Add($"line {i + 1}: model_catalog_json names no file");
                continue;
            }

            var baseUrl = BaseUrlLine.Match(line);
            if (baseUrl.Success && IsLoopback(baseUrl.Groups["url"].Value))
            {
                warnings.Add($"base_url on line {i + 1} points at this machine's loopback "
                           + $"({baseUrl.Groups["url"].Value}) — the box cannot reach it");
            }
        }

        var output = string.Join('\n', lines);

        // The guarantee the whole command rests on. Anything still naming a variable would be a
        // silent 401 on the box, so it is better to refuse than to send it.
        if (output.Contains("env_http_headers", StringComparison.Ordinal))
        {
            refusals.Add("env_http_headers survived the rewrite — a variable will be empty for the "
                       + "app server over ssh, so the gateway would answer 401");
        }

        return new CodexRewriteResult(output, resolved, missing.Distinct().ToList(), refusals, warnings);
    }

    /// <summary>Bytes to send: LF already, and explicitly no BOM.</summary>
    public static byte[] ToBytes(string text) => new UTF8Encoding(false).GetBytes(text);

    /// <summary>The file name out of a path that may use either separator, because the local value
    /// was written on Windows and this may be running anywhere.</summary>
    private static string LastSegment(string path)
    {
        var at = path.LastIndexOfAny(new[] { '/', '\\' });
        return (at < 0 ? path : path[(at + 1)..]).Trim();
    }

    /// <summary>A TOML basic string: the value is a credential, so it is escaped rather than
    /// trusted to contain nothing interesting.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback) return true;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
