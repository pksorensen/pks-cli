using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Persona;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Commands.Persona;

public class PersonaLintSettings : PersonaSettings
{
    [CommandArgument(0, "[path]")]
    [Description("File or folder to lint. Folder recurses over personas/<locale>/<slug>/<slug>.md. Defaults to personas/ in the current tree.")]
    public string? Path { get; set; }

    [CommandOption("--locale")]
    [Description("Lint a single locale when no path is given. Default: all locales under personas/.")]
    public string? Locale { get; set; }

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of the human table.")]
    public bool Json { get; set; }
}

public class PersonaLintCommand : AsyncCommand<PersonaLintSettings>
{
    private readonly IPersonaPathResolver _paths;
    private readonly IPersonaLinter _linter;

    public PersonaLintCommand(IPersonaPathResolver paths, IPersonaLinter linter)
    {
        _paths = paths;
        _linter = linter;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, PersonaLintSettings settings)
    {
        var targets = ResolveTargets(settings);
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]![/] No persona files found.");
            return 0;
        }

        var results = new List<PersonaLintResult>(targets.Count);
        foreach (var t in targets)
        {
            results.Add(await _linter.LintAsync(t));
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = results.All(r => r.Ok),
                count = results.Count,
                errorCount = results.Sum(r => r.Errors.Count),
                warningCount = results.Sum(r => r.Warnings.Count),
                results = results.Select(r => new
                {
                    sourcePath = r.SourcePath,
                    ok = r.Ok,
                    errors = r.Errors,
                    warnings = r.Warnings,
                }),
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return results.Any(r => !r.Ok) ? 1 : 0;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks persona lint[/] [grey]({results.Count} file{(results.Count == 1 ? "" : "s")})[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        var totalErrors = 0;
        var totalWarnings = 0;
        foreach (var r in results)
        {
            totalErrors += r.Errors.Count;
            totalWarnings += r.Warnings.Count;
            RenderFile(r);
        }

        AnsiConsole.WriteLine();
        var color = totalErrors == 0 ? (totalWarnings == 0 ? "green" : "yellow") : "red";
        AnsiConsole.MarkupLine($"[{color}]{totalErrors}[/] error{(totalErrors == 1 ? "" : "s")}, [yellow]{totalWarnings}[/] warning{(totalWarnings == 1 ? "" : "s")} across {results.Count} file{(results.Count == 1 ? "" : "s")}.");
        return totalErrors > 0 ? 1 : 0;
    }

    private List<string> ResolveTargets(PersonaLintSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Path))
        {
            var full = System.IO.Path.GetFullPath(settings.Path);
            if (System.IO.File.Exists(full)) return new List<string> { full };
            if (System.IO.Directory.Exists(full)) return EnumPersonas(full);
            return new List<string>();
        }

        var root = _paths.ResolvePersonasRoot(System.IO.Directory.GetCurrentDirectory());
        if (root is null) return new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.Locale))
            return EnumPersonas(_paths.PersonasLocaleDir(root, settings.Locale));

        return EnumPersonas(root);
    }

    private static List<string> EnumPersonas(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return new List<string>();
        var results = new List<string>();
        foreach (var slugDir in System.IO.Directory.EnumerateDirectories(dir, "*", System.IO.SearchOption.AllDirectories))
        {
            var slug = System.IO.Path.GetFileName(slugDir);
            if (string.IsNullOrEmpty(slug) || slug.StartsWith("_", StringComparison.Ordinal)) continue;
            var md = System.IO.Path.Combine(slugDir, slug + ".md");
            if (System.IO.File.Exists(md)) results.Add(md);
        }
        return results
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static void RenderFile(PersonaLintResult r)
    {
        var rel = System.IO.Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), r.SourcePath);
        if (r.Errors.Count == 0 && r.Warnings.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] [grey]{Markup.Escape(rel)}[/]");
            return;
        }

        var icon = r.Errors.Count > 0 ? "[red]✗[/]" : "[yellow]●[/]";
        AnsiConsole.MarkupLine($"{icon} [bold]{Markup.Escape(rel)}[/]  [red]{r.Errors.Count}E[/] [yellow]{r.Warnings.Count}W[/]");

        foreach (var e in r.Errors)
        {
            AnsiConsole.MarkupLine($"  [red]error[/]  [grey]{Markup.Escape(e.Code)}[/]  [bold]{Markup.Escape(e.Field)}[/]  {Markup.Escape(e.Message)}");
        }
        foreach (var w in r.Warnings)
        {
            AnsiConsole.MarkupLine($"  [yellow]warn[/]   [grey]{Markup.Escape(w.Code)}[/]  [bold]{Markup.Escape(w.Field)}[/]  {Markup.Escape(w.Message)}");
        }
    }
}
