using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Scaleway;

/// <summary>
/// Authenticate with Scaleway using a static API key (Access Key + Secret Key) and
/// select a default project + zone. Scaleway has no OAuth — the secret key is stored
/// and sent as the <c>X-Auth-Token</c> header on every request.
/// </summary>
[Description("Authenticate with Scaleway and select a default project and zone")]
public class ScalewayInitCommand : Command<ScalewayInitCommand.Settings>
{
    // Zones offered at init. GPU instances (H100/L40S/L4) are not available in every zone.
    private static readonly string[] Zones =
    {
        "fr-par-1", "fr-par-2", "fr-par-3",
        "nl-ams-1", "nl-ams-2", "nl-ams-3",
        "pl-waw-1", "pl-waw-2", "pl-waw-3"
    };

    private readonly IScalewayService _scaleway;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public ScalewayInitCommand(IScalewayService scaleway, IActionGuard guard, IAnsiConsole console)
    {
        _scaleway = scaleway;
        _guard = guard;
        _console = console;
    }

    public class Settings : ScalewaySettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _scaleway.IsAuthenticatedAsync())
        {
            var existing = await _scaleway.GetStoredCredentialsAsync();
            _console.MarkupLine("[green]Already authenticated with Scaleway.[/]");
            if (existing != null)
            {
                _console.MarkupLine($"[green]Project: [bold]{Markup.Escape(existing.DefaultProjectName)}[/] ({Markup.Escape(existing.DefaultProjectId)})[/]");
                _console.MarkupLine($"[green]Default zone: [bold]{Markup.Escape(existing.DefaultZone)}[/][/]");
            }
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        _console.MarkupLine("[cyan]Scaleway API key[/] [dim](create one at console.scaleway.com → IAM → API keys)[/]");
        var accessKey = _console.Prompt(
            new TextPrompt<string>("[cyan]Access Key[/] [dim](starts with SCW…)[/]:")
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]Access key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var secretKey = _console.Prompt(
            new TextPrompt<string>("[cyan]Secret Key[/]:")
                .Secret()
                .Validate(v => string.IsNullOrWhiteSpace(v)
                    ? ValidationResult.Error("[red]Secret key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        // Derive organization + default project: api-key -> default_project_id -> project.organization_id
        string organizationId;
        string defaultProjectId;
        try
        {
            var keyInfo = await _scaleway.GetApiKeyInfoAsync(accessKey, secretKey);
            if (keyInfo == null || string.IsNullOrEmpty(keyInfo.DefaultProjectId))
            {
                _console.MarkupLine("[yellow]Could not look up the API key automatically.[/]");
                defaultProjectId = _console.Prompt(new TextPrompt<string>("[cyan]Default Project ID[/]:"));
                organizationId = _console.Prompt(new TextPrompt<string>("[cyan]Organization ID[/]:"));
            }
            else
            {
                defaultProjectId = keyInfo.DefaultProjectId;
                var project = await _scaleway.GetProjectAsync(defaultProjectId, secretKey);
                if (project == null || string.IsNullOrEmpty(project.OrganizationId))
                {
                    organizationId = _console.Prompt(new TextPrompt<string>("[cyan]Organization ID[/]:"));
                }
                else
                {
                    organizationId = project.OrganizationId;
                }
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // List projects so the user can confirm/choose the default
        List<ScalewayProject> projects;
        try
        {
            projects = await _scaleway.ListProjectsAsync(organizationId, secretKey);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to list projects:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        ScalewayProject selectedProject;
        if (projects.Count == 0)
        {
            selectedProject = new ScalewayProject { Id = defaultProjectId, Name = defaultProjectId, OrganizationId = organizationId };
        }
        else if (projects.Count == 1)
        {
            selectedProject = projects[0];
        }
        else
        {
            var choice = _console.Prompt(
                new SelectionPrompt<ScalewayProject>()
                    .Title("[cyan]Select default project:[/]")
                    .UseConverter(p => $"{p.Name} ({p.Id})")
                    .HighlightStyle(Style.Parse("cyan"))
                    .AddChoices(projects.OrderByDescending(p => p.Id == defaultProjectId)));
            selectedProject = choice;
        }

        var zone = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select default zone:[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .PageSize(12)
                .AddChoices(Zones));

        // Gate persisting cloud credentials (an agent could otherwise paste an API key non-interactively).
        try { await _guard.RequireAsync(new ActionRequest(ActionIds.CloudAuthWrite, "Store Scaleway API credentials")); }
        catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

        await _scaleway.StoreCredentialsAsync(new ScalewayStoredCredentials
        {
            AccessKey = accessKey,
            SecretKey = secretKey,
            OrganizationId = organizationId,
            DefaultProjectId = selectedProject.Id,
            DefaultProjectName = selectedProject.Name,
            DefaultZone = zone,
            CreatedAt = DateTime.UtcNow
        });

        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Scaleway Authentication Successful[/]")
            .AddColumn("[bold]Property[/]")
            .AddColumn("[bold]Value[/]");
        table.AddRow("Organization", Markup.Escape(organizationId));
        table.AddRow("Project", Markup.Escape(selectedProject.Name));
        table.AddRow("Project ID", Markup.Escape(selectedProject.Id));
        table.AddRow("Default zone", Markup.Escape(zone));
        _console.Write(table);
        _console.MarkupLine("[dim]List your instances: pks vm list[/]");

        return 0;
    }
}
