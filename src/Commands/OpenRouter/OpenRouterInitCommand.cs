using System.ComponentModel;
using System.Globalization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenRouter;

[Description("Register an OpenRouter API key")]
public sealed class OpenRouterInitCommand : AsyncCommand<OpenRouterInitCommand.Settings>
{
    private readonly IOpenRouterService _openRouter;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public OpenRouterInitCommand(IOpenRouterService openRouter, IActionGuard guard, IAnsiConsole console)
    {
        _openRouter = openRouter;
        _guard = guard;
        _console = console;
    }

    public sealed class Settings : OpenRouterSettings
    {
        [CommandOption("-f|--force")]
        [Description("Replace an existing OpenRouter API key")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!settings.Force && await _openRouter.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[green]OpenRouter API key is already registered.[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace it.[/]");
            return 0;
        }

        _console.MarkupLine("[bold cyan]OpenRouter API key registration[/]");
        _console.MarkupLine("[dim]Create a key at [link]https://openrouter.ai/settings/keys[/].[/]");
        var apiKey = _console.Prompt(
            new TextPrompt<string>("[cyan]API Key:[/]")
                .Secret()
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("[red]API key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var info = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Validating API key...", _ => _openRouter.ValidateApiKeyAsync(apiKey));
        if (info == null)
        {
            _console.MarkupLine("[red]OpenRouter rejected the API key.[/]");
            return 1;
        }

        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.CloudAuthWrite,
                "Store OpenRouter API credentials"));
        }
        catch (ActionGuardDeniedException exception)
        {
            _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        await _openRouter.StoreCredentialsAsync(new OpenRouterStoredCredentials
        {
            ApiKey = SecretValue.From(apiKey),
            CreatedAt = DateTime.UtcNow,
        });

        _console.MarkupLine("[green]OpenRouter API key registered.[/]");
        WriteKeyInfo(_console, info);
        _console.MarkupLine("[dim]Hand it to a local tool with [bold]eval $(pks openrouter proxy)[/].[/]");
        return 0;
    }

    internal static void WriteKeyInfo(IAnsiConsole console, OpenRouterKeyInfo info)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Label", Markup.Escape(info.Label ?? "(unnamed)"));
        table.AddRow("Usage", Credits(info.Usage));
        table.AddRow("Limit", info.Limit is { } limit ? Credits(limit) : "[dim]uncapped[/]");
        table.AddRow("Remaining", info.LimitRemaining is { } left ? Credits(left) : "[dim]uncapped[/]");
        // Free tier is not a footnote: the `:free` model routes only resolve for an account that
        // qualifies, so a "no" here explains a 404 that otherwise looks like a wrong model name.
        table.AddRow("Free tier", info.IsFreeTier ? "[green]yes[/]" : "[yellow]no[/]");
        console.Write(table);
    }

    private static string Credits(double value) =>
        "$" + value.ToString("0.####", CultureInfo.InvariantCulture);
}
