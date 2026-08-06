using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

/// <summary>
/// Delete files from a storage share. Deletion on Azure Files is permanent — there is no recycle
/// bin — so this command resolves the exact target list first, shows it, and then puts it through
/// <see cref="IActionGuard"/> as a resource-scoped request. An agent without a second factor cannot
/// satisfy that gate in-band; it gets a consent request id and a human runs
/// <c>pks consent approve &lt;id&gt;</c>.
/// </summary>
[Description("Delete files from a storage share (permanent; requires approval)")]
public class StorageRmCommand : Command<StorageRmCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public StorageRmCommand(FileShareProviderRegistry registry, IActionGuard guard, IAnsiConsole console)
    {
        _registry = registry;
        _guard = guard;
        _console = console;
    }

    public class Settings : StorageSettings
    {
        [CommandArgument(0, "<path>")]
        [Description("File or directory path within the share")]
        public string Path { get; set; } = string.Empty;

        [CommandOption("--share")]
        [Description("File share name")]
        public string? ShareName { get; set; }

        [CommandOption("--account")]
        [Description("Storage account name")]
        public string? AccountName { get; set; }

        [CommandOption("-r|--recursive")]
        [Description("Include files in subdirectories")]
        public bool Recursive { get; set; }

        [CommandOption("--dry-run")]
        [Description("Resolve and show the targets without deleting or asking for approval")]
        public bool DryRun { get; set; }

        [CommandOption("--yes")]
        [Description("Skip the final confirmation (does NOT skip approval)")]
        public bool Yes { get; set; }

        [CommandOption("--json")]
        [Description("Output as JSON (agent-friendly)")]
        public bool Json { get; set; }
    }

    /// <summary>Above this, printing every path buries the summary; the count is what matters.</summary>
    private const int MaxListed = 25;

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            _console.MarkupLine("[red]A path is required.[/] [dim]Refusing to delete a whole share.[/]");
            return 1;
        }

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

        var (accountName, shareName) = await ResolveTargetShareAsync(provider, settings);
        if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(shareName))
        {
            _console.MarkupLine("[red]Could not resolve the storage account and share.[/]");
            return 1;
        }

        var targets = await provider.EnumerateFilesAsync(accountName, shareName, settings.Path, settings.Recursive);
        if (targets.Count == 0)
        {
            _console.MarkupLine($"[yellow]Nothing matches[/] [dim]{Markup.Escape(settings.Path)}[/] in {Markup.Escape(shareName)}.");
            if (!settings.Recursive)
                _console.MarkupLine("[dim]If the path is a directory with subdirectories, add --recursive.[/]");
            return 1;
        }

        var totalBytes = targets.Sum(t => t.SizeBytes);
        var paths = targets.Select(t => t.Path).ToList();

        if (settings.Json)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                account = accountName,
                share = shareName,
                path = settings.Path,
                recursive = settings.Recursive,
                dryRun = settings.DryRun,
                files = paths,
                totalBytes,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            _console.MarkupLine($"[bold]{Markup.Escape(accountName)}/{Markup.Escape(shareName)}[/][dim]:{Markup.Escape(settings.Path)}[/]");
            _console.WriteLine();
            foreach (var t in targets.Take(MaxListed))
                _console.MarkupLine($"  [red]delete[/] {Markup.Escape(t.Path)} [dim]{FormatBytes(t.SizeBytes)}[/]");
            if (targets.Count > MaxListed)
                _console.MarkupLine($"  [dim]… and {targets.Count - MaxListed} more[/]");
            _console.WriteLine();
            _console.MarkupLine($"[bold]{targets.Count}[/] file{(targets.Count == 1 ? "" : "s")}, {FormatBytes(totalBytes)}");
        }

        if (settings.DryRun)
        {
            if (!settings.Json)
                _console.MarkupLine("[dim]Dry run — nothing was deleted.[/]");
            return 0;
        }

        // Approval binds to the resolved list above, not to the path pattern: files that appear
        // between approval and execution are outside the grant and force a fresh request.
        var resource = $"{provider.ProviderKey}:{accountName}/{shareName}";
        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.StorageDelete,
                $"Delete {targets.Count} file(s) ({FormatBytes(totalBytes)}) from {accountName}/{shareName}:{settings.Path}",
                CostHint: "Permanent — Azure Files has no recycle bin.",
                Resource: resource,
                Targets: paths));
        }
        catch (ActionGuardDeniedException ex)
        {
            _console.WriteLine();
            _console.MarkupLine($"[red]Delete not approved.[/]");
            foreach (var line in ex.Message.Split('\n'))
                _console.MarkupLine(Markup.Escape(line));
            return 1;
        }

        if (!settings.Yes && _console.Profile.Capabilities.Interactive)
        {
            if (!_console.Confirm($"[red]Permanently delete {targets.Count} file(s)?[/]", defaultValue: false))
            {
                _console.MarkupLine("[yellow]Aborted.[/]");
                return 1;
            }
        }

        var result = await provider.DeleteFilesAsync(accountName, shareName, paths);

        foreach (var error in result.Errors)
            _console.MarkupLine($"[red]•[/] {Markup.Escape(error)}");

        _console.MarkupLine(
            $"[green]Deleted[/] {result.FilesDeleted} file{(result.FilesDeleted == 1 ? "" : "s")} ({FormatBytes(result.BytesDeleted)}).");

        return result.Success ? 0 : 1;
    }

    private async Task<(string Account, string Share)> ResolveTargetShareAsync(IFileShareProvider provider, Settings settings)
    {
        var accountName = settings.AccountName ?? string.Empty;
        var shareName = settings.ShareName ?? string.Empty;
        if (!string.IsNullOrEmpty(accountName) && !string.IsNullOrEmpty(shareName))
            return (accountName, shareName);

        var resources = ((await provider.ListResourcesAsync()) ?? Enumerable.Empty<StorageResource>()).ToList();
        if (resources.Count == 0) return (accountName, shareName);

        if (string.IsNullOrEmpty(accountName))
        {
            var accounts = resources.Select(r => r.AccountName).Distinct().ToList();
            accountName = accounts.Count == 1 ? accounts[0] : _console.Prompt(
                new SelectionPrompt<string>().Title("[cyan]Select account:[/]").AddChoices(accounts));
        }

        if (string.IsNullOrEmpty(shareName))
        {
            var shares = resources.Where(r => r.AccountName == accountName).ToList();
            if (shares.Count == 0) return (accountName, shareName);
            shareName = shares.Count == 1 ? shares[0].ResourceName : _console.Prompt(
                new SelectionPrompt<string>().Title("[cyan]Select share:[/]").AddChoices(shares.Select(r => r.ResourceName)));
        }

        return (accountName, shareName);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };
}
