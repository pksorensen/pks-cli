using System.Text;
using PKS.Infrastructure.Services.Agent.Setup;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// Turns one ASP profile into Codex files, and takes them away again.
///
/// <para>
/// <b>What goes where, and why it is not a free choice</b> (all measured on codex-cli 0.163.0):
/// </para>
/// <list type="bullet">
/// <item><b>MCP servers → the layer file.</b> <c>-p</c> merges tables, so a layer can carry a
/// working context and not just a model.</item>
/// <item><b>Marketplaces → <c>config.toml</c>, always.</b> <c>codex plugin</c> refuses <c>-p</c>
/// outright — it names the runtime commands that accept it and <c>plugin</c> is not among them —
/// so a marketplace in a layer file is a marketplace Codex will never read.</item>
/// <item><b>Model keys → wherever the profile is being written.</b> In the layer for a
/// <c>-p</c> profile, in <c>config.toml</c> for the active one.</item>
/// </list>
///
/// <para>
/// <b>Never a credential value.</b> It goes into an environment variable and nowhere else. Only
/// the variable's name is written into a file or printed.
/// </para>
/// </summary>
public static class CodexProfileApplier
{
    /// <summary>The layer file for a namespace: <c>$CODEX_HOME/&lt;ns&gt;.config.toml</c>.</summary>
    public static string LayerFileName(string ns) => $"{ns}.config.toml";

    /// <summary><c>&lt;source&gt;-&lt;profile&gt;</c>, the name given to <c>codex -p</c>.</summary>
    public static string NamespaceFor(string sourceId, string profileId) => $"{sourceId}-{profileId}";

    /// <summary>
    /// The environment variable a profile's credential lives in.
    ///
    /// <para>
    /// Always suffixed with the source, never bare. Two platforms can publish the same
    /// <c>env</c> base name — Home and a self-hosted copy of ours both would — and one
    /// overwriting the other is a 401 against whichever was connected first.
    /// </para>
    /// </summary>
    public static string EnvVarFor(string baseName, string sourceId) =>
        $"{baseName}_{Sanitize(sourceId)}";

