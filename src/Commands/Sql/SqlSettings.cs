using System.Text.Json;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;

namespace PKS.Commands.Sql;

public class SqlSettings : CommandSettings { }

/// <summary>
/// Everything the SQL commands share: where the login is kept, which resource it is for,
/// and how a short server name becomes a host name.
/// </summary>
public static class SqlAuth
{
    /// <summary>
    /// The SQL login lives under its own key. `pks azure init` stores a subscription login for one
    /// tenant; the database you need to query is often in another, and one login must not evict the
    /// other.
    /// </summary>
    public const string StorageKey = "azure.sql.credentials";

    /// <summary>Where `pks sqlserver init` remembers the server and database it selected.</summary>
    public const string DefaultsKey = "azure.sql.defaults";

    /// <summary>
    /// The resource id for Azure SQL carries a trailing slash, so the v2 scope ends up with two —
    /// this is the exact form Microsoft.Data.SqlClient itself requests, and the one to copy.
    /// </summary>
    public const string Scope = "https://database.windows.net//.default offline_access";

    /// <summary>
    /// What the sign-in itself asks for. It is the management scope, not the database one, because
    /// the same refresh token is later exchanged for either — and asking for management up front is
    /// what lets init list your servers instead of making you type a host name from memory.
    /// </summary>
    public const string LoginScope = "https://management.azure.com/.default offline_access";

    /// <summary>Accepts both "sql-mc-weu-prd" and the full host name.</summary>
    public static string ResolveServer(string server)
    {
        var value = server.Trim();
        if (value.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            value = value[4..];
        var host = value.Split(',')[0];
        return host.Contains('.') ? value : $"{value}.database.windows.net";
    }
}

/// <summary>The server and database `pks sql` falls back to when you don't name one.</summary>
public class SqlDefaults
{
    public string Server { get; set; } = string.Empty;
    public string Database { get; set; } = string.Empty;

    public static async Task<SqlDefaults?> LoadAsync(IConfigurationService configuration)
    {
        try
        {
            var json = await configuration.GetAsync(SqlAuth.DefaultsKey);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<SqlDefaults>(json);
        }
        catch
        {
            return null;
        }
    }

    public static Task SaveAsync(IConfigurationService configuration, SqlDefaults defaults) =>
        configuration.SetAsync(SqlAuth.DefaultsKey, JsonSerializer.Serialize(defaults), global: true);
}

/// <summary>Reads a claim out of a JWT without validating it — it is our own token, freshly minted.</summary>
public static class JwtClaims
{
    public static string? Read(string jwt, string claim)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty(claim, out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
