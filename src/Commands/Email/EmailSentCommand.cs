using System.ComponentModel;
using System.Globalization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Email;

/// <summary>
/// Closes the loop that 'pks email draft' opens. Draft composes a letter into the mailbox;
/// a human presses Send; this command reads Sent Items back, stamps each letter that
/// actually left with the date it left, and moves it to the archive folder.
///
/// It writes only to local files. Against the mailbox it is strictly read-only, which is why
/// it needs no more permission than the draft command already has.
/// </summary>
[Description("Match letters against Sent Items, stamp them with the send date and archive them")]
public class EmailSentCommand : AsyncCommand<EmailSentCommand.Settings>
{
    private readonly IMsGraphEmailService _emailService;
    private readonly IAnsiConsole _console;

    public EmailSentCommand(IMsGraphEmailService emailService, IAnsiConsole console)
    {
        _emailService = emailService;
        _console = console;
    }

    public class Settings : EmailSettings
    {
        [CommandArgument(0, "[PATH]")]
        [Description("A markdown letter, or a directory of them. Defaults to the current directory")]
        public string Path { get; set; } = ".";

        [CommandOption("-m|--mailbox <UPN>")]
        [Description("Mailbox the letters were sent from. Defaults to your own")]
        public string? Mailbox { get; set; }

        [CommandOption("--after <DATE>")]
        [Description("Only look at mail sent on or after this date (yyyy-MM-dd). Defaults to 30 days ago")]
        public string? After { get; set; }

        [CommandOption("--archive <DIR>")]
        [Description("Folder to move sent letters into, relative to the letter. Defaults to 'sendt'")]
        public string Archive { get; set; } = "sendt";

        [CommandOption("--no-archive")]
        [Description("Stamp the send date into the letter but leave the file where it is")]
        public bool NoArchive { get; set; }

        [CommandOption("--dry-run")]
        [Description("Report what was found in Sent Items, change no file")]
        public bool DryRun { get; set; }

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
                if (letter != null)
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

        DateTime after;
        if (string.IsNullOrWhiteSpace(settings.After))
        {
            after = DateTime.UtcNow.AddDays(-30);
        }
        else if (!DateTime.TryParse(settings.After, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out after))
        {
            _console.MarkupLine($"[red]Could not read --after '{settings.After.EscapeMarkup()}'. Use yyyy-MM-dd.[/]");
            return 1;
        }

        var mailboxLabel = settings.Mailbox ?? "your own mailbox";
        _console.MarkupLine($"Reading Sent Items in [white]{mailboxLabel.EscapeMarkup()}[/] from [white]{after.ToLocalTime():yyyy-MM-dd}[/]:");

        List<MsGraphFolderMessage> sent;
        try
        {
            sent = await _emailService.ListFolderMessagesAsync("sentitems", after, settings.Mailbox);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Letter");
        table.AddColumn("To");
        table.AddColumn("Sent");
        table.AddColumn("Action");

        var matched = new List<(LetterFile Letter, DateTime When)>();
        var unmatched = 0;

        foreach (var letter in letters)
        {
            var recipients = string.Join(", ", letter.To).EscapeMarkup();

            if (letter.Skip)
            {
                table.AddRow(letter.Name.EscapeMarkup(), recipients, "—", "[yellow]skip: true — deliberately not sent[/]");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(letter.Sent))
            {
                table.AddRow(letter.Name.EscapeMarkup(), recipients, letter.Sent.EscapeMarkup(), "[dim]already stamped[/]");
                continue;
            }

            // The closest match wins, and among equals the earliest: resending a letter leaves
            // two copies in Sent Items, and the date that belongs in the audit trail is the day
            // the customer first had it. Closeness has to come first, though — a letter written
            // as a reply shares its subject with every other letter in that thread once the
            // prefixes are stripped, and the earliest of those is the mail it answers.
            var hit = sent
                .Where(letter.Matches)
                .OrderBy(letter.MatchQuality)
                .ThenBy(m => m.SentDateTime ?? DateTime.MaxValue)
                .FirstOrDefault();

            if (hit?.SentDateTime == null)
            {
                table.AddRow(letter.Name.EscapeMarkup(), recipients, "—", "[red]not found in Sent Items[/]");
                unmatched++;
                continue;
            }

            var when = hit.SentDateTime.Value.ToLocalTime();
            matched.Add((letter, when));
            table.AddRow(
                letter.Name.EscapeMarkup(),
                recipients,
                $"{when:yyyy-MM-dd HH:mm}",
                settings.NoArchive ? "stamp" : $"stamp + move to {settings.Archive.EscapeMarkup()}/");
        }

        _console.Write(table);

        if (settings.DryRun)
        {
            _console.MarkupLine("[yellow]Dry run — no file was changed.[/]");
            return 0;
        }

        foreach (var (letter, when) in matched)
        {
            try
            {
                Stamp(letter, when, settings);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]✗ {letter.Name.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
                return 1;
            }
        }

        _console.WriteLine();
        _console.MarkupLine($"[green]{matched.Count} letter(s) stamped[/]{(settings.NoArchive ? "" : $" and moved to [white]{settings.Archive.EscapeMarkup()}/[/]")}.");
        if (unmatched > 0)
            _console.MarkupLine($"[yellow]{unmatched} letter(s) had no match in Sent Items — they are still waiting to go out.[/]");
        return 0;
    }

