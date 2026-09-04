using System.ComponentModel;
using System.Globalization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Email;

[Description("Export emails from Microsoft Graph to local files")]
public class EmailExportCommand : Command<EmailExportCommand.Settings>
{
    private readonly IMsGraphAuthenticationService _authService;
    private readonly IMsGraphEmailService _emailService;
    private readonly IMsGraphEmailExportService _exportService;
    private readonly IAnsiConsole _console;

    public EmailExportCommand(
        IMsGraphAuthenticationService authService,
        IMsGraphEmailService emailService,
        IMsGraphEmailExportService exportService,
        IAnsiConsole console)
    {
        _authService = authService;
        _emailService = emailService;
        _exportService = exportService;
        _console = console;
    }

    public class Settings : EmailSettings
    {
        [CommandOption("-o|--output <PATH>")]
        [Description("Output directory")]
        [DefaultValue(".emails")]
        public string OutputDirectory { get; set; } = ".emails";

        [CommandOption("--after <DATE>")]
        [Description("Only emails after this date (yyyy-MM-dd)")]
        public string? After { get; set; }

        [CommandOption("--before <DATE>")]
        [Description("Only emails before this date (yyyy-MM-dd)")]
        public string? Before { get; set; }

        [CommandOption("--from <EMAIL>")]
        [Description("Filter by sender email")]
        public string? From { get; set; }

        [CommandOption("--subject <TEXT>")]
        [Description("Filter by subject contains")]
        public string? Subject { get; set; }

        [CommandOption("--folder <NAME>")]
        [Description("Mail folder, or 'all' for the whole mailbox: every folder including archive, subfolders, deleted items and junk")]
        [DefaultValue("all")]
        public string Folder { get; set; } = "all";

        [CommandOption("-m|--mailbox <UPN>")]
        [Description("Mailbox to read. Defaults to your own; any other requires FullAccess on it")]
        public string? Mailbox { get; set; }

        [CommandOption("--layout <LAYOUT>")]
        [Description("thread (one directory per conversation, default) or date")]
        [DefaultValue("thread")]
        public string Layout { get; set; } = "thread";

        [CommandOption("--include-inline")]
        [Description("Also save inline attachments — signature logos and similar")]
        public bool IncludeInline { get; set; }

        [CommandOption("--max-attachment-size <MB>")]
        [Description("Skip attachments larger than this (default 25)")]
        [DefaultValue(25)]
        public int MaxAttachmentSizeMb { get; set; } = 25;

        [CommandOption("--max-total-size <MB>")]
        [Description("Total attachment budget for one run in MB. 0 = no limit (default 500)")]
        [DefaultValue(500)]
        public int MaxTotalSizeMb { get; set; } = 500;

        [CommandOption("--allow-types <TYPES>")]
        [Description("Comma-separated content types to save, or 'all'. Default is a safe list")]
        public string? AllowTypes { get; set; }

        [Description("Re-fetch attachments for messages whose manifest shows something held back")]
        [CommandOption("--retry-held-back")]
        public bool RetryHeldBack { get; set; }

        [Description("Drop attachments with an unrecognised content type instead of storing them as an inert .bin")]
        [CommandOption("--skip-unknown-types")]
        public bool SkipUnknownTypes { get; set; }

        [Description("Re-export messages already recorded in this output directory")]
        [CommandOption("--no-resume")]
        public bool NoResume { get; set; }

        [Description("Only fetch messages newer than the newest one from a previous run (fast, but blind to older mail filed since)")]
        [CommandOption("--incremental")]
        public bool Incremental { get; set; }

        [Description("Do not write a .gitignore into the output directory")]
        [CommandOption("--no-gitignore")]
        public bool NoGitignore { get; set; }

        [CommandOption("--no-message-headers")]
        [Description("Do not request internetMessageHeaders (drops cross-mailbox threading)")]
        public bool NoMessageHeaders { get; set; }

        [CommandOption("--max <COUNT>")]
        [Description("Maximum emails to export")]
        public int? MaxMessages { get; set; }

        [CommandOption("--no-attachments")]
        [Description("Skip attachment downloads")]
        public bool NoAttachments { get; set; }

        [CommandOption("--overwrite")]
        [Description("Overwrite existing exports")]
        public bool Overwrite { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated. Run [bold]pks ms-graph register[/] first.[/]");
            return 1;
        }

        DateTime? after = null;
        DateTime? before = null;

        if (!string.IsNullOrEmpty(settings.After))
        {
            if (!DateTime.TryParseExact(settings.After, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedAfter))
            {
                _console.MarkupLine("[red]Invalid --after date format. Use yyyy-MM-dd.[/]");
                return 1;
            }
            after = parsedAfter;
        }

        if (!string.IsNullOrEmpty(settings.Before))
        {
            if (!DateTime.TryParseExact(settings.Before, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedBefore))
            {
                _console.MarkupLine("[red]Invalid --before date format. Use yyyy-MM-dd.[/]");
                return 1;
            }
            before = parsedBefore;
        }

