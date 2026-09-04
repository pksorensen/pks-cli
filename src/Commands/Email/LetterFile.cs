using Markdig;
using System.Text;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Models;

namespace PKS.Commands.Email;

/// <summary>
/// One letter on disk: front matter, then the mail body, then — after a horizontal rule —
/// whatever notes the author kept beside it. Only the middle part becomes mail.
/// </summary>
internal class LetterFile
{
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Absolute path of the file this was read from. Named FullPath, not Path, because a
    /// property called Path inside this class would shadow System.IO.Path for the whole file.
    /// </summary>
    public string FullPath { get; private set; } = string.Empty;

    /// <summary>
    /// The letter says it is not ready. It is still parsed and returned, because a command that
    /// walks a folder has to be able to say "this one is deliberately not going out" out loud
    /// rather than pretend the file does not exist.
    /// </summary>
    public bool Skip { get; private set; }

    /// <summary>Value of the 'sent' key, once a run has stamped one in. Null while unsent.</summary>
    public string? Sent { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public List<string> To { get; private set; } = new();
    public List<string> Cc { get; private set; } = new();
    public List<string> Bcc { get; private set; } = new();
    public string? ReplyTo { get; private set; }

    /// <summary>
    /// Internet message id of the mail this letter answers, taken straight from the
    /// <c>internetMessageId</c> column of an export. Set it and the letter is composed as a
    /// reply inside the existing thread instead of as a new mail.
    /// </summary>
    public string? InReplyTo { get; private set; }

    public List<string> Attachments { get; private set; } = new();
    public List<string> MissingAttachments { get; private set; } = new();
    public string Markdown { get; private set; } = string.Empty;

    public static LetterFile? Parse(string path)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileName(path);

        var (front, body) = SplitFrontMatter(text);
        if (front == null)
            return null;

        var letter = new LetterFile
        {
            Name = name,
            Markdown = body,
            FullPath = Path.GetFullPath(path),
            Sent = Scalar(front, "sent")
        };
        // A letter can mark itself not-ready. Expressing that in the file rather than in
        // the caller is what makes pointing the command at a whole folder safe.
        // Only the first token counts, so the key can carry the reason inline:
        // "skip: true   # invoice is still a draft in the ledger".
        var skip = Scalar(front, "skip")?.Split(' ', '\t')[0];
        letter.Skip = skip != null && (skip.Equals("true", StringComparison.OrdinalIgnoreCase) || skip.Equals("yes", StringComparison.OrdinalIgnoreCase));

        // A skipped letter is not held to the rules the others are: its invoice number may
        // still be a placeholder and the PDF it names may not exist yet. That is the whole
        // reason it is marked skip.
        if (letter.Skip)
        {
            letter.Subject = Scalar(front, "subject") ?? string.Empty;
            letter.To = Addresses(front, "to");
            return letter;
        }

        letter.Subject = Scalar(front, "subject")
            ?? throw new InvalidOperationException($"{name}: front matter has no 'subject'.");
        letter.To = Addresses(front, "to");
        letter.Cc = Addresses(front, "cc");
        letter.Bcc = Addresses(front, "bcc");
        letter.ReplyTo = Scalar(front, "reply-to");
        // Like 'skip', this key routinely carries a note after the value —
        // "in-reply-to: \"<...>\"   # his mail of 01-09 18:40" — so only the first token counts.
        letter.InReplyTo = Scalar(front, "in-reply-to")?.Split(' ', '\t')[0].Trim().Trim('"', '\'');

        if (letter.To.Count == 0)
            throw new InvalidOperationException($"{name}: front matter has no 'to'.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        foreach (var relative in List(front, "attachments"))
        {
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            if (File.Exists(full))
                letter.Attachments.Add(full);
            else
                letter.MissingAttachments.Add(relative);
        }

        if (string.IsNullOrWhiteSpace(letter.Markdown))
            throw new InvalidOperationException($"{name}: the letter has no body between the front matter and the first horizontal rule.");

        return letter;
    }

    public MsGraphDraftRequest ToRequest() => new()
    {
        Subject = Subject,
        HtmlBody = MarkdownToHtml(Markdown),
        To = To,
        Cc = Cc,
        Bcc = Bcc,
        ReplyTo = ReplyTo,
        InReplyTo = InReplyTo,
        Attachments = Attachments.Select(path => new MsGraphDraftAttachment
        {
            Name = Path.GetFileName(path),
            ContentType = ContentTypeFor(path),
            Content = File.ReadAllBytes(path)
        }).ToList()
    };

    /// <summary>
    /// True when a message in the mailbox is this letter: same subject, and every recipient of
    /// the letter among the recipients of the message. Both halves are needed — a batch of
    /// letters routinely shares a subject, and a subject-only test would collapse them.
    /// </summary>
    public bool Matches(MsGraphFolderMessage message) =>
        string.Equals(WithoutReplyPrefix(message.Subject), WithoutReplyPrefix(Subject), StringComparison.OrdinalIgnoreCase)
        && To.Count > 0
        && To.All(address => message.To.Any(other => string.Equals(other, address, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// How well a message answers to this letter, lowest first. Stripping reply prefixes makes
    /// the match tolerant, and tolerance costs precision: a reply and the letter it answers
    /// share a subject once the prefixes are gone, so a whole thread matches every letter in
    /// it. The rank restores the distinction — an untouched subject is the best evidence, and
    /// for a letter that is itself a reply, a message whose subject carries a prefix beats one
    /// whose does not, because that is what leaving a mailbox as a reply does to a subject.
    /// </summary>
    public int MatchQuality(MsGraphFolderMessage message)
    {
        if (string.Equals(message.Subject.Trim(), Subject.Trim(), StringComparison.OrdinalIgnoreCase))
            return 0;

        if (InReplyTo != null && HasReplyPrefix(message.Subject))
            return 1;

        return 2;
    }

    private static bool HasReplyPrefix(string subject) =>
        !string.Equals(WithoutReplyPrefix(subject), subject.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Strips the reply and forward prefixes a mail system stacks on a subject. It has to be
    /// done on both sides of the comparison: a letter written as "SV: …" comes back from
    /// Graph's createReply as "RE: …", because the prefix is the mail system's, not ours, and
    /// an exact match would then report a letter that has plainly been sent as still waiting.
    /// Repeated prefixes are peeled one at a time — "RE: SV: …" is one thread, not three.
    /// </summary>
    private static string WithoutReplyPrefix(string subject)
    {
        var value = subject.Trim();
        string[] prefixes = ["re:", "sv:", "vs:", "fw:", "fwd:", "vb:", "aw:"];

        bool stripped;
        do
        {
            stripped = false;
            foreach (var prefix in prefixes)
            {
                if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                value = value[prefix.Length..].TrimStart();
                stripped = true;
                break;
            }
        } while (stripped);

        return value;
    }

    /// <summary>The exact HTML that would be posted to Graph, for eyeballing before it is.</summary>
    public string HtmlPreview() => MarkdownToHtml(Markdown);

    /// <summary>
    /// Splits the file into front matter and mail body. The body runs from the end of the
    /// front matter to the first horizontal rule, so anything the author keeps below that
    /// rule — call notes, a briefing, an audit trail — stays out of the mail by construction.
    /// </summary>
    private static (List<string>? front, string body) SplitFrontMatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return (null, string.Empty);

        var front = new List<string>();
        var index = 1;
        for (; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
            {
                index++;
                break;
            }
            front.Add(lines[index]);
        }

        var body = new List<string>();
        for (; index < lines.Length; index++)
        {
            if (lines[index].Trim() == "---")
                break;
            body.Add(lines[index]);
        }

        return (front, string.Join("\n", body).Trim());
    }

    private static string? Scalar(List<string> front, string key)
    {
        var values = Values(front, key);
        return values.Count > 0 ? values[0] : null;
    }

    private static List<string> List(List<string> front, string key) => Values(front, key);

    /// <summary>
    /// Addresses may be written as a plain address, an inline list, a dash list, or the
    /// display form 'Name &lt;name@example.com&gt;'. Everything that is not an address is
    /// dropped, so a human-readable recipient line still produces a machine-correct one.
    /// </summary>
    private static List<string> Addresses(List<string> front, string key)
    {
        var pattern = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}");
        return Values(front, key)
            .SelectMany(value => pattern.Matches(value).Select(m => m.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A deliberately small YAML reader: scalars, inline lists, dash lists and block
    /// scalars. Enough for a letter header, and it fails loudly rather than guessing.
    /// Unknown keys are ignored, which is what keeps audit fields (amount, invoice number,
    /// reconciliation) in the same file as the letter they belong to.
    /// </summary>
    private static List<string> Values(List<string> front, string key)
    {
        var results = new List<string>();

        for (var i = 0; i < front.Count; i++)
        {
            var line = front[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            if (!line[..colon].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = line[(colon + 1)..].Trim();

            if (value is "|" or ">" or "|-" or ">-")
            {
                var block = new List<string>();
                for (var j = i + 1; j < front.Count && (front[j].Length == 0 || char.IsWhiteSpace(front[j][0])); j++)
                    block.Add(front[j].Trim());
                results.Add(string.Join(value.StartsWith('>') ? " " : "\n", block).Trim());
                return results;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                results.AddRange(value[1..^1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Unquote));
                return results;
            }

            if (value.Length > 0)
            {
                results.Add(Unquote(value));
                return results;
            }

            for (var j = i + 1; j < front.Count; j++)
            {
                var item = front[j];
                if (item.Trim().Length == 0)
                    continue;
                if (!char.IsWhiteSpace(item[0]))
                    break;

                var trimmed = item.Trim();
                if (!trimmed.StartsWith("- "))
                    break;

                results.Add(Unquote(trimmed[2..].Trim()));
            }

            return results;
        }

        return results;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".html" or ".htm" => "text/html",
        ".zip" => "application/zip",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// Markdown to the HTML Outlook will show. Soft line breaks are rendered as hard ones
    /// because a signature block is written as consecutive lines and means them; the default
    /// Markdown reading would collapse it into a single line.
    /// </summary>
    internal static string MarkdownToHtml(string markdown)
    {
        var pipeline = new Markdig.MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoLinks()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        var html = Markdig.Markdown.ToHtml(markdown, pipeline);

        // Mail clients have no stylesheet to fall back on, so the few things that must not
        // look broken — table rules above all — carry their styling inline.
        html = html.Replace("<table>", "<table style=\"border-collapse:collapse;\" cellpadding=\"6\">");
        html = html.Replace("<th>", "<th style=\"border:1px solid #999;text-align:left;\">");
        html = html.Replace("<td>", "<td style=\"border:1px solid #999;\">");
        html = Regex.Replace(html, "<th style=\"text-align: (left|right|center);\">", m => $"<th style=\"border:1px solid #999;text-align:{m.Groups[1].Value};\">");
        html = Regex.Replace(html, "<td style=\"text-align: (left|right|center);\">", m => $"<td style=\"border:1px solid #999;text-align:{m.Groups[1].Value};\">");

        var builder = new StringBuilder();
        builder.Append("<div style=\"font-family:Calibri,Segoe UI,Arial,sans-serif;font-size:11pt;color:#000000;\">");
        builder.Append(html);
        builder.Append("</div>");
        return builder.ToString();
    }
}