    /// <summary>
    /// Writes the send date into the letter's front matter and moves the file. Only front
    /// matter lines are touched: the body is the deliverable that was verified before it was
    /// sent, and it must come out of this byte for byte unchanged.
    /// </summary>
    private void Stamp(LetterFile letter, DateTime when, Settings settings)
    {
        var text = File.ReadAllText(letter.FullPath);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var close = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "---")
            {
                close = i;
                break;
            }
        }

        if (close < 0)
            throw new InvalidOperationException("front matter has no closing '---'.");

        var sourceDirectory = Path.GetDirectoryName(letter.FullPath) ?? ".";
        var targetDirectory = settings.NoArchive
            ? sourceDirectory
            : Path.GetFullPath(Path.Combine(sourceDirectory, settings.Archive));

        // Attachment paths are relative to the letter, so moving the letter breaks them unless
        // they are rewritten against where it lands. The bilag folders themselves stay put.
        if (!string.Equals(sourceDirectory, targetDirectory, StringComparison.Ordinal) && letter.Attachments.Count > 0)
        {
            for (var i = 1; i < close; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("- ") || lines[i].Length == 0 || !char.IsWhiteSpace(lines[i][0]))
                    continue;

                var value = trimmed[2..].Trim();
                var absolute = Path.GetFullPath(Path.Combine(sourceDirectory, value));
                if (!letter.Attachments.Contains(absolute))
                    continue;

                var indent = lines[i][..(lines[i].Length - trimmed.Length)];
                lines[i] = $"{indent}- {Path.GetRelativePath(targetDirectory, absolute).Replace('\\', '/')}";
            }
        }

        // A previous 'sent' (or the Danish placeholder the letters carried before this command
        // existed) is replaced rather than doubled up.
        for (var i = close - 1; i >= 1; i--)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            var key = line[..colon].Trim();
            if (key.Equals("sent", StringComparison.OrdinalIgnoreCase) || key.Equals("sendt", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(i);
                // Every removal is above the closing '---', so the insertion point moves up
                // with it. Forgetting this puts the stamp one line below the front matter,
                // where it is body text rather than metadata.
                close--;
            }
        }

        lines.Insert(close, $"sent: {when:yyyy-MM-dd HH:mm}");

        File.WriteAllText(letter.FullPath, string.Join(newline, lines));

        var destination = letter.FullPath;
        if (!settings.NoArchive)
        {
            Directory.CreateDirectory(targetDirectory);
            destination = Path.Combine(targetDirectory, letter.Name);
            if (File.Exists(destination))
                throw new InvalidOperationException($"{Path.GetRelativePath(sourceDirectory, destination)} already exists.");
            File.Move(letter.FullPath, destination);
        }

        _console.MarkupLine($"[green]✓[/] {letter.Name.EscapeMarkup()} → [white]sent: {when:yyyy-MM-dd HH:mm}[/]{(settings.NoArchive ? "" : $" → {settings.Archive.EscapeMarkup()}/")}");
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
