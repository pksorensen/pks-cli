using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// Source-level convention tests for the agentics runner.
/// These guard against regressions of architectural decisions that the type system
/// cannot enforce — e.g. "never target /tmp via Docker's archive endpoint".
/// </summary>
public class AdrComplianceTests
{
    private static string FindRunnerCommandFile()
    {
        // Walk up from test bin/ to repo root, then into src/Commands/Agentics/Runner/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "pks-cli.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "src", "Commands", "Agentics", "Runner", "AgenticsRunnerStartCommand.cs");
        File.Exists(path).Should().BeTrue($"expected source at {path}");
        return path;
    }

    /// <summary>
    /// ADR 0006: Docker's archive endpoint cannot write to tmpfs/volume/bind-mount paths.
    /// Under dind (our spawn-mode containers), /tmp is tmpfs. Every WriteContainerFileAsync /
    /// CopyFileToContainerAsync / ExtractArchiveToContainerAsync target MUST be overlay-backed —
    /// in practice, under $HOME or $vibecastHome. Hard-coded /tmp/... destinations silently
    /// no-op and produce ghost-file bugs hours later (regressed once in May 2026 — the OTLP
    /// bridge silently dropped, killing the per-session cost chip for weeks).
    /// </summary>
    [Fact]
    public void Runner_does_not_write_to_tmp_via_archive_endpoint()
    {
        var source = File.ReadAllText(FindRunnerCommandFile());

        // Banned call shapes — we'll inspect the first arg of each.
        var callPatterns = new[]
        {
            // WriteContainerFileAsync(firstArg, ...)  — first arg is the path
            new Regex(@"WriteContainerFileAsync\s*\(\s*([^,\s\)]+)", RegexOptions.Compiled),
            // CopyFileToContainerAsync(containerId, pathArg, ...) — path is the SECOND arg
            new Regex(@"CopyFileToContainerAsync\s*\([^,]+,\s*([^,\s\)]+)", RegexOptions.Compiled),
            // ExtractArchiveToContainerAsync(containerId, pathArg, ...) — same shape
            new Regex(@"ExtractArchiveToContainerAsync\s*\([^,]+,\s*([^,\s\)]+)", RegexOptions.Compiled),
        };

        // Variable-assignment scan: catches `var foo = "/tmp/..."` and `const string foo = "/tmp/..."`
        // and `var foo = $"/tmp/{...}"`. We trace each banned-call's first-arg identifier back here.
        var assignmentPattern = new Regex(
            @"\b(?:const\s+string|string|var)\s+(\w+)\s*=\s*\$?""(/tmp/[^""]*)""",
            RegexOptions.Compiled);

        var tmpLiteralVars = new HashSet<string>();
        foreach (Match m in assignmentPattern.Matches(source))
            tmpLiteralVars.Add(m.Groups[1].Value);

        var failures = new List<string>();

        foreach (var pat in callPatterns)
        {
            foreach (Match m in pat.Matches(source))
            {
                var arg = m.Groups[1].Value;
                var line = source.Take(m.Index).Count(c => c == '\n') + 1;

                // Inline string literal — banned directly.
                if (arg.StartsWith("\"/tmp/") || arg.StartsWith("$\"/tmp/"))
                {
                    failures.Add($"line {line}: inline \"/tmp/...\" passed to {pat}");
                    continue;
                }

                // Identifier — check if it was assigned a "/tmp/..." literal.
                var ident = arg.TrimStart('@'); // handles @verbatim identifiers
                if (tmpLiteralVars.Contains(ident))
                    failures.Add($"line {line}: argument '{ident}' was assigned a \"/tmp/...\" literal earlier in the file");
            }
        }

        failures.Should().BeEmpty(
            "ADR 0006 forbids /tmp as the destination for Docker archive transfers " +
            "(tmpfs under dind is invisible to the archive endpoint). " +
            "Move the destination under $vibecastHome / $HOME (overlay-backed) instead. " +
            "Offenders:\n  " + string.Join("\n  ", failures));
    }
}
