using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

/// <summary>
/// Store a TypeSafe (Jev) API key on this host.
/// Usage: pks typesafe init [--stdin] [--force] [--model jev-latest]
///
/// The key is read from a hidden prompt or stdin, never from argv — an argument lands in shell
/// history and in every <c>ps</c> listing for the lifetime of the process. It is validated by
/// listing the models the key may call before anything is written.
/// </summary>
public class TypeSafeInitCommand : AsyncCommand<TypeSafeInitCommand.Settings>
{
    private readonly ITypeSafeCredentialService _typeSafe;
    private readonly ISecretStore _secrets;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public TypeSafeInitCommand(
        ITypeSafeCredentialService typeSafe,
        ISecretStore secrets,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _typeSafe = typeSafe ?? throw new ArgumentNullException(nameof(typeSafe));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public class Settings : TypeSafeSettings
    {
        [CommandOption("--stdin")]
        [Description("Read the API key from stdin instead of prompting (for scripted setup)")]
        public bool Stdin { get; set; }

        [CommandOption("-f|--force")]
        [Description("Replace an existing key without confirming")]
        public bool Force { get; set; }

        [CommandOption("--model <MODEL>")]
        [Description("Model sent when a request names none (default: jev-latest)")]
        public string? Model { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var panel = new Panel("[bold cyan]TypeSafe Init[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        if (await _secrets.HasAsync(TypeSafeCredentialService.ApiKeyKey) && !settings.Force)
        {
            _console.MarkupLine("[yellow]A TypeSafe API key is already stored on this host.[/]");
            if (settings.Stdin || !_console.Confirm("Replace it?", defaultValue: false))
            {
                _console.MarkupLine("[yellow]Cancelled — existing key left in place. Use [bold]--force[/] to replace it.[/]");
                return 0;
            }
        }

        string? apiKey;
        if (settings.Stdin)
        {
            apiKey = (await System.Console.In.ReadToEndAsync())?.Trim();
        }
        else
        {
            _console.MarkupLine("[dim]Create a key at [link]https://console.typesafe.ai/keys[/] and paste it below.[/]");
            _console.WriteLine();
            apiKey = _console.Prompt(
                new TextPrompt<string>("[yellow]TypeSafe API key:[/]").Secret()).Trim();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _console.MarkupLine("[red]No API key provided.[/]");
            return 1;
        }

        _console.MarkupLine("[dim]Verifying against TypeSafe (GET /v1/models)...[/]");
        var models = await _typeSafe.ValidateApiKeyAsync(apiKey);
        if (models == null)
        {
            _console.MarkupLine("[red]TypeSafe rejected this key, or could not be reached.[/]");
            _console.MarkupLine("[yellow]Check that it was copied whole and has not been revoked. Nothing was stored.[/]");
            return 1;
        }

        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.CloudAuthWrite,
                "Store TypeSafe API credentials"));
        }
        catch (ActionGuardDeniedException exception)
        {
            _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        await _secrets.SetAsync(TypeSafeCredentialService.ApiKeyKey, apiKey);

        var model = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
        if (model != null)
        {
            if (models.Count > 0 && !models.Any(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
                _console.MarkupLine($"[yellow]Note: '{Markup.Escape(model)}' is not in the model list; versioned ids are accepted anyway.[/]");
            await _typeSafe.SetDefaultModelAsync(model);
        }

        var descriptor = await _secrets.DescribeAsync(TypeSafeCredentialService.ApiKeyKey);
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Stored as", TypeSafeCredentialService.ApiKeyKey);
        if (descriptor != null)
            table.AddRow("Fingerprint", descriptor.Fingerprint);
        table.AddRow("Default model", Markup.Escape(await _typeSafe.GetDefaultModelAsync()));
        table.AddRow("Models", models.Count == 0
            ? "[dim](none listed)[/]"
            : Markup.Escape(string.Join(", ", models.Select(m => m.Name))));

        _console.Write(table);
        _console.WriteLine();

        _console.MarkupLine("[green]TypeSafe API key stored (encrypted).[/]");
        _console.MarkupLine("[cyan]Let a repository's jobs use it with:[/] pks typesafe allow owner/repo");
        _console.MarkupLine("[cyan]Try it locally with:[/] pks typesafe ask --file request.json");

        return 0;
    }
}
