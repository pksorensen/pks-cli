using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Hetzner;

/// <summary>
/// Store a Hetzner Cloud API token and pick a default location. Hetzner Cloud has no
/// OAuth: a token is minted per project in the Cloud Console (Security → API tokens,
/// Read &amp; Write) and sent as a bearer header on every request. The token IS the
/// project — there is no org/sub-account API — so <c>init</c> only validates it (by
/// listing locations) and labels it.
/// </summary>
[Description("Authenticate with Hetzner Cloud and select a default location")]
public class HetznerInitCommand : Command<HetznerInitCommand.Settings>
{
    private readonly IHetznerCloudService _hetzner;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public HetznerInitCommand(IHetznerCloudService hetzner, IActionGuard guard, IAnsiConsole console)
    {
        _hetzner = hetzner;
        _guard = guard;
        _console = console;
    }

    public class Settings : HetznerSettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _hetzner.IsAuthenticatedAsync())
        {
            var existing = await _hetzner.GetStoredCredentialsAsync();
            _console.MarkupLine("[green]Already authenticated with Hetzner Cloud.[/]");
            if (existing != null)
            {
                _console.MarkupLine($"[green]Project: [bold]{Markup.Escape(existing.ProjectName)}[/][/]");
                _console.MarkupLine($"[green]Default location: [bold]{Markup.Escape(existing.DefaultLocation)}[/][/]");
            }
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        _console.MarkupLine("[cyan]Hetzner Cloud API token[/] [dim](console.hetzner.cloud → project → Security → API tokens → Read & Write)[/]");
        var token = _console.Prompt(
            new TextPrompt<string>("[cyan]API token[/]:")
                .Secret()
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]Token is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        // Validate the token with the cheapest read there is, and get the location list for free.
        List<HetznerLocation> locations;
        try
        {
            locations = await _hetzner.ListLocationsAsync(token);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        if (locations.Count == 0)
        {
            _console.MarkupLine("[red]The token works but Hetzner returned no locations.[/]");
            return 1;
        }

        var projectName = _console.Prompt(
            new TextPrompt<string>("[cyan]Project label[/] [dim](the Cloud Console project this token belongs to)[/]:")
                .DefaultValue("default"))
            .Trim();

        // CX (cheapest, Intel/AMD shared) only exists in Germany/Finland; put EU-central first.
        var ordered = locations
            .OrderBy(l => l.NetworkZone == "eu-central" ? 0 : 1)
            .ThenBy(l => l.Name)
            .ToList();
        var location = _console.Prompt(
            new SelectionPrompt<HetznerLocation>()
                .Title("[cyan]Select default location:[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .PageSize(12)
                .UseConverter(l => l.DisplayLabel)
                .AddChoices(ordered));

        // Gate persisting cloud credentials (an agent could otherwise paste a token non-interactively).
        try { await _guard.RequireAsync(new ActionRequest(ActionIds.CloudAuthWrite, "Store Hetzner Cloud API token")); }
        catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

        await _hetzner.StoreCredentialsAsync(new HetznerStoredCredentials
        {
            Token = SecretValue.From(token),
            ProjectName = projectName,
            DefaultLocation = location.Name,
            CreatedAt = DateTime.UtcNow
        });

        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Hetzner Cloud Authentication Successful[/]")
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");
        table.AddRow("Project", Markup.Escape(projectName));
        table.AddRow("Default location", Markup.Escape(location.DisplayLabel));
        table.AddRow("Locations visible", locations.Count.ToString());
        _console.Write(table);
        _console.MarkupLine("[dim]Create a per-hour machine: pks vm init → Hetzner Cloud[/]");
        _console.MarkupLine("[dim]Remember: Hetzner bills a server until it is deleted, powered off or not.[/]");

        return 0;
    }
}
