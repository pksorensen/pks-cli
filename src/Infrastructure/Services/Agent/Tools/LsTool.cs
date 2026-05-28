using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class LsTool : IAgentTool
{
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string" }
      },
      "required": ["path"]
    }
    """).RootElement;

    public LsTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "ls",
        "List entries in a directory. Directories first (with trailing /), then files, each alphabetical.",
        SchemaElement);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: path"));

        var requested = pathProp.GetString()!;
        string full;
        try { full = PathSandbox.Resolve(_cwd, requested); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }

        if (!Directory.Exists(full))
            return Task.FromResult(ToolResult.Error($"directory not found: {requested}"));

        var dirs = new List<string>();
        var files = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(full))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry)) dirs.Add(name);
            else files.Add(name);
        }
        dirs.Sort(StringComparer.Ordinal);
        files.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var d in dirs) { sb.Append(d); sb.Append('/'); sb.Append('\n'); }
        foreach (var f in files) { sb.Append(f); sb.Append('\n'); }
        return Task.FromResult(ToolResult.Success(sb.ToString()));
    }
}
