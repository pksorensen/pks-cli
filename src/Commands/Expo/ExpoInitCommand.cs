using System.ComponentModel;
using PKS.Infrastructure.Services.Expo;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Expo;

/// <summary>
/// Store an Expo access token on this runner host.
/// Usage: pks expo init
///
/// The token is read from a hidden prompt or stdin, never from argv — an argument lands in shell
/// history and in every <c>ps</c> listing for the lifetime of the process.
/// </summary>
public class ExpoInitCommand : AsyncCommand<ExpoInitCommand.Settings>
{
    private readonly IExpoCredentialService _expo;
    private readonly ISecretStore _secrets;
    private readonly IAnsiConsole _console;

    public ExpoInitCommand(IExpoCredentialService expo, ISecretStore secrets, IAnsiConsole console)
    {
        _expo = expo ?? throw new ArgumentNullException(nameof(expo));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public class Settings : ExpoSettings
    {
        [CommandOption("--stdin")]
        [Description("Read the token from stdin instead of prompting (for scripted setup)")]
        public bool Stdin { get; set; }

        [CommandOption("--force")]
        [Description("Replace an existing token without confirming")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var panel = new Panel("[bold cyan]Expo Init[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        if (await _secrets.HasAsync(ExpoCredentialService.TokenKey) && !settings.Force)
        {
            _console.MarkupLine("[yellow]An Expo token is already stored on this host.[/]");
            if (!_console.Confirm("Replace it?", defaultValue: false))
            {
                _console.MarkupLine("[yellow]Cancelled — existing token left in place.[/]");
                return 0;
            }
        }

        string? token;
        if (settings.Stdin)
        {
            token = (await System.Console.In.ReadToEndAsync())?.Trim();
        }
        else
        {
            _console.MarkupLine("[dim]Create a robot user at https://expo.dev → your account → Robot users,[/]");
            _console.MarkupLine("[dim]grant it the role the release needs, and paste its access token below.[/]");
            _console.WriteLine();
            token = _console.Prompt(
                new TextPrompt<string>("[yellow]Expo access token:[/]").Secret());
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            _console.MarkupLine("[red]No token provided.[/]");
            return 1;
        }

        _console.MarkupLine("[dim]Verifying against Expo...[/]");
        var actor = await _expo.ValidateTokenAsync(token);
        if (actor == null)
        {
            _console.MarkupLine("[red]Expo rejected this token.[/]");
            _console.MarkupLine("[yellow]Check that it was copied whole and has not been revoked. Nothing was stored.[/]");
            return 1;
        }

        await _secrets.SetAsync(ExpoCredentialService.TokenKey, token);

        var descriptor = await _secrets.DescribeAsync(ExpoCredentialService.TokenKey);
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Account", string.IsNullOrEmpty(actor.Name) ? "[dim](unnamed)[/]" : actor.Name.EscapeMarkup());
        table.AddRow("Type", actor.Type.EscapeMarkup());
        table.AddRow("Stored as", ExpoCredentialService.TokenKey);
        if (descriptor != null)
            table.AddRow("Fingerprint", descriptor.Fingerprint);

        _console.Write(table);
        _console.WriteLine();

        if (!actor.IsRobot)
        {
            _console.MarkupLine("[yellow]Warning: this is a personal account token, not a robot user.[/]");
            _console.MarkupLine("[yellow]It carries your full Expo permissions. Prefer a robot user for CI.[/]");
            _console.WriteLine();
        }

        _console.MarkupLine("[green]Expo token stored (encrypted).[/]");
        _console.MarkupLine("[cyan]Grant a repository access with:[/] pks github runner register --repo owner/repo --expo");

        return 0;
    }
}
