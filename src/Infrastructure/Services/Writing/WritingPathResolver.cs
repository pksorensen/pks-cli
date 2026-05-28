using System.Security.Cryptography;
using System.Text;

namespace PKS.Infrastructure.Services.Writing;

public sealed class WritingPathResolver : IWritingPathResolver
{
    private readonly string _home;

    public WritingPathResolver()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// Test seam: inject a fake home directory.
    public WritingPathResolver(string home)
    {
        _home = home;
    }

    public string GlobalRoot => Path.Combine(_home, ".pks-cli", "writing");
    public string GlobalProfilePath => Path.Combine(GlobalRoot, "profile.md");
    public string GlobalAnglicismsPath => Path.Combine(GlobalRoot, "anglicisms.txt");
    public string GlobalAllowlistPath => Path.Combine(GlobalRoot, "allowlist.txt");
    public string GlobalCalquesPath => Path.Combine(GlobalRoot, "calques.txt");
    public string GlobalChannelsDir => Path.Combine(GlobalRoot, "channels");
    public string GlobalChannelRubricPath(string channel) =>
        Path.Combine(GlobalChannelsDir, channel + ".md");

    public string GlobalValeDir => Path.Combine(GlobalRoot, "vale");
    public string GlobalValeBinDir => Path.Combine(GlobalValeDir, "bin");
    public string GlobalValeConfigPath => Path.Combine(GlobalValeDir, ".vale.ini");
    public string GlobalValeStylesDir => Path.Combine(GlobalValeDir, "styles");

    public string GlobalReferenceDir => Path.Combine(GlobalRoot, "reference");
    public string GlobalReferenceChannelDir(string channel) =>
        Path.Combine(GlobalReferenceDir, channel);

    public string GlobalLessonsPath => Path.Combine(GlobalRoot, "lessons.md");
    public string GlobalAuthoringPromptPath => Path.Combine(GlobalRoot, "AUTHORING-PROMPT.md");

    public string? ResolveProjectRoot(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var dir = new DirectoryInfo(Path.GetFullPath(cwd));
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Path.Combine(dir.FullName, ".pks", "writing");
            }
            dir = dir.Parent;
        }
        return null;
    }

    public string ProjectChannelConfigPath(string projectRoot) =>
        Path.Combine(projectRoot, "channel.json");

    public string ProjectOverridesAnglicismsPath(string projectRoot) =>
        Path.Combine(projectRoot, "overrides", "anglicisms.txt");

    public string ProjectReportsDir(string projectRoot) =>
        Path.Combine(projectRoot, "reports");

    public string ProjectReportCachePath(string projectRoot, string sourceFilePath) =>
        Path.Combine(ProjectReportsDir(projectRoot), HashPath(sourceFilePath) + ".json");

    public string ReviewDir(string sourceFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
                  ?? throw new ArgumentException("source must have a directory", nameof(sourceFilePath));
        return Path.Combine(dir, "_review");
    }

    public string ReportSidecarMarkdownPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.WRITING-REPORT.md");

    public string ReportSidecarJsonPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.WRITING-REPORT.json");

    public string LearnSidecarMarkdownPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.LEARN.md");

    public string LearnSidecarJsonPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.LEARN.json");

    public string NaturalnessCandidatesSidecarPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.NATURALNESS-CANDIDATES.json");

    public string NaturalnessPicksSidecarPath(string sourceFilePath) =>
        Path.Combine(ReviewDir(sourceFilePath),
            $"{Path.GetFileNameWithoutExtension(sourceFilePath)}.NATURALNESS-PICKS.json");

    public string GlobalNaturalnessPatternsPath =>
        Path.Combine(GlobalRoot, "naturalness-patterns.md");

    private static string HashPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var hash = SHA1.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString()[..16];
    }
}
