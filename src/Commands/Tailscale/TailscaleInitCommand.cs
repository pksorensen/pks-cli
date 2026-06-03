using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Tailscale;

/// <summary>
/// Store a Tailscale auth key + join behaviour so <c>pks vm tailscale</c> can join a VM to
/// the tailnet. Create an auth key at login.tailscale.com → Settings → Keys.
/// </summary>
[Description("Store a Tailscale auth key and join settings")]
public class TailscaleInitCommand : Command<TailscaleInitCommand.Settings>
{
    private readonly ITailscaleService _tailscale;
    private readonly IAnsiConsole _console;

    public TailscaleInitCommand(ITailscaleService tailscale, IAnsiConsole console)
    {
        _tailscale = tailscale;
        _console = console;
    }

    public class Settings : TailscaleSettings
    {
        [CommandOption("-f|--force")]
        [Description("Re-enter the auth key even if already configured")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _tailscale.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[green]Tailscale is already configured.[/] [dim]Use [bold]--force[/] to replace the auth key.[/]");
            return 0;
        }

        _console.MarkupLine("[cyan]Tailscale auth key[/] [dim](create at login.tailscale.com → Settings → Keys; reusable + ephemeral recommended)[/]");
        var authKey = _console.Prompt(
            new TextPrompt<string>("[cyan]Auth key[/] [dim](tskey-…)[/]:")
                .Secret()
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]Auth key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var enableSsh = _console.Confirm("[cyan]Enable Tailscale SSH (--ssh)?[/]", defaultValue: true);
        var acceptRoutes = _console.Confirm("[cyan]Accept subnet routes (--accept-routes, e.g. to reach your NAS)?[/]", defaultValue: true);
        var exitNode = _console.Confirm("[cyan]Advertise as exit node (--advertise-exit-node)?[/]", defaultValue: true);

        var loginServer = _console.Prompt(
            new TextPrompt<string>("[cyan]Custom control server[/] [dim](Headscale URL, or Enter for Tailscale)[/]:")
                .AllowEmpty());

        await _tailscale.StoreCredentialsAsync(new TailscaleStoredCredentials
        {
            AuthKey = authKey,
            EnableSsh = enableSsh,
            AcceptRoutes = acceptRoutes,
            AdvertiseExitNode = exitNode,
            LoginServer = string.IsNullOrWhiteSpace(loginServer) ? null : loginServer.Trim(),
            CreatedAt = DateTime.UtcNow
        });

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Tailscale configured[/]")
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");
        table.AddRow("Tailscale SSH", enableSsh ? "[green]on[/]" : "[dim]off[/]");
        table.AddRow("Accept routes", acceptRoutes ? "[green]on[/]" : "[dim]off[/]");
        table.AddRow("Exit node", exitNode ? "[green]on[/]" : "[dim]off[/]");
        if (!string.IsNullOrWhiteSpace(loginServer)) table.AddRow("Control server", Markup.Escape(loginServer));
        _console.Write(table);
        _console.MarkupLine("[dim]Join a VM: pks vm tailscale[/]");
        return 0;
    }
}
