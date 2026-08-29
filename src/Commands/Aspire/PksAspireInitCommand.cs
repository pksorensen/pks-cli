using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Aspire;

/// <summary>
/// Puts the AppHost half of `pks aspire run` into a project.
///
/// It adds one package reference, <c>Agentics.Extensions.Aspire.Declare</c>, and nothing else. The
/// package holds a plain Aspire extension: it does not reference pks-cli and does not talk to it, so
/// an AppHost that takes it still builds and runs on a machine that has never heard of pks.
///
/// This shipped as a copied source file until 2026-08-29, because an AppHost taking a dependency on
/// the CLI was never on the table. A separate package is not that dependency, and the copy had the
/// cost a copy always has: a change to the file reached only the repositories somebody remembered to
/// re-run this command in. `--source` still writes the file, for an AppHost that genuinely cannot
/// take the package — and it writes the same source the package is built from, so the two cannot
/// drift into two dialects.
/// </summary>
[Description("Add the Agentics declare step to an Aspire AppHost")]
public sealed class PksAspireInitCommand : AsyncCommand<PksAspireInitCommand.Settings>
{
    private const string ResourceName = "AgenticsDeclare.cs";
    private const string FileName = "AgenticsDeclare.cs";

    /// <summary>The package that carries what this command used to copy.</summary>
    public const string PackageId = "Agentics.Extensions.Aspire.Declare";

    /// <summary>The file this command wrote before the package existed. An AppHost that still has it
    /// must not also reference the package: <c>SuggestedValue</c> would be defined twice and the
    /// declare step registered twice.</summary>
    private const string LegacyFileName = "PksDeclare.cs";

    private readonly IAnsiConsole _console;

    public PksAspireInitCommand(IAnsiConsole console) => _console = console;

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[APPHOST]")]
        [Description("AppHost project file or its directory (default: search from here)")]
        public string? AppHost { get; set; }

        [CommandOption("--source")]
        [Description($"Write {FileName} into the project instead of referencing the package")]
        public bool Source { get; set; }

