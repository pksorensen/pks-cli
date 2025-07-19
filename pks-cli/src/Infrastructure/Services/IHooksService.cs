namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Claude Code hooks integration
/// </summary>
public interface IHooksService
{
    /// <summary>
    /// Initializes Claude Code hooks by creating proper settings.json configuration
    /// </summary>
    /// <param name="force">Force overwrite existing hooks configuration</param>
    Task<bool> InitializeClaudeCodeHooksAsync(bool force = false);
}