namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Simple validation result for testing purposes
/// </summary>
public class HookValidationResult
{
    public bool IsValid { get; set; } = true;
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}