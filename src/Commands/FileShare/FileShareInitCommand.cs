using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.FileShares;

[Description("Authenticate with a file share provider")]
public class FileShareInitCommand : Command<FileShareInitCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public FileShareInitCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public class Settings : FileShareSettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var providers = _registry.GetAllProviders().ToList();

        if (providers.Count == 0)
        {
            _console.MarkupLine("[red]No file share providers are registered.[/]");
            return 1;
        }

        IFileShareProvider selectedProvider;
        if (providers.Count == 1)
        {
            selectedProvider = providers[0];
        }
        else
        {
            var providerName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a file share provider:[/]")
                    .AddChoices(providers.Select(p => p.ProviderName)));
            selectedProvider = providers.First(p => p.ProviderName == providerName);
        }

        if (!settings.Force && await selectedProvider.IsAuthenticatedAsync())
        {
            _console.MarkupLine($"[green]Already authenticated with [bold]{Markup.Escape(selectedProvider.ProviderName)}[/].[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        var success = await selectedProvider.AuthenticateAsync(_console);
        return success ? 0 : 1;
    }
}
