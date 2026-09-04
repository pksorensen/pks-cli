using Markdig;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Email;

/// <summary>
/// Turns a markdown letter with front matter into a draft in a Microsoft 365 mailbox.
///
/// The point of the command is what it cannot do: it composes into Drafts and stops. A
/// human opens Outlook, reads the letter and presses Send. That is why the token this
/// CLI registers asks for Mail.ReadWrite and deliberately not Mail.Send, and why this
/// command refuses to run at all if it ever finds Mail.Send in the stored scopes.
/// </summary>
[Description("Create Outlook drafts from markdown letters with front matter")]
public class EmailDraftCommand : AsyncCommand<EmailDraftCommand.Settings>
{
    private readonly IMsGraphAuthenticationService _authService;
    private readonly IMsGraphEmailService _emailService;
    private readonly IAnsiConsole _console;

    public EmailDraftCommand(
        IMsGraphAuthenticationService authService,
        IMsGraphEmailService emailService,
        IAnsiConsole console)
    {
        _authService = authService;
        _emailService = emailService;
        _console = console;
    }

    public class Settings : EmailSettings
    {
        [CommandArgument(0, "[PATH]")]
        [Description("A markdown letter, or a directory of them. Defaults to the current directory")]
        public string Path { get; set; } = ".";

        [CommandOption("-m|--mailbox <UPN>")]
        [Description("Mailbox to compose in. Defaults to your own; any other requires FullAccess on it")]
        public string? Mailbox { get; set; }

        [CommandOption("--dry-run")]
        [Description("Parse and render everything, print what would be created, contact nothing")]
        public bool DryRun { get; set; }

        [CommandOption("--force")]
        [Description("Create the draft even when Drafts already holds one with the same subject")]
        public bool Force { get; set; }

        [CommandOption("--show-html")]
        [Description("Print the rendered HTML of each letter, exactly as it would reach the mailbox")]
        public bool ShowHtml { get; set; }

        [CommandOption("--only <NAMES>")]
        [Description("Comma-separated file name prefixes to include, e.g. 07,08,09")]
        public string? Only { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var letters = new List<LetterFile>();

        try
        {
            foreach (var path in ResolvePaths(settings))
            {
                var letter = LetterFile.Parse(path);
                if (letter != null && !letter.Skip)
                    letters.Add(letter);
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        if (letters.Count == 0)
        {
            _console.MarkupLine("[yellow]No letters with front matter found.[/]");
            return 1;
        }

        // Attachments are resolved before anything is sent anywhere. A letter that promises
        // an invoice and arrives without it is worse than a letter that was never created.
        foreach (var letter in letters)
        {
            foreach (var missing in letter.MissingAttachments)
            {
                _console.MarkupLine($"[red]{letter.Name.EscapeMarkup()}: attachment not found: {missing.EscapeMarkup()}[/]");
                return 1;
            }
        }

        var mailboxLabel = settings.Mailbox ?? "your own mailbox";

        if (settings.DryRun)
        {
            Report(letters, mailboxLabel, settings.ShowHtml);
            _console.MarkupLine("[yellow]Dry run — nothing was created.[/]");
            return 0;
        }

        var guard = await CheckScopesAsync();
        if (guard != null)
        {
            _console.MarkupLine($"[red]{guard.EscapeMarkup()}[/]");
            return 1;
        }

        Report(letters, mailboxLabel, settings.ShowHtml);

        List<MsGraphFolderMessage> existing;
        try
        {
            existing = settings.Force
                ? new List<MsGraphFolderMessage>()
                : await _emailService.ListFolderMessagesAsync("drafts", mailbox: settings.Mailbox);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        var created = 0;
        var skipped = 0;

        foreach (var letter in letters)
        {
            // Subject alone is not identity: three letters in one batch can carry the same
            // subject and differ only in who they go to. Matching on subject alone would
            // "already exists" its way past two of the three.
            if (existing.Any(letter.Matches))
            {
                _console.MarkupLine($"[dim]{letter.Name.EscapeMarkup()}: a draft with this subject already exists — skipped (--force to repeat)[/]");
                skipped++;
                continue;
            }

            try
            {
                var result = await _emailService.CreateDraftAsync(letter.ToRequest(), settings.Mailbox);
                created++;
                _console.MarkupLine($"[green]✓[/] {letter.Name.EscapeMarkup()} → [white]{letter.Subject.EscapeMarkup()}[/] ({result.AttachmentCount} attachment(s))");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]✗ {letter.Name.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
                return 1;
            }
        }

        _console.WriteLine();
        _console.MarkupLine($"[green]{created} draft(s) created[/]{(skipped > 0 ? $", {skipped} already there" : "")} in [white]{mailboxLabel.EscapeMarkup()}[/].");
        _console.MarkupLine("[dim]Nothing has been sent. Open Outlook → Drafts, read them, press Send.[/]");
        return 0;
    }

    private void Report(List<LetterFile> letters, string mailboxLabel, bool showHtml)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Letter");
        table.AddColumn("To");
        table.AddColumn("Subject");
        table.AddColumn("Files");

        foreach (var letter in letters)
        {
            var recipients = string.Join(", ", letter.To);
            if (letter.Cc.Count > 0)
                recipients += $"\ncc: {string.Join(", ", letter.Cc)}";

            table.AddRow(
                letter.Name.EscapeMarkup(),
                recipients.EscapeMarkup(),
                letter.Subject.EscapeMarkup(),
                letter.Attachments.Count.ToString());
        }

        _console.MarkupLine($"Composing into [white]{mailboxLabel.EscapeMarkup()}[/]:");
        _console.Write(table);

        if (showHtml)
        {
            foreach (var letter in letters)
            {
                _console.MarkupLine($"[dim]── {letter.Name.EscapeMarkup()} ──[/]");
                _console.WriteLine(letter.HtmlPreview());
            }
        }
    }

    /// <summary>
    /// The whole safety story of this command in one method: a token carrying Mail.Send must
    /// never be used from here, and a token without Mail.ReadWrite cannot do the job. Reading
    /// the stored scopes is enough — they are what the token was issued for.
    /// </summary>
    private async Task<string?> CheckScopesAsync()
    {
        var token = await _authService.GetStoredTokenAsync();
        if (token == null)
            return "Not signed in. Run 'pks ms-graph register' first.";

        var scopes = token.Scopes ?? Array.Empty<string>();

        if (scopes.Any(s => s.Contains("Mail.Send", StringComparison.OrdinalIgnoreCase)))
            return "The stored token carries Mail.Send. This command composes drafts only and refuses to run with a token that can send. Remove Mail.Send from the app registration and run 'pks ms-graph register --force'.";

        if (!scopes.Any(s => s.Contains("Mail.ReadWrite", StringComparison.OrdinalIgnoreCase)))
            return "The stored token has no Mail.ReadWrite scope, so it cannot write a draft. Run 'pks ms-graph register --force' to sign in again with the current scope set.";

        return null;
    }

    private static IEnumerable<string> ResolvePaths(Settings settings)
    {
        var only = string.IsNullOrWhiteSpace(settings.Only)
            ? null
            : settings.Only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (File.Exists(settings.Path))
        {
            yield return settings.Path;
            yield break;
        }

        if (!Directory.Exists(settings.Path))
            throw new FileNotFoundException($"No such file or directory: {settings.Path}");

        foreach (var file in Directory.GetFiles(settings.Path, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (only != null && !only.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return file;
        }
    }
}
