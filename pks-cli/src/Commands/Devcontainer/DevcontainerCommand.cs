using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Base command for all devcontainer operations
/// </summary>
public abstract class DevcontainerCommand<T> : Command<T> where T : DevcontainerSettings
{
    protected static void DisplayBanner(string operation)
    {
        var panel = new Panel($"[bold cyan]🐳 PKS Devcontainer {operation}[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    protected static void DisplaySuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]✓ {message}[/]");
    }

    protected static void DisplayError(string message)
    {
        AnsiConsole.MarkupLine($"[red]✗ {message}[/]");
    }

    protected static void DisplayWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]⚠ {message}[/]");
    }

    protected static void DisplayInfo(string message)
    {
        AnsiConsole.MarkupLine($"[cyan]ℹ {message}[/]");
    }

    protected static void DisplayProgress(string message)
    {
        AnsiConsole.MarkupLine($"[dim]  {message}[/]");
    }

    protected static bool PromptConfirmation(string message, bool defaultValue = true)
    {
        return AnsiConsole.Confirm(message, defaultValue);
    }

    protected static string PromptText(string message, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>(message);
        
        if (!string.IsNullOrEmpty(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        return AnsiConsole.Prompt(prompt);
    }

    protected static T PromptSelection<T>(string message, IEnumerable<T> choices) where T : notnull
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<T>()
                .Title(message)
                .AddChoices(choices)
        );
    }

    protected static List<T> PromptMultiSelection<T>(string message, IEnumerable<T> choices) where T : notnull
    {
        return AnsiConsole.Prompt(
            new MultiSelectionPrompt<T>()
                .Title(message)
                .AddChoices(choices)
        );
    }

    protected static void DisplayTable<T>(string title, IEnumerable<T> items, params (string Header, Func<T, string> ValueSelector)[] columns)
    {
        var table = new Table()
            .Title(title)
            .Border(TableBorder.Rounded);

        foreach (var (header, _) in columns)
        {
            table.AddColumn(header);
        }

        foreach (var item in items)
        {
            var values = columns.Select(col => col.ValueSelector(item)).ToArray();
            table.AddRow(values);
        }

        AnsiConsole.Write(table);
    }

    protected static void DisplayValidationResults(IEnumerable<string> errors, IEnumerable<string> warnings)
    {
        if (errors.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Validation Errors:[/]");
            foreach (var error in errors)
            {
                AnsiConsole.MarkupLine($"[red]  • {error}[/]");
            }
        }

        if (warnings.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Validation Warnings:[/]");
            foreach (var warning in warnings)
            {
                AnsiConsole.MarkupLine($"[yellow]  • {warning}[/]");
            }
        }
    }

    protected static void DisplayGeneratedFiles(IEnumerable<string> files)
    {
        if (files.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Generated Files:[/]");
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
                AnsiConsole.MarkupLine($"[dim]  • {relativePath}[/]");
            }
        }
    }

    protected static async Task WithSpinnerAsync(string message, Func<Task> operation)
    {
        await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                await operation();
            });
    }

    protected static async Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> operation)
    {
        return await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                return await operation();
            });
    }

    protected static void DisplayFeatureTable(IEnumerable<(string Id, string Name, string Description, string Category)> features)
    {
        var table = new Table()
            .Title("[cyan]Available Features[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name") 
            .AddColumn("Description")
            .AddColumn("Category");

        foreach (var (id, name, description, category) in features)
        {
            var truncatedDescription = description.Length > 50 
                ? description[..47] + "..."
                : description;
                
            table.AddRow(
                $"[yellow]{id}[/]",
                $"[white]{name}[/]",
                $"[dim]{truncatedDescription}[/]",
                $"[cyan]{category}[/]"
            );
        }

        AnsiConsole.Write(table);
    }

    protected static void DisplayExtensionTable(IEnumerable<(string Id, string Name, string Publisher, string Description)> extensions)
    {
        var table = new Table()
            .Title("[cyan]VS Code Extensions[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Publisher")
            .AddColumn("Description");

        foreach (var (id, name, publisher, description) in extensions)
        {
            var truncatedDescription = description.Length > 40 
                ? description[..37] + "..."
                : description;
                
            table.AddRow(
                $"[yellow]{id}[/]",
                $"[white]{name}[/]",
                $"[green]{publisher}[/]",
                $"[dim]{truncatedDescription}[/]"
            );
        }

        AnsiConsole.Write(table);
    }

    protected static void DisplayConfigurationSummary(string name, string? image, IEnumerable<string> features, IEnumerable<string> extensions, IEnumerable<int> ports)
    {
        var panel = new Panel(
            new Rows(
                new Text($"Name: {name}"),
                new Text($"Base Image: {image ?? "N/A"}"),
                new Text($"Features: {features.Count()}"),
                new Text($"Extensions: {extensions.Count()}"),
                new Text($"Forwarded Ports: {string.Join(", ", ports)}")
            )
        )
        .Header("[cyan]Configuration Summary[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);
    }

    protected static string ValidateAndResolvePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be empty");
        }

        // Convert relative paths to absolute
        var resolvedPath = Path.IsPathFullyQualified(path) 
            ? path 
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));

        // Ensure the directory exists
        var directory = Directory.Exists(resolvedPath) ? resolvedPath : Path.GetDirectoryName(resolvedPath);
        
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (PromptConfirmation($"Directory '{directory}' does not exist. Create it?"))
            {
                Directory.CreateDirectory(directory);
                DisplayInfo($"Created directory: {directory}");
            }
            else
            {
                throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
            }
        }

        return resolvedPath;
    }

}