        if (!Enum.TryParse<EmailExportLayout>(settings.Layout, ignoreCase: true, out var layout))
        {
            _console.MarkupLine("[red]Invalid --layout. Use 'thread' or 'date'.[/]");
            return 1;
        }

        var query = new MsGraphEmailQuery
        {
            Folder = settings.Folder,
            After = after,
            Before = before,
            From = settings.From,
            Subject = settings.Subject,
            MaxMessages = settings.MaxMessages,
            Mailbox = settings.Mailbox,
            IncludeMessageHeaders = !settings.NoMessageHeaders
        };

        var exportOptions = new MsGraphEmailExportOptions
        {
            Query = query,
            OutputDirectory = settings.OutputDirectory,
            DownloadAttachments = !settings.NoAttachments,
            OverwriteExisting = settings.Overwrite,
            Layout = layout,
            WriteGitignore = !settings.NoGitignore,
            Resume = !settings.NoResume,
            RetryHeldBack = settings.RetryHeldBack,
            SkipUnknownTypes = settings.SkipUnknownTypes,
            Incremental = settings.Incremental,
            MailboxAddress = settings.Mailbox,
            SkipInlineAttachments = !settings.IncludeInline,
            MaxAttachmentBytes = (long)settings.MaxAttachmentSizeMb * 1024 * 1024,
            MaxTotalAttachmentBytes = (long)settings.MaxTotalSizeMb * 1024 * 1024,
            AllowedContentTypes = string.IsNullOrWhiteSpace(settings.AllowTypes)
                ? new List<string>()
                : settings.AllowTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        if (settings.Verbose)
        {
            _console.MarkupLine($"[dim]Output:      {Markup.Escape(settings.OutputDirectory)}[/]");
            _console.MarkupLine($"[dim]Folder:      {Markup.Escape(settings.Folder)}[/]");
            if (after.HasValue) _console.MarkupLine($"[dim]After:       {after:yyyy-MM-dd}[/]");
            if (before.HasValue) _console.MarkupLine($"[dim]Before:      {before:yyyy-MM-dd}[/]");
            if (!string.IsNullOrEmpty(settings.From)) _console.MarkupLine($"[dim]From:        {Markup.Escape(settings.From)}[/]");
            if (!string.IsNullOrEmpty(settings.Subject)) _console.MarkupLine($"[dim]Subject:     {Markup.Escape(settings.Subject)}[/]");
            if (settings.MaxMessages.HasValue) _console.MarkupLine($"[dim]Max:         {settings.MaxMessages}[/]");
            _console.MarkupLine($"[dim]Attachments: {(!settings.NoAttachments ? "Yes" : "No")}[/]");
            _console.MarkupLine($"[dim]Mailbox:     {Markup.Escape(settings.Mailbox ?? "(your own)")}[/]");
            _console.MarkupLine($"[dim]Layout:      {layout}[/]");
            _console.WriteLine();
        }

        EmailExportResult result;

        try
        {
            result = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Exporting emails...", async ctx =>
                {
                    var progress = new Progress<EmailExportProgress>(p =>
                    {
                        // Escape everything that came off the wire: a subject containing
                        // '[' would otherwise be read as Spectre markup and throw.
                        ctx.Status(p.Phase switch
                        {
                            "Fetching" => $"[dim]Fetching from Graph...[/] {Markup.Escape(p.Detail ?? "")}",
                            "Folders" => "[dim]Resolving mail folders...[/]",
                            // No total: messages are streamed, so the count climbs
                            // rather than filling a bar.
                            _ => $"Exported {p.CurrentMessage}{(p.TotalMessages > 0 ? "/" + p.TotalMessages : "")} [dim]{Markup.Escape(Truncate(p.CurrentSubject))}[/]"
                        });
                    });
                    return await _exportService.ExportAsync(exportOptions, progress, CancellationToken.None);
                });
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Export failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        _console.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn("[bold]Count[/]");
        table.AddRow("Exported", $"[green]{result.ExportedCount}[/]");
        table.AddRow("Skipped", $"[yellow]{result.SkippedCount}[/]");
        table.AddRow("Errors", result.ErrorCount > 0 ? $"[red]{result.ErrorCount}[/]" : $"{result.ErrorCount}");
        table.AddRow("Threads", $"{result.ThreadCount}");
        table.AddRow("Attachments saved", $"{result.AttachmentsWritten} ({result.AttachmentBytesWritten / (1024.0 * 1024.0):F1} MB)");
        table.AddRow("Attachments held back", result.AttachmentsSkipped > 0 ? $"[yellow]{result.AttachmentsSkipped}[/]" : "0");
        _console.Write(table);

        if (result.AttachmentsSkipped > 0)
        {
            _console.MarkupLine("[dim]Held-back attachments are listed with a reason in each message's manifest.csv.[/]");
        }

        _console.WriteLine();
        _console.MarkupLine($"[dim]Output directory: [bold]{Markup.Escape(Path.GetFullPath(settings.OutputDirectory))}[/][/]");

        return result.ErrorCount > 0 ? 1 : 0;
    }

    private static string Truncate(string? value, int max = 60)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var flat = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "\u2026";
    }
}
