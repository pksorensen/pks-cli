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
        [Description("Mail folder (default: inbox)")]
        [DefaultValue("inbox")]
        public string Folder { get; set; } = "inbox";

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

        var query = new MsGraphEmailQuery
        {
            Folder = settings.Folder,
            After = after,
            Before = before,
            From = settings.From,
            Subject = settings.Subject,
            MaxMessages = settings.MaxMessages
        };

        var exportOptions = new MsGraphEmailExportOptions
        {
            Query = query,
            OutputDirectory = settings.OutputDirectory,
            DownloadAttachments = !settings.NoAttachments,
            OverwriteExisting = settings.Overwrite
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
                        ctx.Status($"Exporting emails... {p.CurrentMessage}/{p.TotalMessages} - {p.Phase}");
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
        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine($"[dim]Output directory: [bold]{Markup.Escape(Path.GetFullPath(settings.OutputDirectory))}[/][/]");

        return result.ErrorCount > 0 ? 1 : 0;
    }
}
