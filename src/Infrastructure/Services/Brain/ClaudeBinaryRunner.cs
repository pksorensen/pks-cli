using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

public sealed class ClaudeBinaryRunner : IClaudeRunner
{
    private const string ClaudeBinary = "claude";

    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = ClaudeBinary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Route this worker through Azure AI Foundry when requested (--foundry): sets
        // CLAUDE_CODE_USE_FOUNDRY + the MSI token endpoint + tier model deployments.
        request.Foundry?.Apply(psi.Environment);
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--no-session-persistence");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--system-prompt");
        psi.ArgumentList.Add(request.SystemPrompt);
        psi.ArgumentList.Add("--disable-slash-commands");
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        // Extract workers need no tools — only read JSON, write a markdown summary.
        // --strict-mcp-config makes claude ignore the project's .mcp.json so each worker
        // does NOT spin up MCP servers (e.g. the aspire server, which leaks hung
        // `nuget search` processes — one per worker, thousands over a full extract run).
        psi.ArgumentList.Add("--strict-mcp-config");
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(request.Model);
        }
        if (request.MaxBudgetUsd is { } cap && cap > 0)
        {
            psi.ArgumentList.Add("--max-budget-usd");
            psi.ArgumentList.Add(cap.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return Fail("notfound", "`claude` CLI not found in PATH. Install Claude Code: https://claude.com/claude-code", 127, sw.Elapsed);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardInput.WriteAsync(request.UserPrompt.AsMemory(), ct);
        }
        catch (IOException) { /* surfaced via stderr below */ }
        finally
        {
            try { proc.StandardInput.Close(); } catch { /* ignore */ }
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return Fail("timeout", $"timed out after {request.Timeout.TotalSeconds:0.#}s", -1, sw.Elapsed, stdout.ToString(), stderr.ToString());
        }

        var rawStdout = stdout.ToString();
        var rawStderr = stderr.ToString();

        if (proc.ExitCode != 0)
        {
            return Fail("exit", rawStderr.Trim().Length == 0 ? $"exit {proc.ExitCode}" : rawStderr.Trim(),
                proc.ExitCode, sw.Elapsed, rawStdout, rawStderr);
        }

        return ParseClaudeJson(rawStdout, rawStderr, proc.ExitCode, sw.Elapsed);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ClaudeRunResult ParseClaudeJson(string stdout, string stderr, int exitCode, TimeSpan elapsed)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return Fail("parse", "claude returned empty stdout", exitCode, elapsed, stdout, stderr);
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(trimmed);
        }
        catch (JsonException ex)
        {
            return Fail("parse", $"could not parse claude JSON: {ex.Message}", exitCode, elapsed, stdout, stderr);
        }

        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        var result = root.TryGetProperty("result", out var rp) && rp.ValueKind == JsonValueKind.String
            ? rp.GetString() ?? string.Empty
            : string.Empty;

        double cost = 0;
        if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number)
            cost = c.GetDouble();

        long inTok = 0, outTok = 0, cacheRead = 0, cacheCreate = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inTok      = GetLong(usage, "input_tokens");
            outTok     = GetLong(usage, "output_tokens");
            cacheRead  = GetLong(usage, "cache_read_input_tokens");
            cacheCreate = GetLong(usage, "cache_creation_input_tokens");
        }

        string? model = null;
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in mu.EnumerateObject())
            {
                model = p.Name;
                break;
            }
        }

        var success = !isError && result.Length > 0;
        return new ClaudeRunResult
        {
            Success = success,
            ResponseText = result,
            RawStdout = stdout,
            Stderr = stderr,
            ExitCode = exitCode,
            Duration = elapsed,
            Model = model,
            InputTokens = inTok,
            OutputTokens = outTok,
            CacheReadInputTokens = cacheRead,
            CacheCreationInputTokens = cacheCreate,
            CostUsd = cost,
            ErrorKind = success ? null : (isError ? "is_error" : "empty"),
        };
    }

    private static ClaudeRunResult Fail(string kind, string stderr, int exitCode, TimeSpan elapsed,
        string rawStdout = "", string rawStderr = "")
    {
        return new ClaudeRunResult
        {
            Success = false,
            ResponseText = string.Empty,
            RawStdout = rawStdout,
            Stderr = string.IsNullOrEmpty(rawStderr) ? stderr : rawStderr,
            ExitCode = exitCode,
            Duration = elapsed,
            ErrorKind = kind,
        };
    }

    private static long GetLong(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    }
}
