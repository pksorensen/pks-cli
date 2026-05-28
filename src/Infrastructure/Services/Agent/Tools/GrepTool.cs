using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class GrepTool : IAgentTool
{
    private const int MaxLines = 2000;
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "pattern": { "type": "string" },
        "path": { "type": "string" },
        "glob": { "type": "string" }
      },
      "required": ["pattern"]
    }
    """).RootElement;

    public GrepTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "grep",
        "Search files for a regex pattern using ripgrep.",
        SchemaElement);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("pattern", out var patProp) || patProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("missing required string property: pattern");
        var pattern = patProp.GetString()!;

        string searchPath = _cwd;
        if (arguments.TryGetProperty("path", out var pProp) && pProp.ValueKind == JsonValueKind.String)
        {
            try { searchPath = PathSandbox.Resolve(_cwd, pProp.GetString()!); }
            catch (Exception ex) { return ToolResult.Error(ex.Message); }
        }

        string? glob = null;
        if (arguments.TryGetProperty("glob", out var gProp) && gProp.ValueKind == JsonValueKind.String)
            glob = gProp.GetString();

        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _cwd,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--line-number");
        psi.ArgumentList.Add("--no-heading");
        psi.ArgumentList.Add("--color=never");
        psi.ArgumentList.Add(pattern);
        psi.ArgumentList.Add(searchPath);
        if (!string.IsNullOrEmpty(glob))
        {
            psi.ArgumentList.Add("-g");
            psi.ArgumentList.Add(glob);
        }

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex) { return ToolResult.Error($"failed to start rg: {ex.Message}"); }

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 1)
            return ToolResult.Success("(no matches)");
        if (process.ExitCode != 0)
            return ToolResult.Error($"rg exit {process.ExitCode}\n{stderr}");

        var lines = stdout.Split('\n');
        var sb = new StringBuilder();
        int emitted = 0;
        bool truncated = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == lines.Length - 1 && lines[i].Length == 0) break;
            if (emitted >= MaxLines) { truncated = true; break; }
            sb.Append(lines[i]);
            sb.Append('\n');
            emitted++;
        }
        if (truncated) sb.Append("[... truncated]\n");
        return ToolResult.Success(sb.ToString());
    }
}
