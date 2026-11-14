using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>
/// One stdio MCP server the runner HOST allows an <c>mcp_task</c> job to use. Lives only in the
/// host allowlist file -- the server (e.g. famai.dk) sends server NAMES and can never supply a
/// command, args or env. See customers/famai/docs/mcp-task-protocol.md.
/// </summary>
public sealed class McpTaskServerConfig
{
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("args")] public List<string>? Args { get; set; }
    [JsonPropertyName("env")] public Dictionary<string, string>? Env { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }

    /// <summary>When present, ONLY these MCP tool names are exposed (denyTools is then ignored).</summary>
    [JsonPropertyName("allowTools")] public List<string>? AllowTools { get; set; }

    /// <summary>Used only when allowTools is absent: these MCP tool names are hidden.</summary>
    [JsonPropertyName("denyTools")] public List<string>? DenyTools { get; set; }

    /// <summary>Allow/deny on the ORIGINAL MCP tool name (e.g. <c>aula.contacts.list</c>).</summary>
    public bool IsToolAllowed(string toolName)
    {
        if (AllowTools != null) return AllowTools.Contains(toolName, StringComparer.Ordinal);
        if (DenyTools != null) return !DenyTools.Contains(toolName, StringComparer.Ordinal);
        return true;
    }
}

/// <summary>The host allowlist file: <c>{ "servers": { "&lt;name&gt;": { command, args, env, allowTools|denyTools } } }</c>.</summary>
public sealed class McpTaskConfig
{
    public const string EnvVar = "PKS_MCP_TASK_CONFIG";
    public const string TaskCapability = "mcp-task:v1";
    public const string ServerCapabilityPrefix = "mcp-server:";

    [JsonPropertyName("servers")]
    public Dictionary<string, McpTaskServerConfig> Servers { get; set; } = null!; // null after parse => "servers" missing

    /// <summary>Default location: <c>~/.config/pks/mcp-task-servers.json</c>.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "pks", "mcp-task-servers.json");

    /// <summary>Precedence: <c>--mcp-config</c>, then <c>PKS_MCP_TASK_CONFIG</c>, then <paramref name="defaultPath"/>
    /// (or <see cref="DefaultPath"/>). Injectable pieces so tests never see a real ~/.config file.</summary>
    public static string ResolvePath(string? cliPath, Func<string, string?>? getEnv = null, string? defaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(cliPath)) return ExpandHome(cliPath);
        var env = (getEnv ?? Environment.GetEnvironmentVariable)(EnvVar);
        if (!string.IsNullOrWhiteSpace(env)) return ExpandHome(env);
        return defaultPath ?? DefaultPath;
    }

    private static string ExpandHome(string p) =>
        p.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[2..])
            : p;

    /// <summary>Parses the allowlist JSON. Returns null + error on anything malformed; never throws.</summary>
    public static McpTaskConfig? Parse(string json, out string? error)
    {
        error = null;
        McpTaskConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<McpTaskConfig>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return null;
        }

        if (cfg?.Servers == null)
        {
            error = "missing \"servers\" object";
            return null;
        }

        foreach (var (name, server) in cfg.Servers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(c => char.IsWhiteSpace(c) || c == ','))
            {
                error = $"invalid server name '{name}'";
                return null;
            }
            if (server == null || string.IsNullOrWhiteSpace(server.Command))
            {
                error = $"server '{name}' has no \"command\"";
                return null;
            }
        }

        // Re-key with an ordinal comparer (the deserializer builds a default dictionary).
        cfg.Servers = new Dictionary<string, McpTaskServerConfig>(cfg.Servers, StringComparer.Ordinal);
        return cfg;
    }

    /// <summary>Loads the file at <paramref name="path"/>. Missing file => null with error = null
    /// (the ordinary case on a runner that serves no mcp_task jobs). Never throws.</summary>
    public static McpTaskConfig? TryLoad(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path), out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary><c>mcp-task:v1</c> plus <c>mcp-server:&lt;name&gt;</c> per configured server, sorted for a stable poll payload.</summary>
    public IEnumerable<string> Capabilities()
    {
        yield return TaskCapability;
        foreach (var name in Servers.Keys.OrderBy(k => k, StringComparer.Ordinal))
            yield return ServerCapabilityPrefix + name;
    }
}