    private static string Sanitize(string s) =>
        new(s.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>
    /// The TOML a profile contributes to a layer file: its MCP servers and its model keys.
    /// </summary>
    public static string BuildLayerToml(AspProfile profile, string sourceId, out List<string> tableNames)
    {
        tableNames = [];
        var sb = new StringBuilder();

        sb.Append("# Written by `pks codex connect`. Select it with `codex -p ")
          .Append(NamespaceFor(sourceId, profile.Id))
          .Append("`.\n# Edits here are replaced on the next connect; edit the profile on the platform instead.\n");

        // Model keys only when the profile actually names a model. A `model` with no provider is
        // fine; `model_provider` without a matching table is NOT — it fails the whole config load,
        // every invocation, not just one thread. See §3b of the spec.
        if (profile.Provider is { } provider && !string.IsNullOrEmpty(provider.BaseUrl))
        {
            var providerTable = $"model_providers.{NamespaceFor(sourceId, profile.Id)}";
            sb.Append("\nmodel_provider = \"").Append(NamespaceFor(sourceId, profile.Id)).Append("\"\n");
            if (!string.IsNullOrEmpty(profile.Model?.Default))
                sb.Append("model = \"").Append(profile.Model.Default).Append("\"\n");
            if (!string.IsNullOrEmpty(profile.Model?.ReasoningEffort))
                sb.Append("model_reasoning_effort = \"").Append(profile.Model.ReasoningEffort).Append("\"\n");

            sb.Append('\n').Append(BuildProviderTable(providerTable, provider, sourceId)).Append('\n');
            tableNames.Add(providerTable);
        }
        else
        {
            // No provider: the reader keeps their own model login. Write `model` only if the
            // platform named one, and never `model_provider`.
            if (!string.IsNullOrEmpty(profile.Model?.Default))
                sb.Append("\nmodel = \"").Append(profile.Model.Default).Append("\"\n");
            if (!string.IsNullOrEmpty(profile.Model?.ReasoningEffort))
                sb.Append("model_reasoning_effort = \"").Append(profile.Model.ReasoningEffort).Append("\"\n");
        }

        foreach (var server in profile.McpServers ?? [])
        {
            var table = $"mcp_servers.{server.Name}";
            sb.Append('\n').Append(BuildMcpTable(table, server)).Append('\n');
            tableNames.Add(table);
        }

        return sb.ToString();
    }

    private static string BuildProviderTable(string tableName, AspProvider provider, string sourceId)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(tableName).Append("]\n");
        sb.Append("name = \"").Append(Escape(provider.Name ?? tableName)).Append("\"\n");
        sb.Append("base_url = \"").Append(Escape(provider.BaseUrl!)).Append("\"\n");

        // `responses` or nothing. Codex speaks the Responses API; pointed at a
        // chat-completions endpoint it fails with "The requested operation is unsupported" and
        // no hint as to which setting caused it.
        sb.Append("wire_api = \"").Append(Escape(provider.WireApi ?? "responses")).Append("\"\n");

        var auth = provider.Auth;
        if (auth is not null && !string.IsNullOrEmpty(auth.Env))
        {
            var envVar = EnvVarFor(auth.Env, sourceId);

            if (string.Equals(auth.Kind, "header", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(auth.Header))
            {
                // env_http_headers, not env_key. Azure API Management matches a subscription key
                // in a named header and cannot match one behind a scheme prefix, which is what
                // env_key emits — so env_key against APIM 401s on every call.
                sb.Append("env_http_headers = { \"").Append(Escape(auth.Header))
                  .Append("\" = \"").Append(envVar).Append("\" }\n");
            }
            else
            {
                sb.Append("env_key = \"").Append(envVar).Append("\"\n");
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string BuildMcpTable(string tableName, AspMcpServer server)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(tableName).Append("]\n");

        if (!string.IsNullOrEmpty(server.Url))
        {
            // Streamable HTTP. Codex performs its own OAuth per server, so no credential is
            // written here — `codex mcp login <name>` is what completes it.
            sb.Append("url = \"").Append(Escape(server.Url)).Append("\"\n");
        }
        else if (!string.IsNullOrEmpty(server.Command))
        {
            sb.Append("command = \"").Append(Escape(server.Command)).Append("\"\n");
            if (server.Args is { Count: > 0 })
                sb.Append("args = [").Append(string.Join(", ", server.Args.Select(a => $"\"{Escape(a)}\""))).Append("]\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The marketplace tables for a profile, which must go in <c>config.toml</c>.
    ///
    /// <para>
    /// <b>Each one is verified before it is written</b>, because Codex loads marketplaces eagerly
    /// and a single bad one is fatal to every <c>codex plugin</c> command — including the ones for
    /// the marketplaces that are fine: <c>failed to load marketplace(s): … marketplace root does
    /// not contain a supported manifest</c>. The platform cannot know whether a URL resolves from
    /// the reader's machine, so the client checks rather than trusting.
    /// </para>
    /// </summary>
    public static string BuildMarketplaceToml(AspMarketplace marketplace, string sourceId, out string tableName)
    {
        tableName = $"marketplaces.{marketplace.Name}";

        return $"""
            [{tableName}]
            source_type = "git"
            source = "{Escape(marketplace.Url ?? string.Empty)}"
            # from {sourceId}, via `pks codex connect`
            """;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// Removes every table a ledger entry claims, from whichever file holds it.
    /// By name, so a table Codex has relocated is still found.
    /// </summary>
    public static string RemoveOwned(string? content, IEnumerable<string> tableNames)
    {
        var text = content ?? string.Empty;
        foreach (var name in tableNames)
            text = CodexTomlEditor.RemoveTable(text, name);

        return text;
    }
}
