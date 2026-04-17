using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for exporting Microsoft Graph email messages to markdown files
/// </summary>
public interface IMsGraphEmailExportService
{
    /// <summary>
    /// Exports email messages to markdown files with YAML frontmatter and attachments
    /// </summary>
    Task<EmailExportResult> ExportAsync(MsGraphEmailExportOptions options, IProgress<EmailExportProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Generates markdown content with YAML frontmatter for a single email message
    /// </summary>
    string GenerateMarkdown(MsGraphMessage message, List<MsGraphAttachment>? attachments = null);

    /// <summary>
    /// Generates the output file path for a message based on its received date and subject
    /// </summary>
    string GenerateOutputPath(MsGraphMessage message, string baseOutputDir);

    /// <summary>
    /// Converts text to a URL-friendly slug
    /// </summary>
    string Slugify(string text, int maxLength = 60);
}

/// <summary>
/// Implementation of email export service that converts Microsoft Graph messages
/// to markdown files with YAML frontmatter and saves attachments to disk
/// </summary>
public class MsGraphEmailExportService : IMsGraphEmailExportService
{
    private readonly IMsGraphEmailService _emailService;
    private readonly ILogger<MsGraphEmailExportService> _logger;

    public MsGraphEmailExportService(
        IMsGraphEmailService emailService,
        ILogger<MsGraphEmailExportService> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<EmailExportResult> ExportAsync(
        MsGraphEmailExportOptions options,
        IProgress<EmailExportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new EmailExportResult();

        _logger.LogInformation("Starting email export to {OutputDirectory}", options.OutputDirectory);

        progress?.Report(new EmailExportProgress
        {
            Phase = "Fetching",
            CurrentMessage = 0,
            TotalMessages = 0
        });

        List<MsGraphMessage> messages;
        try
        {
            messages = await _emailService.GetMessagesAsync(options.Query, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch messages");
            result.Errors.Add($"Failed to fetch messages: {ex.Message}");
            result.ErrorCount = 1;
            return result;
        }

        result.TotalMessages = messages.Count;
        _logger.LogInformation("Fetched {Count} messages for export", messages.Count);

        for (var i = 0; i < messages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var message = messages[i];

            try
            {
                progress?.Report(new EmailExportProgress
                {
                    Phase = "Exporting",
                    CurrentMessage = i + 1,
                    TotalMessages = messages.Count,
                    CurrentSubject = message.Subject
                });

                var outputPath = GenerateOutputPath(message, options.OutputDirectory);

                if (File.Exists(outputPath) && !options.OverwriteExisting)
                {
                    _logger.LogDebug("Skipping existing file: {Path}", outputPath);
                    result.SkippedCount++;
                    continue;
                }

                List<MsGraphAttachment>? attachments = null;
                if (message.HasAttachments && options.DownloadAttachments)
                {
                    attachments = await _emailService.GetAttachmentsAsync(message.Id, ct);
                }

                var markdown = GenerateMarkdown(message, attachments);

                var directory = Path.GetDirectoryName(outputPath)!;
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(outputPath, markdown, ct);

                if (attachments != null && attachments.Count > 0)
                {
                    var attachmentDir = Path.Combine(directory, "attachments");
                    Directory.CreateDirectory(attachmentDir);

                    foreach (var attachment in attachments)
                    {
                        if (string.IsNullOrEmpty(attachment.ContentBytes))
                            continue;

                        var attachmentPath = Path.Combine(attachmentDir, attachment.Name);
                        var bytes = Convert.FromBase64String(attachment.ContentBytes);
                        await File.WriteAllBytesAsync(attachmentPath, bytes, ct);
                    }
                }

                result.ExportedCount++;
                _logger.LogDebug("Exported message: {Subject}", message.Subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export message {Id}: {Subject}", message.Id, message.Subject);
                result.Errors.Add($"Failed to export '{message.Subject}': {ex.Message}");
                result.ErrorCount++;
            }
        }

        _logger.LogInformation(
            "Export complete: {Exported} exported, {Skipped} skipped, {Errors} errors out of {Total} messages",
            result.ExportedCount, result.SkippedCount, result.ErrorCount, result.TotalMessages);

        return result;
    }

    public string GenerateMarkdown(MsGraphMessage message, List<MsGraphAttachment>? attachments = null)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"subject: \"{EscapeYaml(message.Subject)}\"");
        sb.AppendLine($"from: \"{FormatRecipient(message.From)}\"");

        AppendRecipientList(sb, "to", message.ToRecipients);
        AppendRecipientList(sb, "cc", message.CcRecipients);

        var date = message.ReceivedDateTime ?? message.SentDateTime;
        if (date.HasValue)
        {
            sb.AppendLine($"date: {date.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (!string.IsNullOrEmpty(message.InternetMessageId))
        {
            sb.AppendLine($"messageId: \"{EscapeYaml(message.InternetMessageId)}\"");
        }

        if (!string.IsNullOrEmpty(message.ConversationId))
        {
            sb.AppendLine($"conversationId: \"{EscapeYaml(message.ConversationId)}\"");
        }

        sb.AppendLine($"importance: {message.Importance}");
        sb.AppendLine($"isRead: {message.IsRead.ToString().ToLowerInvariant()}");
        sb.AppendLine($"hasAttachments: {message.HasAttachments.ToString().ToLowerInvariant()}");

        if (message.Categories.Count > 0)
        {
            sb.AppendLine("categories:");
            foreach (var category in message.Categories)
            {
                sb.AppendLine($"  - \"{EscapeYaml(category)}\"");
            }
        }

        if (!string.IsNullOrEmpty(message.WebLink))
        {
            sb.AppendLine($"webLink: \"{EscapeYaml(message.WebLink)}\"");
        }

        sb.AppendLine($"exported_at: {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# {message.Subject}");
        sb.AppendLine();

        // Body
        if (message.Body != null && !string.IsNullOrEmpty(message.Body.Content))
        {
            var bodyContent = ConvertBody(message.Body);
            sb.AppendLine(bodyContent.TrimEnd());
        }

        // Attachments section
        if (attachments != null && attachments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Attachments");
            sb.AppendLine();
            foreach (var attachment in attachments)
            {
                var sizeStr = FormatFileSize(attachment.Size);
                sb.AppendLine($"- [{attachment.Name}](attachments/{attachment.Name}) ({sizeStr})");
            }
        }

        return sb.ToString();
    }

    public string GenerateOutputPath(MsGraphMessage message, string baseOutputDir)
    {
        var date = message.ReceivedDateTime ?? DateTime.UtcNow;
        var slug = Slugify(message.Subject);

        var path = Path.Combine(
            baseOutputDir,
            "raw",
            date.ToString("yyyy", CultureInfo.InvariantCulture),
            date.ToString("MM", CultureInfo.InvariantCulture),
            date.ToString("dd", CultureInfo.InvariantCulture),
            $"{date.ToString("HHmmss", CultureInfo.InvariantCulture)}-{slug}",
            $"{slug}.md");

        return path;
    }

    public string Slugify(string text, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "untitled";

        var slug = text.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9]", "-");
        slug = Regex.Replace(slug, @"-{2,}", "-");
        slug = slug.Trim('-');

        if (slug.Length > maxLength)
        {
            slug = slug[..maxLength].TrimEnd('-');
        }

        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
    }

    private string ConvertBody(MsGraphBody body)
    {
        if (string.Equals(body.ContentType, "html", StringComparison.OrdinalIgnoreCase))
        {
            var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
            {
                UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
                GithubFlavored = true,
                RemoveComments = true,
                SmartHrefHandling = true
            });
            return converter.Convert(body.Content);
        }

        return body.Content;
    }

    private static string FormatRecipient(MsGraphRecipient? recipient)
    {
        if (recipient?.EmailAddress == null)
            return string.Empty;

        var email = recipient.EmailAddress;
        if (!string.IsNullOrEmpty(email.Name) && !string.IsNullOrEmpty(email.Address))
            return $"{email.Name} <{email.Address}>";

        return email.Address ?? email.Name ?? string.Empty;
    }

    private static void AppendRecipientList(StringBuilder sb, string fieldName, List<MsGraphRecipient> recipients)
    {
        if (recipients.Count == 0)
            return;

        sb.AppendLine($"{fieldName}:");
        foreach (var recipient in recipients)
        {
            sb.AppendLine($"  - \"{FormatRecipient(recipient)}\"");
        }
    }

    private static string EscapeYaml(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";

        if (bytes >= 1024)
            return $"{bytes / 1024.0:F0} KB";

        return $"{bytes} B";
    }
}
