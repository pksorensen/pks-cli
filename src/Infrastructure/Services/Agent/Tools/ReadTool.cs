using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class ReadTool : IAgentTool
{
    private const int MaxBytes = 256 * 1024;
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string" },
        "offset": { "type": "integer" },
        "limit": { "type": "integer" }
      },
      "required": ["path"]
    }
    """).RootElement;

    public ReadTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "read",
        "Read a file from the sandbox. Returns cat -n style output. Supports offset (1-based) and limit.",
        SchemaElement);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(ToolResult.Error("missing required string property: path"));
        }
        var requested = pathProp.GetString()!;
        int offset = 1;
        int limit = 2000;
        if (arguments.TryGetProperty("offset", out var oProp) && oProp.ValueKind == JsonValueKind.Number)
            offset = oProp.GetInt32();
        if (arguments.TryGetProperty("limit", out var lProp) && lProp.ValueKind == JsonValueKind.Number)
            limit = lProp.GetInt32();
        if (offset < 1) offset = 1;
        if (limit < 1) limit = 1;

        string full;
        try { full = PathSandbox.Resolve(_cwd, requested); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }

        if (!File.Exists(full))
        {
            return Task.FromResult(ToolResult.Error($"file not found: {requested}"));
        }

        var raw = File.ReadAllBytes(full);
        var totalBytes = raw.Length;
        var truncatedBytes = totalBytes > MaxBytes;
        var slice = truncatedBytes ? new ArraySegment<byte>(raw, 0, MaxBytes) : new ArraySegment<byte>(raw);
        var text = Encoding.UTF8.GetString(slice.Array!, slice.Offset, slice.Count);

        var lines = text.Split('\n');
        var sb = new StringBuilder();
        int start = offset - 1;
        int end = Math.Min(lines.Length, start + limit);
        for (int i = start; i < end; i++)
        {
            int lineNo = i + 1;
            sb.Append(lineNo.ToString().PadLeft(6));
            sb.Append('\t');
            sb.Append(lines[i]);
            sb.Append('\n');
        }
        if (truncatedBytes)
        {
            sb.Append($"\n[... truncated, {totalBytes} bytes total]");
        }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