        [CommandOption("--force")]
        [Description($"Overwrite an existing {FileName} (with --source)")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        string directory;
        try
        {
            directory = LocateAppHostDirectory(settings.AppHost);
        }
        catch (SingleFileAppHostException ex)
        {
            // `apphost.cs` with no project file. There is nothing to add a PackageReference to, and
            // editing somebody's single-file AppHost to insert a directive is more than this command
            // should presume. The line is short enough to hand over.
            _console.MarkupLine($"[yellow]{Rel(ex.Path).EscapeMarkup()} is a single-file AppHost — no project to add a reference to.[/]");
            _console.WriteLine();
            _console.MarkupLine("Add these two lines at the top of it yourself:");
            _console.MarkupLine($"[dim]    #:package {PackageId}@*[/]");
            _console.MarkupLine("[dim]    builder.AddAgenticsDeclare();[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        var legacy = Path.Combine(directory, LegacyFileName);
        if (File.Exists(legacy))
        {
            _console.MarkupLine($"[yellow]{Rel(legacy).EscapeMarkup()} is the old copied file.[/]");
            _console.WriteLine();
            _console.MarkupLine("Delete it before referencing the package — with both in place, [bold]SuggestedValue[/] is");
            _console.MarkupLine("defined twice and the declare step is registered twice. Then rename the call sites:");
            _console.MarkupLine("[dim]    AddPksDeclare              -> AddAgenticsDeclare[/]");
            _console.MarkupLine("[dim]    AddPksCapability           -> AddAgenticsCapability[/]");
            _console.MarkupLine("[dim]    PksDeclareExtensions       -> AgenticsDeclareExtensions[/]");
            return 1;
        }

        return settings.Source
            ? await WriteSourceAsync(directory, settings.Force)
            : await AddPackageAsync(directory);
    }

    /// <summary>
    /// Hands the reference to `dotnet add package`, without a version.
    ///
    /// Deliberately without one: pks and the package release on their own schedules now, so a version
    /// baked in here would be whatever was newest when this build of pks was cut, and would go stale
    /// on the shelf. Letting NuGet pick keeps a fresh `pks aspire init` correct in a year.
    /// </summary>
    private async Task<int> AddPackageAsync(string directory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("add");
        start.ArgumentList.Add("package");
        start.ArgumentList.Add(PackageId);

        using var process = Process.Start(start);
        if (process is null)
        {
            _console.MarkupLine("[red]could not run dotnet[/]");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _console.MarkupLine($"[red]dotnet add package {PackageId} failed.[/]");
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                _console.WriteLine(detail.Trim());
            }

            _console.WriteLine();
            _console.MarkupLine("[dim]Offline, or the AppHost cannot take the package? [bold]--source[/] writes the file instead.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]referenced[/] {PackageId}");
        WriteNextSteps();
        return 0;
    }

    private async Task<int> WriteSourceAsync(string directory, bool force)
    {
        var destination = Path.Combine(directory, FileName);
        if (File.Exists(destination) && !force)
        {
            _console.MarkupLine($"[yellow]{Rel(destination).EscapeMarkup()} already exists.[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace it.[/]");
            return 1;
        }

        await File.WriteAllTextAsync(destination, ReadEmbedded());

        _console.MarkupLine($"[green]wrote[/] {Rel(destination).EscapeMarkup()}");
        _console.MarkupLine($"[dim]This copy is now that repository's file — a fix here will not reach it. Prefer the package.[/]");
        WriteNextSteps();
        return 0;
    }

    private void WriteNextSteps()
    {
        _console.WriteLine();
        _console.MarkupLine("Add one line near the top of [bold]AppHost.cs[/], so the composition can answer");
        _console.MarkupLine("even when the answer is \"nothing\":");
        _console.MarkupLine("[dim]    builder.AddAgenticsDeclare();[/]");
        _console.WriteLine();
        _console.MarkupLine("Declare what the composition needs, next to the parameters that receive it:");
        _console.MarkupLine("[dim]    builder.AddAgenticsCapability(\"chat\", \"The model behind the assistant\")[/]");
        _console.MarkupLine("[dim]           .Offers(\"foundry\", \"Azure AI Foundry\")[/]");
        _console.MarkupLine("[dim]           .Binds(baseUrl, \"{endpoint:openai}\")[/]");
        _console.MarkupLine("[dim]           .Binds(apiKey,  \"{apikey}\")[/]");
        _console.MarkupLine("[dim]           .Binds(model,   \"{model:default}\");[/]");
        _console.WriteLine();
        _console.MarkupLine("Then start it with [bold]pks aspire run[/] instead of [bold]aspire run[/].");
        _console.MarkupLine("[dim]Started the plain way, the dashboard will say so — [bold]PKS_ASPIRE_NO_REMINDER=1[/] silences it.[/]");
    }

    /// <summary>A directory whose AppHost is `apphost.cs` rather than a project.</summary>
    private sealed class SingleFileAppHostException(string path) : Exception
    {
        public string Path { get; } = path;
    }

    /// <summary>
    /// Finds the project to work on. A directory with exactly one project file is unambiguous; more
    /// than one is not, and guessing which of two csproj files is the AppHost would be a reference
    /// added to the wrong project and a confusing build error somewhere else.
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
        if (projects.Length == 1)
        {
            return start;
        }

        if (projects.Length == 0)
        {
            var singleFile = Directory.GetFiles(start, "*apphost*.cs")
                .FirstOrDefault(f => !Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (singleFile is not null)
            {
                throw new SingleFileAppHostException(singleFile);
            }

            throw new InvalidOperationException($"no project file in {start} — point at the AppHost's .csproj");
        }

        throw new InvalidOperationException($"{projects.Length} project files in {start} — point at the AppHost's .csproj");
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
