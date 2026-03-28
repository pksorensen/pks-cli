using PKS.Infrastructure;
using PKS.Infrastructure.Attributes;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
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
        var lintCmd = await _config.GetAsync("hooks:quality:lint_command");

        if (string.IsNullOrWhiteSpace(lintCmd))
        {
            return HookDecision.Proceed();
        }

        if (!settings.Json)
        {
            AnsiConsole.MarkupLine($"[cyan]Running lint check:[/] {lintCmd}");
        }

        var result = await RunProcessAsync(lintCmd, Directory.GetCurrentDirectory());

        if (result.ExitCode == 0)
        {
            return HookDecision.Proceed();
        }

        var message = $"Lint check failed — fix the errors before stopping:\n\n{result.Output}";

        return HookDecision.Block(message);
    }
}