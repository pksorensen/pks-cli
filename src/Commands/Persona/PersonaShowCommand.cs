using System.ComponentModel;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaShowSettings : PersonaSettings
{
    [CommandArgument(0, "<persona-id>")]
    [Description("Persona id (slug) to render.")]
    public string Id { get; set; } = "";

    [CommandOption("--locale")]
    [Description("Locale to look up the persona under. Default: da.")]
    public string Locale { get; set; } = "da";
}

public class PersonaShowCommand : AsyncCommand<PersonaShowSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _store;

    public PersonaShowCommand(IPersonaPathResolver paths, IPersonaStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaShowSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            Console.Error.WriteLine("error: persona-id argument required.");
            return 1;
        }
        var root = _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null)
        {
            Console.Error.WriteLine("error: no personas/ directory found walking up from cwd.");
            return 1;
        }
        var persona = await _store.LoadByIdAsync(root, settings.Locale, settings.Id);
        if (persona is null)
        {
            Console.Error.WriteLine($"error: persona '{settings.Id}' not found under personas/{settings.Locale}/.");
            return 1;
        }
        // Round-trip the source verbatim — `show` is meant to be pipeable to
        // another tool or quickly scanned.
        Console.WriteLine(System.IO.File.ReadAllText(persona.SourcePath));
        return 0;
    }
}
