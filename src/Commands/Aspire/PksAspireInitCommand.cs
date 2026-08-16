using System.ComponentModel;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Aspire;

/// <summary>
/// Puts the AppHost half of `pks aspire run` into a project.
///
/// It writes one file, `PksDeclare.cs`, and nothing else — no package reference, no project reference,
/// no line in the csproj. That is the whole design: an AppHost cannot take a dependency on pks-cli
/// (most of them are in trees that are not allowed to reach it, and the ones that could should not have
/// to), so the contract travels as source. The same choice the Go side made with
/// `internal/pksmanifest`, for the same reason.
///
/// Which means the copy is now that repository's file. Editing it is fine and re-running this offers to
/// overwrite it, which is the honest trade for not having a package to bump.
/// </summary>
[Description("Add the `pks-declare` pipeline step to an Aspire AppHost")]
public sealed class PksAspireInitCommand : AsyncCommand<PksAspireInitCommand.Settings>
{
    private const string ResourceName = "PksDeclare.cs";
    private const string FileName = "PksDeclare.cs";

    private readonly IAnsiConsole _console;

    public PksAspireInitCommand(IAnsiConsole console) => _console = console;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[APPHOST]")]
        [Description("AppHost project file or its directory (default: search from here)")]
        public string? AppHost { get; set; }

        [CommandOption("--force")]
        [Description("Overwrite an existing PksDeclare.cs")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        string directory;
        try
        {
            directory = LocateAppHostDirectory(settings.AppHost);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        var destination = Path.Combine(directory, FileName);
        if (File.Exists(destination) && !settings.Force)
        {
            _console.MarkupLine($"[yellow]{Rel(destination).EscapeMarkup()} already exists.[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace it.[/]");
            return 1;
        }

        var source = ReadEmbedded();
        await File.WriteAllTextAsync(destination, source);

        _console.MarkupLine($"[green]wrote[/] {Rel(destination).EscapeMarkup()}");
        _console.WriteLine();
        _console.MarkupLine("Add one line near the top of [bold]AppHost.cs[/], so the composition can answer");
        _console.MarkupLine("even when the answer is \"nothing\":");
        _console.MarkupLine("[dim]    builder.AddPksDeclare();[/]");
        _console.WriteLine();
        _console.MarkupLine("Declare what the composition needs, next to the parameters that receive it:");
        _console.MarkupLine("[dim]    builder.AddPksCapability(\"chat\", \"The model behind the assistant\")[/]");
        _console.MarkupLine("[dim]           .Offers(\"foundry\", \"Azure AI Foundry\")[/]");
        _console.MarkupLine("[dim]           .Binds(baseUrl, \"{endpoint:openai}\")[/]");
        _console.MarkupLine("[dim]           .Binds(apiKey,  \"{apikey}\")[/]");
        _console.MarkupLine("[dim]           .Binds(model,   \"{model:default}\");[/]");
        _console.WriteLine();
        _console.MarkupLine("Then start it with [bold]pks aspire run[/] instead of [bold]aspire run[/].");
        return 0;
    }

    /// <summary>
    /// Finds the project to write into. A directory with exactly one project file is unambiguous; more
    /// than one is not, and guessing which of two csproj files is the AppHost would be a file written
    /// into the wrong project and a confusing build error somewhere else.
    /// </summary>
    private static string LocateAppHostDirectory(string? hint)
    {
        var start = string.IsNullOrWhiteSpace(hint) ? Directory.GetCurrentDirectory() : Path.GetFullPath(hint);

        if (File.Exists(start))
        {
            return Path.GetDirectoryName(start)!;
        }

        if (!Directory.Exists(start))
        {
            throw new InvalidOperationException($"no such path: {start}");
        }

        var projects = Directory.GetFiles(start, "*.csproj");
        return projects.Length switch
        {
            1 => start,
            0 => throw new InvalidOperationException($"no project file in {start} — point at the AppHost's .csproj"),
            _ => throw new InvalidOperationException($"{projects.Length} project files in {start} — point at the AppHost's .csproj"),
        };
    }

    private static string ReadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded in this build of pks");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Rel(string path)
    {
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }
}
