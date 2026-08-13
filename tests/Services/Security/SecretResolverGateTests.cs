using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

/// <summary>
/// The actual enforcement behind "secrets cannot be read outside the interfaces pks exposes".
///
/// pks is one assembly, so <c>internal</c> buys nothing — a command could take an
/// <c>ISecretResolver</c> and print what it returns, and the compiler would be perfectly happy. What
/// stops that is this test: the layers whose output lands in a terminal, a transcript, or an agent's
/// context may not name the plaintext API at all. It is a source scan because that is the only thing
/// that can express the rule.
///
/// If you are here because this test failed: do not add an exception. Move the work that needs the
/// credential into a service under <c>Infrastructure/Services/</c> and have the command ask that
/// service to do the thing — the way <c>AgenticsRunnerStartCommand</c> asks
/// <c>IAgenticsRunnerSshHandoffService.ForwardStoredSecretAsync</c> to forward a token it never sees.
/// </summary>
public class SecretResolverGateTests
{
    private static readonly string[] ForbiddenIn =
    {
        Path.Combine("src", "Commands"),
        Path.Combine("src", "Infrastructure", "Services", "MCP"),
    };

    private static readonly Regex PlaintextApi = new(
        @"\bISecretResolver\b|\bRevealAsync\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "pks-cli.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the gate test has to find the repository root to scan sources");
        return dir!.FullName;
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void CommandAndMcpLayers_CannotReachPlaintextSecrets()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var relative in ForbiddenIn)
        {
            var directory = Path.Combine(root, relative);
            Directory.Exists(directory).Should().BeTrue($"'{relative}' must exist for the gate to mean anything");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                foreach (Match match in PlaintextApi.Matches(text))
                {
                    // The doc comments that explain the rule are allowed to name it.
                    var lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
                    var line = text[lineStart..text.IndexOf('\n', match.Index)].TrimStart();
                    if (line.StartsWith("//") || line.StartsWith("///") || line.StartsWith("*")) continue;

                    var lineNumber = text[..match.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "the command and MCP layers must not be able to obtain a credential in plaintext");
    }
}
