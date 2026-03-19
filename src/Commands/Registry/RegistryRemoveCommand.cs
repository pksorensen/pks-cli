using System.ComponentModel;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Registry;

/// <summary>
/// Remove a registered container registry.
/// Usage: pks registry remove &lt;hostname&gt;
/// </summary>
public class RegistryRemoveCommand : Command<RegistryRemoveCommand.Settings>
{
    private readonly IRegistryConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RegistryRemoveCommand(IRegistryConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<hostname>")]
        [Description("Registry hostname to remove (e.g. registry.kjeldager.io)")]
        public string Hostname { get; set; } = "";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var entry = await _configService.GetByHostnameAsync(settings.Hostname);
        if (entry == null)
        {
            _console.MarkupLine($"[red]No registry registered for '{settings.Hostname.EscapeMarkup()}'[/]");
            return 1;
        }

        await _configService.RemoveAsync(settings.Hostname);
        _console.MarkupLine($"[green]Registry '{settings.Hostname.EscapeMarkup()}' removed.[/]");

        return 0;
    }
}
