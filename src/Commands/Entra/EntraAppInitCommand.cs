using System.ComponentModel;
using PKS.Infrastructure.Services.Entra;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Entra;

/// <summary>
/// Provisions the app registration an application signs in with, and puts its client secret somewhere
/// nothing can read it back out of.
///
/// The paste this removes: portal → App registrations → New registration → Certificates &amp; secrets →
/// New client secret → copy the blue box → `dotnet user-secrets set`. Six months later, again. On a
/// second machine, again. Every one of those copies is a credential in a shell history, a chat log or a
/// settings file, and the one in this workspace that leaked leaked exactly that way.
///
/// What comes out instead is an alias. `pks aspire run` binds it into the parameters an AppHost
/// declared; nothing prints it, and this command could not print it if it wanted to — what it holds is
/// a <c>SecretValue</c>.
/// </summary>
[Description("Create or adopt an Entra app registration and store its secret")]
public sealed class EntraAppInitCommand : AsyncCommand<EntraAppInitCommand.Settings>
{
    private readonly IEntraApplicationService _entra;
    private readonly IAnsiConsole _console;

    public EntraAppInitCommand(IEntraApplicationService entra, IAnsiConsole console)
    {
        _entra = entra;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Display name of the app registration (default: the alias)")]
        public string? Name { get; set; }

        [CommandOption("--alias <ALIAS>")]
        [Description("What to call it locally — this is what a capability binds (default: from NAME)")]
        public string? Alias { get; set; }

        [CommandOption("--redirect-uri <URI>")]
        [Description("Web redirect URI; repeatable, added to whatever is already registered")]
        public string[] RedirectUris { get; set; } = Array.Empty<string>();

        [CommandOption("--spa-redirect-uri <URI>")]
        [Description("Single-page-app redirect URI; repeatable")]
        public string[] SpaRedirectUris { get; set; } = Array.Empty<string>();

        [CommandOption("--audience <AUDIENCE>")]
        [Description("AzureADMyOrg (default), AzureADMultipleOrgs, AzureADandPersonalMicrosoftAccount")]
        public string Audience { get; set; } = "AzureADMyOrg";

        [CommandOption("--adopt <APPID>")]
        [Description("Adopt this exact registration instead of searching by name")]
        public string? AdoptAppId { get; set; }

        [CommandOption("--expires-days <DAYS>")]
        [Description("Lifetime of the client secret, 1–730 (default: 180)")]
        public int ExpiresDays { get; set; } = 180;

        [CommandOption("--rotate")]
        [Description("Mint a new secret even if a live one is stored, and remove the one it replaces")]
        public bool Rotate { get; set; }

        [CommandOption("--yes")]
        [Description("Do not ask before writing to the directory")]
        public bool Yes { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var name = settings.Name ?? settings.Alias;
        if (string.IsNullOrWhiteSpace(name))
        {
            _console.MarkupLine("[red]give the app registration a name:[/] pks entra app init \"Margin v1 (dev)\"");
            return 1;
        }

        if (!await _entra.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]not signed in to Azure.[/]");
            _console.MarkupLine("[dim]run [bold]pks foundry init[/] once — this uses the same sign-in.[/]");
            return 1;
        }

        var who = await _entra.WhoAmIAsync();
        if (who is null)
        {
            _console.MarkupLine("[red]signed in, but Microsoft Graph refused the token.[/]");
            _console.MarkupLine("[dim]sign in again with [bold]pks foundry init[/]; the stored one may have expired.[/]");
            return 1;
        }

        // Creating a registration writes to the company directory. That is not something to do because
        // a command was typed with a typo in it, so it is confirmed once, with the tenant named.
        _console.MarkupLine($"acting as [green]{who.Value.UserPrincipalName.EscapeMarkup()}[/] in tenant [green]{who.Value.TenantId.EscapeMarkup()}[/]");
        if (!settings.Yes && !_console.Confirm($"create or update the app registration [yellow]{name.EscapeMarkup()}[/] there?"))
        {
            _console.MarkupLine("[dim]nothing written.[/]");
            return 1;
        }

        var request = new EntraAppRequest
        {
            DisplayName = name,
            Alias = settings.Alias ?? name,
            SignInAudience = settings.Audience,
            RedirectUris = settings.RedirectUris.ToList(),
            SpaRedirectUris = settings.SpaRedirectUris.ToList(),
            AdoptAppId = settings.AdoptAppId,
            SecretDays = settings.ExpiresDays,
            Rotate = settings.Rotate,
        };

        EntraAppResult result;
        try
        {
            result = await _entra.InitAsync(request);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            if (ex.Message.Contains("Insufficient privileges", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Authorization_RequestDenied", StringComparison.OrdinalIgnoreCase))
            {
                _console.MarkupLine("[dim]the signed-in account may not create app registrations in this tenant — ask an admin, or use [bold]--adopt <appId>[/] on one that exists.[/]");
            }
            return 1;
        }

        var app = result.App;
        _console.WriteLine();
        _console.MarkupLine(result.CreatedApplication
            ? $"[green]created[/] {app.DisplayName.EscapeMarkup()}"
            : $"[green]adopted[/] {app.DisplayName.EscapeMarkup()}");

        var table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(new TableColumn("").PadRight(2));
        table.AddColumn(new TableColumn(""));
        table.AddRow("alias", $"[bold]{app.Alias.EscapeMarkup()}[/]");
        table.AddRow("client id", app.AppId.EscapeMarkup());
        table.AddRow("tenant id", app.TenantId.EscapeMarkup());
        table.AddRow("secret", result.MintedSecret
            ? $"stored, expires {app.SecretExpiresOn:yyyy-MM-dd}"
            : $"already stored, expires {app.SecretExpiresOn:yyyy-MM-dd}");
        _console.Write(table);

        if (result.CreatedServicePrincipal)
        {
            _console.MarkupLine("[dim]  + service principal — without one the registration cannot be signed in to[/]");
        }
        if (result.AddedRedirectUris)
        {
            _console.MarkupLine("[dim]  + redirect URIs added to the ones already registered[/]");
        }
        if (result.RemovedSecretKeyId is not null)
        {
            _console.MarkupLine("[dim]  - the previous secret was removed from the directory[/]");
        }

        _console.WriteLine();
        _console.MarkupLine("Bind it in an AppHost, next to the parameters that receive it:");
        _console.MarkupLine($"[dim]    builder.AddPksCapability(\"{app.Alias.EscapeMarkup()}\", \"Signing in with Entra ID\")[/]");
        _console.MarkupLine("[dim]           .Offers(\"entra\", \"An app registration pks provisioned\")[/]");
        _console.MarkupLine("[dim]           .Binds(tenantId,     \"{entra:tenantid}\")[/]");
        _console.MarkupLine("[dim]           .Binds(clientId,     \"{entra:clientid}\")[/]");
        _console.MarkupLine("[dim]           .Binds(clientSecret, \"{entra:clientsecret}\");[/]");
        return 0;
    }
}
