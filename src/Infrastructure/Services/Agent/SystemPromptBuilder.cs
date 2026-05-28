using System.Text;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Builds the system prompt sent to the model on each turn.
/// Ported from pi/src/core/system-prompt.ts — simplified.
/// </summary>
public static class SystemPromptBuilder
{
    /// <summary>
    /// Construct a system prompt. If <paramref name="customPrompt"/> is non-empty,
    /// it REPLACES the default body (skill mode) but context files, date, and cwd
    /// are still appended.
    /// </summary>
    public static string Build(
        string cwd,
        IReadOnlyList<string> toolNames,
        string? customPrompt = null,
        IReadOnlyList<(string Path, string Content)>? contextFiles = null,
        DateTimeOffset? now = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(customPrompt))
        {
            sb.Append(customPrompt);
        }
        else
        {
            sb.AppendLine("You are an expert coding assistant operating inside pks-cli. You help users by reading files, executing commands, editing code, and writing new files.");
            sb.AppendLine();
            sb.AppendLine("Available tools:");
            foreach (var t in toolNames)
            {
                sb.Append("- ");
                sb.AppendLine(t);
            }
            sb.AppendLine();
            sb.AppendLine("Guidelines:");
            sb.AppendLine("- Be concise in your responses");
            sb.AppendLine("- Show file paths clearly when working with files");
            sb.AppendLine("- Use tools rather than asking the user to run commands");
        }

        if (contextFiles is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("<project_context>");
            sb.AppendLine();
            sb.AppendLine("Project-specific instructions and guidelines:");
            sb.AppendLine();
            foreach (var (path, content) in contextFiles)
            {
                sb.Append("<project_instructions path=\"");
                sb.Append(path);
                sb.AppendLine("\">");
                sb.AppendLine(content);
                sb.AppendLine("</project_instructions>");
                sb.AppendLine();
            }
            sb.AppendLine("</project_context>");
        }

        var d = (now ?? DateTimeOffset.Now).ToLocalTime();
        sb.AppendLine();
        sb.Append("Current date: ");
        sb.AppendLine(d.ToString("yyyy-MM-dd"));
        sb.Append("Current working directory: ");
        sb.AppendLine(cwd.Replace('\\', '/'));

        return sb.ToString();
    }

    /// <summary>
    /// Load CLAUDE.md / AGENTS.md from <paramref name="cwd"/> if they exist,
    /// returning the pairs to feed into <see cref="Build"/>.
    /// </summary>
    public static List<(string Path, string Content)> LoadContextFiles(string cwd)
    {
        var result = new List<(string, string)>();
        foreach (var name in new[] { "CLAUDE.md", "AGENTS.md" })
        {
            var p = Path.Combine(cwd, name);
            if (File.Exists(p))
            {
                result.Add((name, File.ReadAllText(p)));
            }
        }
        return result;
    }
}
