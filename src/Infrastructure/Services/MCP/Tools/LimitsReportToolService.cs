using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service backing the `pks claude limits --llm` fallback path. When the
/// deterministic `/usage` panel parser fails (or `--llm` is forced), `pks claude limits`
/// spawns claude with `--mcp-config` pointed at `pks mcp --transport stdio` and pastes the
/// raw captured panel back as a prompt, asking the model to call
/// <see cref="ReportSessionLimits"/> exactly once with the numbers it read. The tool writes
/// the payload to the file named by the <c>PKS_LIMITS_SINK</c> environment variable (set in
/// the MCP server config's <c>env</c> block so it travels to the `pks mcp` child process
/// spawned by claude), where the CLI command reads it back and resolves the reset strings
/// itself via <see cref="PKS.Commands.Claude.UsagePanelParser.ResolveReset"/> — the model
/// never does date math.
/// </summary>
[McpServerToolType]
public class LimitsReportToolService
{
    private readonly ILogger<LimitsReportToolService> _logger;

    public LimitsReportToolService(ILogger<LimitsReportToolService> logger)
    {
        _logger = logger;
    }

    [McpServerTool]
    [Description("Report parsed Claude Code session/week usage limits back to the pks CLI. Call this exactly once with the numbers you read from the /usage panel.")]
    public object ReportSessionLimits(
        [Description("Current session usage percent (0-100).")] int sessionUsedPct,
        [Description("Current session reset, e.g. '7:19pm' (time-of-day, UTC).")] string sessionResetsAt,
        [Description("Current week (all models) usage percent (0-100).")] int weekUsedPct,
        [Description("Current week reset, e.g. 'Jul 20, 3:59am' (UTC).")] string weekResetsAt,
        [Description("Optional per-model week usage as a JSON array of {model,usedPct,resetsAt}.")] string? weekByModelJson = null)
    {
        var sink = Environment.GetEnvironmentVariable("PKS_LIMITS_SINK");
        if (string.IsNullOrEmpty(sink))
        {
            _logger.LogWarning("report_session_limits called but PKS_LIMITS_SINK is not set");
            return new { success = false, error = "PKS_LIMITS_SINK not set" };
        }

        var payload = new
        {
            sessionUsedPct,
            sessionResetsAt,
            weekUsedPct,
            weekResetsAt,
            weekByModelJson,
            receivedAt = DateTime.UtcNow
        };

        File.WriteAllText(sink, JsonSerializer.Serialize(payload));
        _logger.LogInformation("report_session_limits wrote payload to {Sink}", sink);
        return new { success = true };
    }
}
