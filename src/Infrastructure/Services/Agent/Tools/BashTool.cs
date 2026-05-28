using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Tools;

public sealed class BashTool : IAgentTool
{
    private readonly string _cwd;

    private static readonly JsonElement SchemaElement = JsonDocument.Parse("""
    {
      "type": "object",
      "properties": {
        "command": { "type": "string" },
        "timeout": { "type": "integer" }
      },
      "required": ["command"]
    }
    """).RootElement;

    public BashTool(string cwd) { _cwd = cwd; }

    public ChatToolDefinition Definition { get; } = new(
        "bash",
        "Execute a shell command in the sandbox cwd. Combined stdout+stderr.",
        SchemaElement);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("command", out var cmdProp) || cmdProp.ValueKind != JsonValueKind.String)
            return ToolResult.Error("missing required string property: command");
        var command = cmdProp.GetString()!;
        int timeoutSec = 30;
        if (arguments.TryGetProperty("timeout", out var tProp) && tProp.ValueKind == JsonValueKind.Number)
            timeoutSec = tProp.GetInt32();

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _cwd,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var sbLock = new object();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (sbLock) sb.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (sbLock) sb.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"failed to start process: {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            string partial;
            lock (sbLock) partial = sb.ToString();
            if (timeoutCts.IsCancellationRequested)
                return ToolResult.Error($"timed out after {timeoutSec}s\n{partial}");
            return ToolResult.Error($"cancelled\n{partial}");
        }

        string output;
        lock (sbLock) output = sb.ToString();
        return ToolResult.Success($"exit {process.ExitCode}\n{output}");
    }
}
