using System.Text;
using PKS.Infrastructure.Services.Models;

namespace PKS.Commands.Confluence;

/// <summary>
/// Renders Confluence comments into a markdown sidecar file.
/// Sidecar files are read-only snapshots — the commit command skips them by filename
/// pattern and by their <c>type: comments</c> frontmatter.
/// </summary>
internal static class ConfluenceCommentsWriter
{
    public const string FullSyncFilename = "comments.md";

    /// <summary>
    /// Writes a comments sidecar if there are any comments. If the page has no comments
    /// and a sidecar already exists from a previous checkout, deletes it so the local
    /// state stays in sync with Confluence.
    /// </summary>
    public static async Task WriteSidecarAsync(
        string sidecarPath,
        string pageId,
        string pageTitle,
        string? spaceKey,
        string? siteUrl,
        List<ConfluenceComment> comments,
        DateTime fetchedAt)
    {
        if (comments.Count == 0)
        {
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
            return;
        }

        var md = RenderMarkdown(pageId, pageTitle, spaceKey, siteUrl, comments, fetchedAt);
        Directory.CreateDirectory(Path.GetDirectoryName(sidecarPath)!);
        await File.WriteAllTextAsync(sidecarPath, md);
    }

    internal static string RenderMarkdown(
        string pageId,
        string pageTitle,
        string? spaceKey,
        string? siteUrl,
        List<ConfluenceComment> comments,
        DateTime fetchedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("type: comments");
        sb.AppendLine($"page_id: \"{pageId}\"");
        sb.AppendLine($"page_title: \"{EscapeYaml(pageTitle)}\"");
        if (!string.IsNullOrEmpty(spaceKey))
            sb.AppendLine($"space: {spaceKey}");
        sb.AppendLine($"fetched_at: \"{fetchedAt:O}\"");
        sb.AppendLine($"comment_count: {CountAll(comments)}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# Comments on \"{pageTitle}\"");
        sb.AppendLine();
        sb.AppendLine("> Read-only snapshot of Confluence comments — **not pushed back** on `pks confluence commit`.");
        var pageUrl = BuildPageUrl(siteUrl, spaceKey, pageId);
        if (pageUrl != null)
            sb.AppendLine($"> View / reply on Confluence: {pageUrl}");
        sb.AppendLine();

        for (var i = 0; i < comments.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
            RenderComment(sb, comments[i], pageUrl, depth: 2);
        }

        return sb.ToString();
    }

    private static void RenderComment(StringBuilder sb, ConfluenceComment c, string? pageUrl, int depth)
    {
        var heading = new string('#', Math.Min(depth, 6));
        var meta = new List<string>
        {
            c.Id,
            string.IsNullOrEmpty(c.AuthorName) ? "(unknown)" : c.AuthorName,
            c.Created == default ? "(no date)" : c.Created.ToString("yyyy-MM-dd HH:mm"),
            c.Location
        };
        if (!string.IsNullOrEmpty(c.ResolutionStatus))
            meta.Add(c.ResolutionStatus);

        sb.Append(heading).Append(' ').AppendLine(string.Join(" · ", meta));
        if (pageUrl != null)
            sb.AppendLine($"_Link: {pageUrl}?focusedCommentId={c.Id}_");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(c.InlineSelection))
        {
            sb.Append("**Selection:** ").Append('"').Append(c.InlineSelection).AppendLine("\"");
            sb.AppendLine();
        }

        var body = ConvertStorageHtmlToMarkdown(c.BodyStorageHtml).Trim();
        if (body.Length > 0)
        {
            sb.AppendLine(body);
            sb.AppendLine();
        }

        foreach (var reply in c.Replies)
            RenderComment(sb, reply, pageUrl, depth + 1);
    }

    private static string ConvertStorageHtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            SmartHrefHandling = true
        });
        return converter.Convert(html);
    }

    private static string? BuildPageUrl(string? siteUrl, string? spaceKey, string pageId)
    {
        if (string.IsNullOrEmpty(siteUrl) || string.IsNullOrEmpty(spaceKey))
            return null;

        var baseUrl = siteUrl.TrimEnd('/');
        return $"{baseUrl}/wiki/spaces/{spaceKey}/pages/{pageId}";
    }

    private static int CountAll(List<ConfluenceComment> comments)
    {
        var n = 0;
        foreach (var c in comments)
        {
            n++;
            n += CountAll(c.Replies);
        }
        return n;
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
