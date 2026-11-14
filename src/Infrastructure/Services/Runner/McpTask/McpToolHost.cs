using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>A tool one connected MCP server offers (original name, as reported by tools/list).</summary>
public sealed record McpServerTool(string Name, string? Description, JsonElement InputSchema);

/// <summary>A live connection to one MCP server. Disposing it terminates the server process.</summary>
public interface IMcpServerSession : IAsyncDisposable
{
    string ServerName { get; }
    Task<IReadOnlyList<McpServerTool>> ListToolsAsync(CancellationToken ct);
    /// <summary>Returns the tool result flattened to text and whether the server flagged it as an error.</summary>
    Task<(string Text, bool IsError)> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct);
}

public interface IMcpServerLauncher
{
    Task<IMcpServerSession> StartAsync(string serverName, McpTaskServerConfig config, CancellationToken ct);
}

/// <summary>Spawns stdio servers with the MCP C# SDK (ModelContextProtocol 0.3.0-preview.3).</summary>
public sealed class StdioMcpServerLauncher : IMcpServerLauncher
{
    /// <summary>Runner secrets a spawned server has no business seeing. The host allowlist can
    /// still pass a value explicitly through its own <c>env</c> block.</summary>
    internal static readonly string[] StrippedEnvVars =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
        "AGENTICS_REGISTRATION_TOKEN", "AGENTICS_RUNNER_TOKEN", "GH_TOKEN", "GITHUB_TOKEN",
    ];

    public async Task<IMcpServerSession> StartAsync(string serverName, McpTaskServerConfig config, CancellationToken ct)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var k in StrippedEnvVars) env[k] = null;
        if (config.Env != null)
            foreach (var (k, v) in config.Env) env[k] = v;

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = serverName,
            Command = config.Command,
            Arguments = config.Args ?? new List<string>(),
            EnvironmentVariables = env,
            WorkingDirectory = config.Cwd,
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });
        var client = await McpClientFactory.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "pks-cli-mcp-task", Version = "1.0.0" },
        }, cancellationToken: ct);
        return new Session(serverName, client);
    }

    private sealed class Session(string serverName, IMcpClient client) : IMcpServerSession
    {
        public string ServerName => serverName;

        public async Task<IReadOnlyList<McpServerTool>> ListToolsAsync(CancellationToken ct)
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => new McpServerTool(t.Name, t.Description, t.JsonSchema)).ToList();
        }

        public async Task<(string Text, bool IsError)> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
        {
            var res = await client.CallToolAsync(toolName, args, cancellationToken: ct);
            var sb = new StringBuilder();
            foreach (var block in res.Content)
            {
                if (sb.Length > 0) sb.Append('\n');
                switch (block)
                {
                    case TextContentBlock t: sb.Append(t.Text); break;
                    case EmbeddedResourceBlock { Resource: TextResourceContents r }: sb.Append(r.Text); break;
                    default: sb.Append($"[{block.Type} content omitted]"); break;
                }
            }
            if (sb.Length == 0 && res.StructuredContent is { } sc)
                sb.Append(sc.ToJsonString());
            return (sb.ToString(), res.IsError == true);
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
