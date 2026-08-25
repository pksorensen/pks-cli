using PKS.Infrastructure.Services.Expo;
using PKS.Infrastructure.Services.Runner;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Expo;

/// <summary>
/// Report whether an Expo token is stored and which repositories may spend it.
/// Usage: pks expo status
///
/// Never prints the token. Presence, fingerprint and the resolved account are enough to tell an
/// operator whether the box is set up, without putting a credential in a terminal transcript.
/// </summary>
public class ExpoStatusCommand : AsyncCommand<ExpoSettings>
{
    private readonly IExpoCredentialService _expo;
    private readonly ISecretStore _secrets;
    private readonly IRunnerConfigurationService _runners;
    private readonly IAnsiConsole _console;

    public ExpoStatusCommand(
        IExpoCredentialService expo,
        ISecretStore secrets,
        IRunnerConfigurationService runners,
        IAnsiConsole console)
    {
        _expo = expo ?? throw new ArgumentNullException(nameof(expo));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ExpoSettings settings)
    {
        var panel = new Panel("[bold cyan]Expo Status[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        var descriptor = await _secrets.DescribeAsync(ExpoCredentialService.TokenKey);
        if (descriptor == null)
        {
            _console.MarkupLine("[yellow]No Expo token stored on this host.[/]");
            _console.MarkupLine("[cyan]Run 'pks expo init' to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Stored as", descriptor.Key);
        table.AddRow("Written", descriptor.SetAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));
        table.AddRow("Fingerprint", descriptor.Fingerprint);

        var actor = await _expo.DescribeStoredActorAsync();
        if (actor == null)
        {
            table.AddRow("Account", "[red]Expo rejected the stored token[/]");
        }
        else
        {
            table.AddRow("Account", string.IsNullOrEmpty(actor.Name) ? "[dim](unnamed)[/]" : actor.Name.EscapeMarkup());
            table.AddRow("Type", actor.IsRobot ? actor.Type.EscapeMarkup() : $"[yellow]{actor.Type.EscapeMarkup()} (not a robot user)[/]");
        }

        _console.Write(table);
        _console.WriteLine();

        var allowed = (await _runners.ListRegistrationsAsync())
            .Where(r => r.Enabled && r.ExpoEnabled)
            .ToList();

        if (allowed.Count == 0)
        {
            _console.MarkupLine("[yellow]No repository may fetch this token.[/]");
            _console.MarkupLine("[cyan]Grant one with:[/] pks github runner register --repo owner/repo --expo");
        }
        else
        {
            var repoTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[yellow]Repository allowed to fetch the token[/]")
                .AddColumn("[dim]Registered[/]");

            foreach (var r in allowed)
                repoTable.AddRow($"{r.Owner}/{r.Repository}", r.RegisteredAt.ToString("yyyy-MM-dd"));

            _console.Write(repoTable);
        }

        if (actor == null) return 1;
        return 0;
    }
}
