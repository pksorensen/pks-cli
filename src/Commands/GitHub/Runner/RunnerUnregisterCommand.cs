using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Unregister a repository from the runner daemon
/// </summary>
public class RunnerUnregisterCommand : RunnerCommand<RunnerUnregisterCommand.Settings>
{
    private readonly IRunnerConfigurationService _configService;

    public RunnerUnregisterCommand(
        IRunnerConfigurationService configService,
        IAnsiConsole console)
        : base(console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    public class Settings : RunnerSettings
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
            DisplayBanner("Unregister");

            // 1. Validate repository argument
            var (owner, repo) = ParseRepository(settings.Repository);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            {
                DisplayError("Repository must be specified in owner/repo format. Use --repo owner/repo.");
                return 1;
            }

            DisplayInfo($"Repository: {owner}/{repo}");
            Console.WriteLine();

            // 2. Find matching registration
            var registrations = await WithSpinnerAsync("Loading registrations...", async () =>
                await _configService.ListRegistrationsAsync());

            var match = registrations.FirstOrDefault(r =>
                string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Repository, repo, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                DisplayError($"No registration found for {owner}/{repo}.");
                DisplayWarning("Use 'pks github runner list' to see all registrations.");
                return 1;
            }

            // 3. Confirm removal
            DisplayInfo($"Found registration: {match.Id}");
            DisplayInfo($"Labels: {match.Labels}");
            DisplayInfo($"Registered: {match.RegisteredAt:yyyy-MM-dd HH:mm:ss UTC}");
            Console.WriteLine();

            var confirmed = Console.Confirm($"Remove registration for [yellow]{owner}/{repo}[/]?", defaultValue: true);
            if (!confirmed)
            {
                DisplayWarning("Unregister cancelled.");
                return 0;
            }

            // 4. Remove registration
            var removed = await WithSpinnerAsync("Removing registration...", async () =>
                await _configService.RemoveRegistrationAsync(match.Id));

            if (!removed)
            {
                DisplayError("Failed to remove registration.");
                return 1;
            }

            Console.WriteLine();
            DisplaySuccess($"Runner unregistered for {owner}/{repo}.");

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to unregister runner: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
