using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Base command for all devcontainer operations
/// </summary>
public abstract class DevcontainerCommand<T> : Command<T> where T : DevcontainerSettings
{
    protected readonly IAnsiConsole Console;

    protected DevcontainerCommand(IAnsiConsole console)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
    }

    protected void DisplayBanner(string operation)
    {
        var panel = new Panel($"[bold cyan]🐳 PKS Devcontainer {operation}[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);

        Console.Write(panel);
        Console.WriteLine();
    }

    protected void DisplaySuccess(string message)
    {
        Console.MarkupLine($"[green]✓ {message}[/]");
    }

    protected void DisplayError(string message)
    {
        Console.MarkupLine($"[red]✗ {message.EscapeMarkup()}[/]");
    }

    protected void DisplayWarning(string message)
    {
        Console.MarkupLine($"[yellow]⚠ {message.EscapeMarkup()}[/]");
    }

    protected void DisplayInfo(string message)
    {
        Console.MarkupLine($"[cyan]ℹ {message}[/]");
    }

    protected void DisplayProgress(string message)
    {
        Console.MarkupLine($"[dim]  {message}[/]");
    }

    protected bool PromptConfirmation(string message, bool defaultValue = true)
    {
        return Console.Confirm(message, defaultValue);
    }

    protected string PromptText(string message, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>(message);

        if (!string.IsNullOrEmpty(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        return Console.Prompt(prompt);
    }

    protected TItem PromptSelection<TItem>(string message, IEnumerable<TItem> choices) where TItem : notnull
    {
        return Console.Prompt(
            new SelectionPrompt<TItem>()
                .Title(message)
                .AddChoices(choices)
        );
    }

    protected List<TItem> PromptMultiSelection<TItem>(string message, IEnumerable<TItem> choices) where TItem : notnull
    {
        return Console.Prompt(
            new MultiSelectionPrompt<TItem>()
                .Title(message)
                .AddChoices(choices)
        );
    }

    protected void DisplayTable<TItem>(string title, IEnumerable<TItem> items, params (string Header, Func<TItem, string> ValueSelector)[] columns)
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

        Console.Write(table);
    }

    protected void DisplayValidationResults(IEnumerable<string> errors, IEnumerable<string> warnings)
    {
        if (errors.Any())
        {
            Console.WriteLine();
            Console.MarkupLine("[red]Validation Errors:[/]");
            foreach (var error in errors)
            {
                Console.MarkupLine($"[red]  • {error}[/]");
            }
        }

        if (warnings.Any())
        {
            Console.WriteLine();
            Console.MarkupLine("[yellow]Validation Warnings:[/]");
            foreach (var warning in warnings)
            {
                Console.MarkupLine($"[yellow]  • {warning}[/]");
            }
        }
    }

    protected void DisplayGeneratedFiles(IEnumerable<string> files)
    {
        if (files.Any())
        {
            Console.WriteLine();
            Console.MarkupLine("[cyan]Generated Files:[/]");
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
                Console.MarkupLine($"[dim]  • {relativePath}[/]");
            }
        }
    }

    protected async Task WithSpinnerAsync(string message, Func<Task> operation)
    {
        await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                await operation();
            });
    }

    protected async Task<TResult> WithSpinnerAsync<TResult>(string message, Func<Task<TResult>> operation)
    {
        return await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, async _ =>
            {
                return await operation();
            });
    }

    protected async Task WithSpinnerAsync(string message, Func<StatusContext, Task> operation)
    {
        await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, operation);
    }

    protected async Task<TResult> WithSpinnerAsync<TResult>(string message, Func<StatusContext, Task<TResult>> operation)
    {
        return await Console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync(message, operation);
    }

    protected void DisplayFeatureTable(IEnumerable<(string Id, string Name, string Description, string Category)> features)
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

        Console.Write(table);
    }

    protected void DisplayExtensionTable(IEnumerable<(string Id, string Name, string Publisher, string Description)> extensions)
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

        Console.Write(table);
    }

    protected void DisplayConfigurationSummary(string name, string? image, IEnumerable<string> features, IEnumerable<string> extensions, IEnumerable<int> ports)
    {
        var table = new Table()
            .Title("[cyan]Configuration Summary[/]")
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Setting").Width(20))
            .AddColumn(new TableColumn("Value").NoWrap());

        table.AddRow("[yellow]Name[/]", $"[white]{name}[/]");
        table.AddRow("[yellow]Base Image[/]", $"[dim]{image ?? "N/A"}[/]");

        var featuresList = features.ToList();
        if (featuresList.Any())
        {
            var featuresText = featuresList.Count <= 3
                ? string.Join(", ", featuresList)
                : $"{string.Join(", ", featuresList.Take(3))} and {featuresList.Count - 3} more";
            table.AddRow("[yellow]Features[/]", $"[green]{featuresText}[/]");
        }
        else
        {
            table.AddRow("[yellow]Features[/]", "[dim]None[/]");
        }

        var extensionsList = extensions.ToList();
        if (extensionsList.Any())
        {
            var extensionsText = extensionsList.Count <= 3
                ? string.Join(", ", extensionsList)
                : $"{string.Join(", ", extensionsList.Take(3))} and {extensionsList.Count - 3} more";
            table.AddRow("[yellow]Extensions[/]", $"[blue]{extensionsText}[/]");
        }
        else
        {
            table.AddRow("[yellow]Extensions[/]", "[dim]None[/]");
        }

        var portsList = ports.ToList();
        if (portsList.Any())
        {
            table.AddRow("[yellow]Forwarded Ports[/]", $"[cyan]{string.Join(", ", portsList)}[/]");
        }
        else
        {
            table.AddRow("[yellow]Forwarded Ports[/]", "[dim]None[/]");
        }

        Console.Write(table);
    }

    protected string ValidateAndResolvePath(string path)
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