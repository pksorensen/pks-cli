namespace PKS.Infrastructure.Services.Brain;

/// <summary>Selects the extract summarizer backend for `pks brain extract --agent ...`.</summary>
public interface IExtractRunnerFactory
{
    /// "claude" → shell out to the claude binary; anything else (default) → in-process pks agent.
    IClaudeRunner Resolve(string? agent);
}

public sealed class ExtractRunnerFactory : IExtractRunnerFactory
{
    private readonly ClaudeBinaryRunner _claude;
    private readonly PksAgentRunner _pks;

    public ExtractRunnerFactory(ClaudeBinaryRunner claude, PksAgentRunner pks)
    {
        _claude = claude;
        _pks = pks;
    }

    public IClaudeRunner Resolve(string? agent) =>
        string.Equals(agent, "claude", StringComparison.OrdinalIgnoreCase) ? _claude : _pks;
}
