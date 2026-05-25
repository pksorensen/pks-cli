using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

/// Prints the cowork authoring prompt to stdout so the user can pipe it
/// (`pks writing profile prompt | pbcopy`) or just copy-paste.
public class WritingProfilePromptCommand : AsyncCommand<WritingSettings>
{
    private readonly IWritingPathResolver _paths;
    private readonly IWritingProfileStore _store;

    public WritingProfilePromptCommand(IWritingPathResolver paths, IWritingProfileStore store)
    {
        _paths = paths;
        _store = store;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WritingSettings settings)
    {
        await _store.EnsureGlobalLayoutAsync();
        var path = _paths.GlobalAuthoringPromptPath;
        var content = await System.IO.File.ReadAllTextAsync(path);
        // Plain stdout — no Spectre markup — so the prompt is pipe-friendly.
        Console.Write(content);
        return 0;
    }
}
