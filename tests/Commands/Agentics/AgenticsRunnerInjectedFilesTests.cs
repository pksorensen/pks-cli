using FluentAssertions;
using PKS.Commands.Agentics.Runner;
using System.Diagnostics;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsRunnerInjectedFilesTests
{
    [Fact]
    public void RunnerGitExcludePatterns_IncludeEnvironmentAndStageFiles()
    {
        var patterns = AgenticsRunnerRunCommand.RunnerGitExcludePatterns(new[]
        {
            ".devcontainer/devcontainer.json",
            @".devcontainer\feature.json",
            "../outside.json",
        });

        patterns.Should().Contain(new[]
        {
            "CLAUDE.md",
            ".claude/settings.json",
            ".claude/settings.local.json",
            ".claude/.gitignore",
            ".mcp.json",
            ".devcontainer/override-config-devcontainer-*.json",
            ".devcontainer/devcontainer.json",
            ".devcontainer/feature.json",
        });
        patterns.Should().NotContain("../outside.json");
    }

    [Fact]
    public void BuildRunnerGitExcludeScript_PreservesTrackedFilesAndQuotesPaths()
    {
        var script = AgenticsRunnerRunCommand.BuildRunnerGitExcludeScript(new[]
        {
            ".devcontainer/it's-safe.json",
        });

        script.Should().Contain("git rev-parse --git-path info/exclude");
        script.Should().Contain("git ls-files --error-unmatch");
        script.Should().Contain("&& continue");
        script.Should().Contain("grep -qxF");
        script.Should().Contain("'.devcontainer/it'\"'\"'s-safe.json'");
    }

    [Fact]
    public void BuildRunnerGitExcludeScript_LeavesOnlyRealRepositoryWorkVisible()
    {
        if (OperatingSystem.IsWindows()) return;

        var repo = Path.Combine(Path.GetTempPath(), "runner-excludes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Run(repo, "git", "init", "-q");
            Run(repo, "git", "config", "user.name", "Runner Test");
            Run(repo, "git", "config", "user.email", "runner@example.invalid");
            File.WriteAllText(Path.Combine(repo, ".mcp.json"), "{}\n");
            Run(repo, "git", "add", ".mcp.json");
            Run(repo, "git", "commit", "-qm", "baseline");

            Directory.CreateDirectory(Path.Combine(repo, ".claude"));
            Directory.CreateDirectory(Path.Combine(repo, ".devcontainer"));
            File.WriteAllText(Path.Combine(repo, "CLAUDE.md"), "runner\n");
            File.WriteAllText(Path.Combine(repo, ".claude", "settings.json"), "{}\n");
            File.WriteAllText(Path.Combine(repo, ".claude", "settings.local.json"), "{}\n");
            File.WriteAllText(Path.Combine(repo, ".claude", ".gitignore"), "settings.local.json\n");
            File.WriteAllText(Path.Combine(repo, ".devcontainer", "devcontainer.json"), "{}\n");
            File.WriteAllText(Path.Combine(repo, ".devcontainer", "override-config-devcontainer-test.json"), "{}\n");
            File.WriteAllText(Path.Combine(repo, "keep.txt"), "real work\n");

            var script = AgenticsRunnerRunCommand.BuildRunnerGitExcludeScript(new[]
            {
                ".devcontainer/devcontainer.json",
            });
            Run(repo, "bash", "-c", script);

            Run(repo, "git", "status", "--short", "--untracked-files=all")
                .Trim()
                .Should().Be("?? keep.txt");
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static string Run(string workingDirectory, string fileName, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"{fileName} {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
