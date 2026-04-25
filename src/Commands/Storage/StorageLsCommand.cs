using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

[Description("List files and directories in a storage share (agent-safe)")]
public class StorageLsCommand : Command<StorageLsCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public StorageLsCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public class Settings : StorageSettings
    {
        [CommandArgument(0, "[path]")]
        [Description("Path within the share (default: /)")]
        public string? Path { get; set; }

        [CommandOption("--share")]
        [Description("File share name")]
        public string? ShareName { get; set; }

        [CommandOption("--account")]
        [Description("Storage account name")]
        public string? AccountName { get; set; }

        [CommandOption("--limit")]
        [Description("Maximum items to return (default: 100)")]
        public int Limit { get; set; } = 100;

        [CommandOption("--count")]
        [Description("Show item count per directory (extra API calls)")]
        public bool IncludeCount { get; set; }

        [CommandOption("--dirs-only")]
        [Description("Only show directories")]
        public bool DirsOnly { get; set; }

        [CommandOption("--json")]
        [Description("Output as JSON (agent-friendly)")]
        public bool Json { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var authenticated = (await _registry.GetAuthenticatedProvidersAsync()).ToList();

        if (authenticated.Count == 0)
        {
            _console.MarkupLine("[yellow]No authenticated storage providers found.[/]");
            _console.MarkupLine("[dim]Run [bold]pks fileshare init[/] to authenticate.[/]");
            return 1;
        }

        var provider = authenticated.Count == 1
            ? authenticated[0]
            : authenticated.First(p => p.ProviderName == _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a provider:[/]")
                    .AddChoices(authenticated.Select(p => p.ProviderName))));

        // Resolve share
        var resources = ((await provider.ListResourcesAsync()) ?? Enumerable.Empty<StorageResource>()).ToList();
        var accountName = settings.AccountName ?? string.Empty;
        var shareName = settings.ShareName ?? string.Empty;

        if (string.IsNullOrEmpty(shareName) && resources.Count > 0)
        {
            var accounts = resources.Select(r => r.AccountName).Distinct().ToList();
            if (string.IsNullOrEmpty(accountName))
                accountName = accounts.Count == 1 ? accounts[0] : _console.Prompt(
                    new SelectionPrompt<string>().Title("[cyan]Select account:[/]").AddChoices(accounts));

            var shares = resources.Where(r => r.AccountName == accountName).ToList();
            shareName = shares.Count == 1 ? shares[0].ResourceName : _console.Prompt(
                new SelectionPrompt<string>().Title("[cyan]Select share:[/]").AddChoices(shares.Select(r => r.ResourceName)));
        }

        var request = new StorageListRequest
        {
            Path = string.IsNullOrEmpty(settings.Path) ? "/" : settings.Path,
            Limit = settings.Limit,
            IncludeCount = settings.IncludeCount,
            DirsOnly = settings.DirsOnly
        };

        var result = await provider.ListDirectoryAsync(accountName, shareName, request);

        if (settings.Json)
        {
            var json = JsonSerializer.Serialize(new
            {
                share = result.ShareName,
                path = result.Path,
                items = result.Items.Select(i => new
                {
                    type = i.Type.ToString().ToLowerInvariant(),
                    name = i.Name,
                    sizeBytes = i.SizeBytes,
                    itemCount = i.ItemCount
                }),
                returned = result.Items.Count,
                truncated = result.Truncated
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            _console.WriteLine(json);
            return 0;
        }

        // Human-readable output
        _console.MarkupLine($"[bold]{Markup.Escape(result.ShareName)}[/][dim]:{Markup.Escape(result.Path)}[/]");
        _console.WriteLine();

        var dirs = result.Items.Where(i => i.Type == StorageItemType.Directory).ToList();
        var files = result.Items.Where(i => i.Type == StorageItemType.File).ToList();

        foreach (var dir in dirs)
        {
            var countStr = dir.ItemCount.HasValue ? $" [dim]({dir.ItemCount} items)[/]" : string.Empty;
            _console.MarkupLine($"  [cyan]DIR[/]  {Markup.Escape(dir.Name)}/{countStr}");
        }

        foreach (var file in files)
        {
            var size = file.SizeBytes.HasValue ? $" [dim]{FormatBytes(file.SizeBytes.Value)}[/]" : string.Empty;
            _console.MarkupLine($"       {Markup.Escape(file.Name)}{size}");
        }

        _console.WriteLine();

        if (result.Truncated)
            _console.MarkupLine($"[yellow]Results truncated at {settings.Limit} items. Use --limit N or specify a narrower --path.[/]");

        _console.MarkupLine($"[dim]{dirs.Count} director{(dirs.Count == 1 ? "y" : "ies")}, {files.Count} file{(files.Count == 1 ? "" : "s")}[/]");

        return 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
