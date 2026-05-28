namespace PKS.Infrastructure.Services.Writing.Models;

/// A learning pattern persisted to ~/.pks-cli/writing/naturalness-patterns.md.
/// Injected as few-shot into future naturalness extraction prompts so the
/// critic biases suggestions toward what the author has previously accepted.
public sealed class NaturalnessPattern
{
    public string TriggerSummary { get; set; } = "";
    public string AcceptedExample { get; set; } = "";
    public string? RejectedExample { get; set; }
    public string? FirstSeenSource { get; set; }
    public int AcceptedCount { get; set; } = 1;
}
