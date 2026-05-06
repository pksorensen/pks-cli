using System.Reflection;
using System.Text;
using PKS.Attributes;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Tools;

public class ToolsPublishCommand : AsyncCommand<ToolsSettings>
{
    private readonly IAnsiConsole _console;

    public ToolsPublishCommand(IAnsiConsole console) => _console = console;

    public override async Task<int> ExecuteAsync(CommandContext context, ToolsSettings settings)
    {
        var exportedTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<ToolRegistryExportAttribute>()))
            .Where(x => x.Attr != null)
            .OrderBy(x => x.Attr!.Slug)
            .ToList();

        if (exportedTypes.Count == 0)
        {
            _console.MarkupLine("[yellow]No commands tagged with [cyan][ToolRegistryExport][/] found.[/]");
            return 0;
        }

        _console.Write(new Rule("[bold cyan]Tool Registry Export[/]").RuleStyle("cyan dim"));
        _console.WriteLine();
        _console.MarkupLine($"[dim]Found {exportedTypes.Count} tagged command(s).[/]");
        _console.WriteLine();

        var registryRoot = FindRegistryRoot();
        if (registryRoot != null)
            _console.MarkupLine($"[dim]Output:[/] [cyan]{registryRoot.EscapeMarkup()}[/]");
        else
            _console.MarkupLine("[yellow]tools-registry/ not found in parent directories — printing to stdout.[/]");
        _console.WriteLine();

        var results = new List<(string Slug, string Path, bool Written)>();

        foreach (var (type, attr) in exportedTypes)
        {
            var markdown = GenerateMarkdown(attr!);
            var slug = attr!.Slug.Trim('/');

            if (registryRoot != null)
            {
                var leaf = slug.Split('/')[^1];
                var dir = Path.Combine(registryRoot, Path.Combine(slug.Split('/')));
                Directory.CreateDirectory(dir);
                var filePath = Path.Combine(dir, $"{leaf}.md");
                await File.WriteAllTextAsync(filePath, markdown);
                results.Add((slug, filePath, true));
            }
            else
            {
                _console.MarkupLine($"[bold]── {slug} ──[/]");
                _console.WriteLine(markdown);
                results.Add((slug, "(stdout)", false));
            }
        }

        if (registryRoot != null)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Green)
                .AddColumn("[dim]Slug[/]")
                .AddColumn("[dim]File[/]")
                .AddColumn(new TableColumn("[dim]Status[/]").Centered());

            foreach (var (slug, path, written) in results)
                table.AddRow(slug.EscapeMarkup(), path.EscapeMarkup(), written ? "[green]written[/]" : "[yellow]stdout[/]");

            _console.Write(table);
            _console.WriteLine();
            _console.MarkupLine("[green]Done.[/] Commit the new files and deploy to publish to [cyan]agentics.dk/tools[/].");
        }

        return 0;
    }

    private static string? FindRegistryRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "tools-registry");
            if (Directory.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        return null;
    }

    private static string GenerateMarkdown(ToolRegistryExportAttribute attr)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(attr.Title)}\"");
        sb.AppendLine($"description: \"{EscapeYaml(attr.Description)}\"");

        if (attr.Tags.Length > 0)
        {
            sb.Append("tags: [");
            sb.Append(string.Join(", ", attr.Tags.Select(t => $"\"{t}\"")));
            sb.AppendLine("]");
        }

        sb.AppendLine("category: \"ai-tools\"");
        sb.AppendLine("platform: [\"linux\", \"macos\"]");

        if (!string.IsNullOrEmpty(attr.Icon))
            sb.AppendLine($"icon: \"{attr.Icon}\"");

        sb.AppendLine($"status: \"{attr.Status}\"");
        sb.AppendLine("type: \"cli\"");
        sb.AppendLine("author: \"Poul Kjeldager\"");
        sb.AppendLine("component: \"pks\"");

        if (!string.IsNullOrEmpty(attr.Usage))
            sb.AppendLine($"usage: \"{EscapeYaml(attr.Usage)}\"");

        if (attr.Examples.Length > 0)
        {
            sb.AppendLine("examples:");
            foreach (var ex in attr.Examples)
                sb.AppendLine($"  - command: \"{EscapeYaml(ex)}\"");
        }

        sb.AppendLine("---");
        sb.AppendLine();

        // Markdown body
        sb.AppendLine($"# {attr.Title}");
        sb.AppendLine();
        sb.AppendLine(attr.Description);

        if (!string.IsNullOrEmpty(attr.Usage))
        {
            sb.AppendLine();
            sb.AppendLine("## Usage");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(attr.Usage);
            sb.AppendLine("```");
        }

        if (attr.Examples.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Examples");
            sb.AppendLine();
            sb.AppendLine("```bash");
            foreach (var ex in attr.Examples)
                sb.AppendLine(ex);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static string EscapeYaml(string s) => s.Replace("\"", "\\\"");
}
