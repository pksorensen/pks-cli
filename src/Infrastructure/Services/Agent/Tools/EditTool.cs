using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class EditTool : IAgentTool
{
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "path": { "type": "string" },
        "old_string": { "type": "string" },
        "new_string": { "type": "string" },
        "replace_all": { "type": "boolean" }
      },
      "required": ["path", "old_string", "new_string"]
    }
    """).RootElement;

    public EditTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "edit",
        "Replace text in a file. Must match exactly once unless replace_all is true.",
        SchemaElement);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("path", out var pathProp) || pathProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: path"));
        if (!arguments.TryGetProperty("old_string", out var oldProp) || oldProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: old_string"));
        if (!arguments.TryGetProperty("new_string", out var newProp) || newProp.ValueKind != JsonValueKind.String)
            return Task.FromResult(ToolResult.Error("missing required string property: new_string"));

        var requested = pathProp.GetString()!;
        var oldStr = oldProp.GetString()!;
        var newStr = newProp.GetString()!;
        bool replaceAll = false;
        if (arguments.TryGetProperty("replace_all", out var raProp) && raProp.ValueKind == JsonValueKind.True)
            replaceAll = true;

        string full;
        try { full = PathSandbox.Resolve(_cwd, requested); }
        catch (Exception ex) { return Task.FromResult(ToolResult.Error(ex.Message)); }

        if (!File.Exists(full))
            return Task.FromResult(ToolResult.Error($"file not found: {requested}"));

        var content = File.ReadAllText(full, Encoding.UTF8);

        int count = 0;
        if (oldStr.Length > 0)
        {
            int idx = 0;
            while ((idx = content.IndexOf(oldStr, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += oldStr.Length;
            }
        }

        if (replaceAll)
        {
            var updated = oldStr.Length == 0 ? content : content.Replace(oldStr, newStr);
            File.WriteAllText(full, updated, new UTF8Encoding(false));
            return Task.FromResult(ToolResult.Success($"replaced {count} occurrences in {requested}"));
        }

        if (count == 0)
            return Task.FromResult(ToolResult.Error("old_string not found"));
        if (count > 1)
            return Task.FromResult(ToolResult.Error($"old_string not unique ({count} matches) — pass replace_all=true or include more context"));

        var firstIdx = content.IndexOf(oldStr, StringComparison.Ordinal);
        var result = content.Substring(0, firstIdx) + newStr + content.Substring(firstIdx + oldStr.Length);
        File.WriteAllText(full, result, new UTF8Encoding(false));
        return Task.FromResult(ToolResult.Success($"edited {requested}"));
    }
}
