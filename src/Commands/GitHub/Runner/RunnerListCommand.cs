using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// List all registered runner repositories
/// </summary>
public class RunnerListCommand : RunnerCommand<RunnerListCommand.Settings>
{
    private readonly IRunnerConfigurationService _configService;

    public RunnerListCommand(
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
            DisplayBanner("List");

            var registrations = await WithSpinnerAsync("Loading registrations...", async () =>
                await _configService.ListRegistrationsAsync());

            if (!registrations.Any())
            {
                DisplayWarning("No runner registrations found.");
                DisplayInfo("Use 'pks github runner register --repo owner/repo' to add one.");
                return 0;
            }

            var table = new Table()
                .Title($"[cyan]Runner Registrations ({registrations.Count})[/]")
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .AddColumn("[yellow]ID[/]")
                .AddColumn("[cyan]Repository[/]")
                .AddColumn("[green]Labels[/]")
                .AddColumn("[blue]Registered[/]")
                .AddColumn("[magenta]Enabled[/]");

            foreach (var reg in registrations)
            {
                table.AddRow(
                    reg.Id.Length > 8 ? reg.Id[..8] + "..." : reg.Id,
                    $"{reg.Owner}/{reg.Repository}",
                    reg.Labels,
                    reg.RegisteredAt.ToString("yyyy-MM-dd HH:mm"),
                    reg.Enabled ? "[green]Yes[/]" : "[red]No[/]");
            }

            Console.Write(table);

            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Failed to list registrations: {ex.Message}");
            if (settings.Verbose)
            {
                Console.WriteException(ex);
            }
            return 1;
        }
    }
}
