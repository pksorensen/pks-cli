using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class WriteTool : IAgentTool
{
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string" },
        "content": { "type": "string" }
      },
      "required": ["path", "content"]
    }
    """).RootElement;

    public WriteTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "write",
        "Write content to a file (overwrite). Creates parent directories as needed.",
        SchemaElement);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: path"));
        if (!arguments.TryGetProperty("content", out var contentProp) || contentProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: content"));

        var requested = pathProp.GetString()!;
        var content = contentProp.GetString()!;

        string full;
        try { full = PathSandbox.Resolve(_cwd, requested); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(full, bytes);

        string rel;
        try
        {
            var cwdFull = Path.GetFullPath(_cwd);
            rel = Path.GetRelativePath(cwdFull, full);
        }
        catch
        {
            rel = full;
        }
        return Task.FromResult(ToolResult.Success($"wrote {bytes.Length} bytes to {rel}"));
    }
}
