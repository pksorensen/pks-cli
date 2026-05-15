namespace PKS.Infrastructure.Services.Brain;

/// Thin wrapper around the local `claude` CLI in headless mode
/// (`claude --print --bare --no-session-persistence ...`).
/// Used by `pks brain extract` so the user's existing Claude billing/auth
/// is honored without duplicating the API client.
public interface IClaudeRunner
{
    Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct = default);
}

public sealed class ClaudeRunRequest
{
    /// User-message body (the prompt that gets sent to Claude).
    public required string UserPrompt { get; init; }

    /// System-prompt body (typically the brain-extract SKILL.md content).
    public required string SystemPrompt { get; init; }

    /// "sonnet" / "opus" / "haiku" / explicit model id. Null = use Claude's default.
    public string? Model { get; init; }

    /// Hard dollar cap per invocation passed through to `claude --max-budget-usd`.
    public double? MaxBudgetUsd { get; init; }

    /// Hard wall-clock timeout. Defaults to 5 minutes.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class ClaudeRunResult
{
    public required bool Success { get; init; }
    /// The actual assistant response body. Empty when claude failed or the
    /// JSON output couldn't be parsed.
    public required string ResponseText { get; init; }
    /// Raw stdout from the process (the single-line JSON blob in success cases).
    public required string RawStdout { get; init; }
    public required string Stderr { get; init; }
    public required int ExitCode { get; init; }
    public required TimeSpan Duration { get; init; }

    /// Resolved model id (e.g. "claude-haiku-4-5-20251001"). Null if we couldn't
    /// parse it from the JSON response.
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadInputTokens { get; init; }
    public long CacheCreationInputTokens { get; init; }
    public double CostUsd { get; init; }

    /// Optional error reason ("budget", "is_error", "timeout", "exit", "parse").
    /// Helps the caller report something useful when Success is false.
    public string? ErrorKind { get; init; }

    public long TotalInput => InputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}
