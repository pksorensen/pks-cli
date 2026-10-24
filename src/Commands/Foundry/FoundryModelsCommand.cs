using System.ComponentModel;
using PKS.Commands.Models;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// <c>pks foundry models</c>: the Foundry slice of <see cref="ModelsCommand"/> — the deployments on
/// the selected resource with what <c>pks foundry select</c> chose.
/// </summary>
[Description("List the model deployments on the selected Foundry resource (the foundry slice of pks models)")]
public class FoundryModelsCommand : AsyncCommand<FoundryModelsCommand.Settings>
{
    private readonly IModelCatalogService _catalog;
    private readonly IAnsiConsole _console;

    public FoundryModelsCommand(IModelCatalogService catalog, IAnsiConsole console)
    {
        _catalog = catalog;
        _console = console;
    }

    public class Settings : FoundrySettings
    {
        [CommandOption("--json")]
        [Description("Print the catalog as JSON (OpenAI list shape with a pks object per model)")]
        public bool Json { get; set; }

        [CommandOption("--role <ROLE>")]
        [Description("Only deployments with this pks role (text, decision)")]
        public string? Role { get; set; }
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings)
        => ModelsCommand.RunAsync(_catalog, _console, ProviderCatalog.Foundry, settings.Role, settings.Json);
}
