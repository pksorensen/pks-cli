using System.Text;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>Bidirectional conversion between Confluence storage format and Markdown.</summary>
public interface IConfluenceMarkdownConverter
{
    /// <summary>Confluence storage format XHTML → Markdown with YAML frontmatter.</summary>
    string StorageToMarkdown(string storageHtml, ConfluenceFrontmatter frontmatter);

    /// <summary>Markdown body (no frontmatter) → Confluence storage format XHTML.</summary>
    string MarkdownToStorage(string markdown);

    /// <summary>Parse YAML frontmatter from a markdown file.</summary>
    ConfluenceFrontmatter? ParseFrontmatter(string markdownContent);

    /// <summary>Extract body (everything after frontmatter) from a markdown file.</summary>
    string ExtractBody(string markdownContent);
}

public partial class ConfluenceMarkdownConverter : IConfluenceMarkdownConverter
{
    private static readonly Markdig.MarkdownPipeline Pipeline = Markdig.MarkdownExtensions.UseAdvancedExtensions(
        new Markdig.MarkdownPipelineBuilder())
        .Build();

    public string StorageToMarkdown(string storageHtml, ConfluenceFrontmatter frontmatter)
    {
        // Pre-process: extract ac:code macros into fenced code blocks before ReverseMarkdown
        var preprocessed = PreProcessStorageToMarkdown(storageHtml);

        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
            GithubFlavored = true,
            SmartHrefHandling = true
        });

        var markdown = converter.Convert(preprocessed);

        // Turn the inline-comment-marker sentinels we injected in pre-processing
        // into custom inline tags that Markdig treats as inline HTML (and thus wraps
        // in a <p>). HTML comments at column 0 would be parsed as a block and lose
        // that wrapping — Confluence then rejects the bare marker.
        markdown = CcOpenSentinelRegex().Replace(markdown, m => $"<ac-cc ref=\"{m.Groups[1].Value}\">");
        markdown = CcCloseSentinelRegex().Replace(markdown, "</ac-cc>");

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("confluence:");
        sb.AppendLine($"  id: \"{frontmatter.Id}\"");
        sb.AppendLine($"  version: {frontmatter.Version}");
        sb.AppendLine($"  space: {frontmatter.Space}");
        sb.AppendLine($"  title: \"{EscapeYaml(frontmatter.Title)}\"");
        if (!string.IsNullOrEmpty(frontmatter.ParentId))
            sb.AppendLine($"  parent_id: \"{frontmatter.ParentId}\"");
        sb.AppendLine($"  last_synced: \"{frontmatter.LastSynced:O}\"");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(markdown.TrimEnd());
        sb.AppendLine();

        return sb.ToString();
    }

    public string MarkdownToStorage(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(markdown, Pipeline);

        // Post-process: wrap fenced code blocks in ac:structured-macro
        html = PostProcessCodeBlocks(html);

        // Rewrite our custom <ac-cc ref="..."> inline-comment markers back to the
        // Confluence storage format so existing comment anchors are preserved.
        html = AcCcOpenTagRegex().Replace(html, m => $"<ac:inline-comment-marker ac:ref=\"{m.Groups[1].Value}\">");
        html = AcCcCloseTagRegex().Replace(html, "</ac:inline-comment-marker>");

        return html;
    }

    public ConfluenceFrontmatter? ParseFrontmatter(string markdownContent)
    {
        if (!markdownContent.StartsWith("---"))
            return null;

        var endIndex = markdownContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var yamlBlock = markdownContent.Substring(3, endIndex - 3).Trim();
        var frontmatter = new ConfluenceFrontmatter();

        foreach (var line in yamlBlock.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("id:"))
                frontmatter.Id = ExtractYamlValue(trimmed, "id:");
            else if (trimmed.StartsWith("version:"))
            {
                if (int.TryParse(ExtractYamlValue(trimmed, "version:"), out var v))
                    frontmatter.Version = v;
            }
            else if (trimmed.StartsWith("space:"))
                frontmatter.Space = ExtractYamlValue(trimmed, "space:");
            else if (trimmed.StartsWith("title:"))
                frontmatter.Title = ExtractYamlValue(trimmed, "title:");
            else if (trimmed.StartsWith("parent_id:"))
                frontmatter.ParentId = ExtractYamlValue(trimmed, "parent_id:");
            else if (trimmed.StartsWith("last_synced:"))
            {
                if (DateTime.TryParse(ExtractYamlValue(trimmed, "last_synced:"), out var dt))
                    frontmatter.LastSynced = dt;
            }
        }

        return string.IsNullOrEmpty(frontmatter.Id) ? null : frontmatter;
    }

    public string ExtractBody(string markdownContent)
    {
        if (!markdownContent.StartsWith("---"))
            return markdownContent;

        var endIndex = markdownContent.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return markdownContent;

        return markdownContent.Substring(endIndex + 4).TrimStart('\r', '\n');
    }

    /// <summary>
    /// Pre-processes Confluence storage format before passing to ReverseMarkdown.
    /// Converts ac:structured-macro code blocks into HTML pre/code blocks.
    /// </summary>
    private static string PreProcessStorageToMarkdown(string html)
    {
        // Inline-comment-markers (anchors for Confluence inline comments) must round-trip
        // intact or Confluence drops the anchor on the next PUT. Replace each pair with
        // plain-text sentinels that survive HTML→Markdown conversion; StorageToMarkdown
        // then rewrites them to HTML comments before writing to disk.
        html = AcInlineCommentMarkerRegex().Replace(html, match =>
        {
            var id = match.Groups[1].Value;
            var inner = match.Groups[2].Value;
            // No underscores in the sentinel — ReverseMarkdown escapes `_` to `\_` which
            // would break the post-conversion regex below.
            return $"\u2983CCOPEN{id}\u2984{inner}\u2983CCCLOSE\u2984";
        });

        // Convert ac:code macros to <pre><code>
        html = AcCodeMacroRegex().Replace(html, match =>
        {
            var lang = AcCodeLangRegex().Match(match.Value);
            var body = AcPlainTextBodyRegex().Match(match.Value);

            var language = lang.Success ? lang.Groups[1].Value : "";
            var code = body.Success ? body.Groups[1].Value : "";

            // Unescape CDATA
            code = code.Replace("]]]]><![CDATA[>", "]]>");

            return $"<pre><code class=\"language-{language}\">{System.Net.WebUtility.HtmlEncode(code)}</code></pre>";
        });

        // Convert ac:image to <img>
        html = AcImageRegex().Replace(html, match =>
        {
            var url = AcImageUrlRegex().Match(match.Value);
            var attachment = AcImageAttachmentRegex().Match(match.Value);

            if (url.Success)
                return $"<img src=\"{url.Groups[1].Value}\" />";
            if (attachment.Success)
                return $"<img src=\"{attachment.Groups[1].Value}\" />";

            return match.Value;
        });

        return html;
    }

    /// <summary>
    /// Post-processes HTML from Markdig to wrap code blocks in Confluence ac:code macros
    /// and convert image tags to Confluence attachment references.
    /// </summary>
    private static string PostProcessCodeBlocks(string html)
    {
        // Code blocks
        html = PreCodeRegex().Replace(html, match =>
        {
            var langMatch = CodeLangClassRegex().Match(match.Groups[1].Value);
            var language = langMatch.Success ? langMatch.Groups[1].Value : "";
            var code = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);

            var sb = new StringBuilder();
            sb.Append("<ac:structured-macro ac:name=\"code\">");
            if (!string.IsNullOrEmpty(language))
                sb.Append($"<ac:parameter ac:name=\"language\">{language}</ac:parameter>");
            sb.Append("<ac:plain-text-body><![CDATA[");
            sb.Append(code.Replace("]]>", "]]]]><![CDATA[>"));
            sb.Append("]]></ac:plain-text-body>");
            sb.Append("</ac:structured-macro>");
            return sb.ToString();
        });

        // Images: convert <img src="filename.png"> to Confluence attachment macro
        // Only for local filenames (no http:// URLs)
        // Uses filename only (no path) since Confluence attachments are flat per page
        html = ImgTagRegex().Replace(html, match =>
        {
            var src = match.Groups[1].Value;
            if (src.StartsWith("http://") || src.StartsWith("https://"))
                return match.Value; // Keep external URLs as-is

            var filename = Path.GetFileName(src);
            return $"<ac:image><ri:attachment ri:filename=\"{filename}\"/></ac:image>";
        });

        return html;
    }

    private static string ExtractYamlValue(string line, string prefix)
    {
        var value = line.Substring(prefix.Length).Trim();
        // Strip surrounding quotes
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1];
        return value;
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // Regex patterns for Confluence storage format parsing
    [GeneratedRegex(@"<ac:structured-macro[^>]*ac:name=""code""[^>]*>.*?</ac:structured-macro>", RegexOptions.Singleline)]
    private static partial Regex AcCodeMacroRegex();

    [GeneratedRegex(@"<ac:parameter ac:name=""language"">([^<]*)</ac:parameter>")]
    private static partial Regex AcCodeLangRegex();

    [GeneratedRegex(@"<ac:plain-text-body><!\[CDATA\[(.*?)\]\]></ac:plain-text-body>", RegexOptions.Singleline)]
    private static partial Regex AcPlainTextBodyRegex();

    [GeneratedRegex(@"<ac:image[^>]*>.*?</ac:image>", RegexOptions.Singleline)]
    private static partial Regex AcImageRegex();

    [GeneratedRegex(@"<ri:url ri:value=""([^""]*)""\s*/>")]
    private static partial Regex AcImageUrlRegex();

    [GeneratedRegex(@"<ri:attachment\s+ri:filename=""([^""]*)""[^>]*/>")]
    private static partial Regex AcImageAttachmentRegex();

    [GeneratedRegex(@"<pre><code([^>]*)>(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex PreCodeRegex();

    [GeneratedRegex(@"class=""language-(\w+)""")]
    private static partial Regex CodeLangClassRegex();

    [GeneratedRegex(@"<img\s+src=""([^""]+)""[^>]*/?>")]
    private static partial Regex ImgTagRegex();

    // Inline comment anchor round-trip.
    [GeneratedRegex(@"<ac:inline-comment-marker\s+ac:ref=""([^""]+)"">(.*?)</ac:inline-comment-marker>", RegexOptions.Singleline)]
    private static partial Regex AcInlineCommentMarkerRegex();

    [GeneratedRegex(@"\u2983CCOPEN([^\u2984]+)\u2984")]
    private static partial Regex CcOpenSentinelRegex();

    [GeneratedRegex(@"\u2983CCCLOSE\u2984")]
    private static partial Regex CcCloseSentinelRegex();

    [GeneratedRegex(@"<ac-cc\s+ref=""([^""]+)"">")]
    private static partial Regex AcCcOpenTagRegex();

    [GeneratedRegex(@"</ac-cc>")]
    private static partial Regex AcCcCloseTagRegex();
}
