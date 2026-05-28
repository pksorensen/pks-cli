using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class FindTool : IAgentTool
{
    private const int MaxEntries = 2000;
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "pattern": { "type": "string" },
        "path": { "type": "string" },
        "type": { "type": "string", "enum": ["f", "d"] }
      },
      "required": ["pattern"]
    }
    """).RootElement;

    public FindTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "find",
        "Find files/directories matching a glob pattern, recursively.",
        SchemaElement);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("pattern", out var patProp) || patProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: pattern"));
        var pattern = patProp.GetString()!;

        string searchPath = _cwd;
        if (arguments.TryGetProperty("path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
        {
            try { searchPath = PathSandbox.Resolve(_cwd, pProp.GetString()!); }
            catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }
        }

        string? typeFilter = null;
        if (arguments.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String)
            typeFilter = tProp.GetString();

        if (!Directory.Exists(searchPath))
            return Task.FromResult(ToolResult.Error($"directory not found: {searchPath}"));

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(searchPath, pattern, SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"find failed: {ex.Message}"));
        }

        var results = new List<string>();
        foreach (var e in entries)
        {
            if (typeFilter == "f" && !File.Exists(e)) continue;
            if (typeFilter == "d" && !Directory.Exists(e)) continue;
            results.Add(e);
        }
        results.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        bool truncated = false;
        for (int i = 0; i < results.Count; i++)
        {
            if (i >= MaxEntries) { truncated = true; break; }
            sb.Append(results[i]);
            sb.Append('\n');
        }
        if (truncated) sb.Append("[... truncated]\n");
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
