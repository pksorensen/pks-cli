using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

[Description("Sync files between storage and local directory (download is agent-safe)")]
public class StorageSyncCommand : Command<StorageSyncCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public StorageSyncCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public class Settings : StorageSettings
    {
        [CommandArgument(0, "[local-path]")]
        [Description("Local directory path")]
        public string? LocalPath { get; set; }

        [CommandOption("--provider")]
        [Description("Provider key (e.g. azure-fileshare). Auto-detected if only one is authenticated.")]
        public string? ProviderKey { get; set; }

        [CommandOption("--account")]
        [Description("Storage account name")]
        public string? AccountName { get; set; }

        [CommandOption("--share")]
        [Description("File share name")]
        public string? ShareName { get; set; }

        [CommandOption("-d|--direction")]
        [Description("Sync direction: Download (default), Upload, Bidirectional")]
        public SyncDirection Direction { get; set; } = SyncDirection.Download;

        [CommandOption("--dry-run")]
        [Description("Preview changes without transferring files")]
        public bool DryRun { get; set; }

        [CommandOption("--delete")]
        [Description("Delete orphaned files at the destination (requires interactive confirmation)")]
        public bool Delete { get; set; }

        [CommandOption("--verify-checksum")]
        [Description("Verify file integrity using MD5 checksums")]
        public bool VerifyChecksum { get; set; }

        [CommandOption("--parallel")]
        [Description("Maximum parallel file transfers (default: 4)")]
        public int MaxParallelism { get; set; } = 4;

        [CommandOption("--include")]
        [Description("Glob pattern for files to include, e.g. '*.json' or 'users/**'. Can be repeated.")]
        public string[] Include { get; set; } = [];

        [CommandOption("--exclude")]
        [Description("Glob pattern for files to exclude, e.g. '*.tmp'. Can be repeated.")]
        public string[] Exclude { get; set; } = [];
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var authenticated = (await _registry.GetAuthenticatedProvidersAsync()).ToList();

        if (authenticated.Count == 0)
        {
            _console.MarkupLine("[yellow]No authenticated storage providers found.[/]");
            _console.MarkupLine("[dim]Run [bold]pks fileshare init[/] to authenticate with a provider.[/]");
            return 1;
        }

        // Select provider
        IFileShareProvider provider;
        if (!string.IsNullOrEmpty(settings.ProviderKey))
        {
            var found = authenticated.FirstOrDefault(p => p.ProviderKey == settings.ProviderKey);
            if (found == null)
            {
                _console.MarkupLine($"[red]Provider '[bold]{Markup.Escape(settings.ProviderKey)}[/]' is not authenticated.[/]");
                return 1;
            }
            provider = found;
        }
        else if (authenticated.Count == 1)
        {
            provider = authenticated[0];
        }
        else
        {
            var providerName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a provider:[/]")
                    .AddChoices(authenticated.Select(p => p.ProviderName)));
            provider = authenticated.First(p => p.ProviderName == providerName);
        }

        // Resolve account and share interactively if not provided
        var resources = (await provider.ListResourcesAsync()).ToList();

        var accountName = settings.AccountName;
        var shareName = settings.ShareName;

        if (string.IsNullOrEmpty(shareName) && resources.Count > 0)
        {
            var accounts = resources.Select(r => r.AccountName).Distinct().ToList();
            if (string.IsNullOrEmpty(accountName))
            {
                accountName = accounts.Count == 1
                    ? accounts[0]
                    : _console.Prompt(new SelectionPrompt<string>().Title("[cyan]Select storage account:[/]").AddChoices(accounts));
            }

            var sharesForAccount = resources.Where(r => r.AccountName == accountName).ToList();
            if (sharesForAccount.Count == 1)
            {
                shareName = sharesForAccount[0].ResourceName;
                _console.MarkupLine($"[dim]Using share: [bold]{Markup.Escape(shareName)}[/][/]");
            }
            else if (sharesForAccount.Count > 1)
            {
                shareName = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select a file share:[/]")
                        .AddChoices(sharesForAccount.Select(r => r.ResourceName)));
            }
        }

        if (string.IsNullOrEmpty(accountName) || string.IsNullOrEmpty(shareName))
        {
            _console.MarkupLine("[red]Could not determine storage account or share name.[/]");
            return 1;
        }

        // Resolve local path
        var localPath = settings.LocalPath;
        if (string.IsNullOrEmpty(localPath))
        {
            localPath = _console.Prompt(
                new TextPrompt<string>("[cyan]Local directory path:[/]")
                    .DefaultValue(Path.Combine(Directory.GetCurrentDirectory(), shareName)));
        }

        // Write consent gate — write operations require a human at the keyboard
        var isWriteOperation = settings.Direction is SyncDirection.Upload or SyncDirection.Bidirectional || settings.Delete;
        if (isWriteOperation && !settings.DryRun)
        {
            if (!_console.Profile.Capabilities.Interactive)
            {
                _console.MarkupLine("[red]Write operations require interactive confirmation and cannot run non-interactively.[/]");
                _console.MarkupLine("[dim]Only download (read-only) operations are allowed for automated use.[/]");
                return 1;
            }

            _console.WriteLine();
            _console.MarkupLine("[yellow]Warning: This operation will write to or delete files.[/]");
            _console.MarkupLine($"  Direction : [bold]{settings.Direction}[/]");
            _console.MarkupLine($"  Account   : [bold]{Markup.Escape(accountName)}[/]");
            _console.MarkupLine($"  Share     : [bold]{Markup.Escape(shareName)}[/]");
            _console.MarkupLine($"  Local     : [bold]{Markup.Escape(localPath)}[/]");
            if (settings.Delete)
                _console.MarkupLine("  [red]Delete orphaned files: YES[/]");
            _console.WriteLine();

            if (!_console.Confirm("[cyan]Proceed with write operation?[/]", defaultValue: false))
            {
                _console.MarkupLine("[dim]Cancelled.[/]");
                return 0;
            }
        }

        if (settings.DryRun)
            _console.MarkupLine("[dim]Dry run mode — no files will be transferred[/]");

        var request = new StorageSyncRequest
        {
            ProviderKey = provider.ProviderKey,
            AccountName = accountName,
            ResourceName = shareName,
            LocalDirectory = localPath,
            Direction = settings.Direction,
            DryRun = settings.DryRun,
            Delete = settings.Delete,
            VerifyChecksum = settings.VerifyChecksum,
            MaxParallelism = settings.MaxParallelism,
            Include = settings.Include,
            Exclude = settings.Exclude
        };

        if (settings.Include.Length > 0)
            _console.MarkupLine($"[dim]Include: {string.Join(", ", settings.Include.Select(Markup.Escape))}[/]");
        if (settings.Exclude.Length > 0)
            _console.MarkupLine($"[dim]Exclude: {string.Join(", ", settings.Exclude.Select(Markup.Escape))}[/]");

        SyncResult syncResult = default!;
        await _console.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn() { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                ProgressTask? fileTask = null;

                syncResult = await provider.SyncAsync(request, update =>
                {
                    // Total grows as the producer discovers files — update MaxValue to track it
                    if (update.Total == 0)
                    {
                        // Still discovering, no files yet
                        fileTask ??= ctx.AddTask("[dim]Discovering...[/]", maxValue: 1);
                        fileTask.Description = "[dim]Discovering...[/]";
                        return;
                    }

                    if (fileTask == null)
                        fileTask = ctx.AddTask("[dim]Discovering...[/]", maxValue: update.Total);

                    // Grow MaxValue as more files are discovered
                    if (update.Total > fileTask.MaxValue)
                        fileTask.MaxValue = update.Total;

                    fileTask.Description = $"[cyan]{update.Completed}/{update.Total}[/]  [dim]{Markup.Escape(update.CurrentFile)}[/]";
                    fileTask.Value = update.Completed;
                });

                fileTask?.StopTask();
            });

        // Summary table
        _console.WriteLine();
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .Title(syncResult.Success ? "[bold green]Sync Complete[/]" : "[bold red]Sync Completed with Errors[/]");

        summary.AddColumn("[bold]Metric[/]");
        summary.AddColumn("[bold]Count[/]");

        if (settings.Direction is SyncDirection.Download or SyncDirection.Bidirectional)
            summary.AddRow("Files downloaded", syncResult.FilesDownloaded.ToString());
        if (settings.Direction is SyncDirection.Upload or SyncDirection.Bidirectional)
            summary.AddRow("Files uploaded", syncResult.FilesUploaded.ToString());
        if (settings.Delete)
            summary.AddRow("Files deleted", syncResult.FilesDeleted.ToString());
        summary.AddRow("Files skipped", syncResult.FilesSkipped.ToString());
        summary.AddRow("Bytes transferred", $"{syncResult.BytesTransferred:N0}");
        if (syncResult.Errors.Count > 0)
            summary.AddRow("[red]Errors[/]", syncResult.Errors.Count.ToString());

        _console.Write(summary);

        if (syncResult.Errors.Count > 0)
        {
            _console.MarkupLine("[red]Errors:[/]");
            foreach (var error in syncResult.Errors)
                _console.MarkupLine($"  [red]• {Markup.Escape(error)}[/]");
            return 1;
        }

        return 0;
    }
}
