using System.Text.Json;
using PKS.Infrastructure;
using PKS.Infrastructure.Attributes;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Cli;

namespace PKS.Commands.Hooks;

/// <summary>
/// Command for handling Stop hook events from Claude Code.
/// Runs the configured lint command (if any) and blocks Claude from stopping if lint fails.
/// </summary>
[SkipFirstTimeWarning]
public class StopCommand : BaseHookCommand
{
    private readonly IConfigurationService _config;

    public StopCommand(IConfigurationService config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override async Task<HookDecision> ProcessHookEventAsync(CommandContext context, HooksSettings settings)
    {
        // Read stdin to check stop_hook_active (prevents infinite loops)
        if (Console.IsInputRedirected)
        {
            var stdin = await Console.In.ReadToEndAsync();
            try
            {
                var input = JsonSerializer.Deserialize<JsonElement>(stdin);
                if (input.TryGetProperty("stop_hook_active", out var active) && active.GetBoolean())
                {
                    return HookDecision.Proceed();
                }
            }
            catch { /* ignore parse errors */ }
        }

        var lintCmd = await _config.GetAsync("hooks:quality:lint_command");

        if (string.IsNullOrWhiteSpace(lintCmd))
        {
            return HookDecision.Proceed();
        }

        Console.Error.WriteLine($"Running lint check: {lintCmd}");

        var result = await RunProcessAsync(lintCmd, Directory.GetCurrentDirectory());

        if (result.ExitCode == 0)
        {
            return HookDecision.Proceed();
        }

        var reason = $"Lint check failed — fix the errors before stopping:\n\n{result.Output}";

        return HookDecision.Block(reason);
    }
}
