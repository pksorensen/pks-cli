using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;

namespace PKS.Commands.Persona;

public class PersonaListSettings : PersonaSettings
{
    [CommandOption("--locale")]
    [Description("Locale to enumerate (default: da).")]
    public string Locale { get; set; } = "da";

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of the human table.")]
    public bool Json { get; set; }
}

public class PersonaListCommand : AsyncCommand<PersonaListSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaStore _store;

    public PersonaListCommand(IPersonaPathResolver paths, IPersonaStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaListSettings settings)
    {
        var root = _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null)
        {
            Console.Error.WriteLine("error: no personas/ directory found walking up from cwd.");
            return 1;
        }

        var personas = await _store.ListAsync(root, settings.Locale);

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                locale = settings.Locale,
                count = personas.Count,
                personas = personas.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    segment = p.Segment,
                    bucket = p.Bucket,
                    lang = p.Lang,
                    sourcePath = p.SourcePath,
                }),
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 0;
        }

        if (personas.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]![/] No personas found for locale [cyan]{settings.Locale}[/].");
            return 0;
        }

        var table = new Table().Border(TableBorder.Minimal).ShowHeaders();
        table.AddColumn("Bucket");
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn("Segment");
        foreach (var p in personas)
        {
            table.AddRow(
                $"[grey]{Markup.Escape(p.Bucket)}[/]",
                $"[bold]{Markup.Escape(p.Id)}[/]",
                Markup.Escape(p.Name),
                $"[grey]{Markup.Escape(p.Segment)}[/]");
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{personas.Count} persona{(personas.Count == 1 ? "" : "s")} ({Markup.Escape(settings.Locale)})[/]");
        return 0;
    }
}
