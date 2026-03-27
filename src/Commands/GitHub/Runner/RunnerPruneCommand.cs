using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Remove duplicate runner registrations, keeping only the most recent per repository
/// </summary>
public class RunnerPruneCommand : RunnerCommand<RunnerPruneCommand.Settings>
{
    private readonly IRunnerConfigurationService _configService;

    public RunnerPruneCommand(
        IRunnerConfigurationService configService,
        IAnsiConsole console)
        : base(console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    public class Settings : GitHubSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner("Prune");

            // 1. Show current state
            var registrations = await WithSpinnerAsync("Loading registrations...", async () =>
                await _configService.ListRegistrationsAsync());

            if (registrations.Count <= 1)
            {
                DisplayInfo($"Found {registrations.Count} registration(s). Nothing to prune.");
                return 0;
            }

            // Show duplicates before pruning
            var groups = registrations
                .GroupBy(r => $"{r.Owner}/{r.Repository}", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            if (groups.Count == 0)
            {
                DisplayInfo($"Found {registrations.Count} registration(s) across different repositories. No duplicates to prune.");
                return 0;
            }

            foreach (var group in groups)
            {
                var sorted = group.OrderByDescending(r => r.RegisteredAt).ToList();
                Console.MarkupLine($"[yellow]{group.Key}[/]: {group.Count()} registrations (keeping newest from {sorted.First().RegisteredAt:yyyy-MM-dd HH:mm})");
                foreach (var stale in sorted.Skip(1))
                {
                    Console.MarkupLine($"  [red]- Remove:[/] {stale.Id[..8]}... registered {stale.RegisteredAt:yyyy-MM-dd HH:mm}");
                }
            }

            Console.WriteLine();
            var totalToRemove = groups.Sum(g => g.Count() - 1);
            var confirmed = Console.Confirm($"Remove [yellow]{totalToRemove}[/] duplicate registration(s)?", defaultValue: true);
            if (!confirmed)
            {
                DisplayWarning("Prune cancelled.");
                return 0;
            }

            // 2. Prune
            var removed = await WithSpinnerAsync("Pruning duplicates...", async () =>
                await _configService.PruneRegistrationsAsync());

            Console.WriteLine();
            DisplaySuccess($"Removed {removed.Count} duplicate registration(s).");

            // 3. Show remaining
            var remaining = await _configService.ListRegistrationsAsync();
            DisplayInfo($"{remaining.Count} registration(s) remaining.");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to prune registrations: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
