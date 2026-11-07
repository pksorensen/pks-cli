using System.Diagnostics;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Agent.Setup;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Runs the real <c>codex</c> binary against what <see cref="CodexProfileWriter"/> produced.
///
/// <para>
/// <b>This is the only test that can fail for the right reason.</b> Everything else asserts the
/// shape of our own strings; this asserts that Codex accepts them. The failures it guards are all
/// ones where our TOML is perfectly well-formed and Codex still refuses it — a
/// <c>model_provider</c> naming no table, a legacy <c>profile</c> key, a marketplace with no
/// manifest — and each of those is fatal to the whole config rather than to one setting.
/// </para>
///
/// <para>
/// <b>These pass vacuously when <c>codex</c> is not on PATH</b>, which is the normal case in CI.
/// xUnit 2.6.2 has no runtime skip (<c>Assert.Skip</c> arrived later), so the early return reports
/// a pass rather than a skip — which means a green CI run is <i>not</i> evidence that these ran.
/// They are tagged <c>Integration</c> for that reason: treat them as a local gate, and run
/// <c>dotnet test --filter "FullyQualifiedName~CodexProfileRealBinaryTests"</c> on a machine with
/// codex installed before trusting a change to the TOML writer.
/// </para>
/// </summary>
[Trait(TestTraits.Category, TestCategories.Integration)]
[Trait(TestTraits.Speed, TestSpeed.Medium)]
[Collection("CodexHome")]
public sealed class CodexProfileRealBinaryTests : IDisposable
{
    private readonly string _home;
    private readonly string? _priorHome;

    public CodexProfileRealBinaryTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "pks-codex-real-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
        _priorHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", _priorHome);
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void Codex_LoadsTheConfigWeWrote_AndSeesTheProfilesServer()
    {
        if (!CodexOnPath()) return;

        // A config the reader already had — including a legacy selector, which on codex-cli
        // 0.163.0 is a hard error that also makes `-p` fail for EVERY profile.
        File.WriteAllText(Path.Combine(_home, "config.toml"), """
            model_reasoning_effort = "high"
            profile = "legacy-brick"

            [desktop]
            theme = "dark"

            [mcp_servers.github]
            command = "echo"
            args = ["github"]
            """);

        // Proves the fixture really is broken, so a pass below means we repaired it.
        Run("mcp", "list").ExitCode.Should().NotBe(0, "the legacy selector should brick the base config");

        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(
            new CodexApplyRequest(
                "https://agentics.dk", "agentics", "Agentics Factory",
                "https://login.agentics.dk/realms/agentics",
                "https://agentics.dk/oauth/clients/pks-cli.json",
                new AspProfile
                {
                    Id = "my-project",
                    DisplayName = "My Project",
                    McpServers = [new AspMcpServer { Name = "agentics-project", Url = "https://agentics.dk/p/poul/my-project/mcp" }],
                },
                MakeActive: false),
            ledger);

        // 1. The base config loads again, and the reader's own server survived.
        var bare = Run("mcp", "list");
        bare.ExitCode.Should().Be(0, $"we should have repaired the config; got: {bare.Output}");
        bare.Output.Should().Contain("github");

        // 2. The profile layers, and our server appears under it.
        var layered = Run("-p", "agentics-my-project", "mcp", "list");
        layered.ExitCode.Should().Be(0, $"the profile should load; got: {layered.Output}");
        layered.Output.Should().Contain("agentics-project");
        layered.Output.Should().Contain("agentics.dk/p/poul/my-project/mcp");

        // 3. Codex merges the base in, so the reader's own server is there too. This is the
        //    union documented in the spec — asserted so a change in Codex shows up here.
        layered.Output.Should().Contain("github");

        // 4. Codex Desktop's table is untouched.
        File.ReadAllText(Path.Combine(_home, "config.toml")).Should().Contain("[desktop]");
    }

    [Fact]
    public void Codex_AcceptsAProfileThatCarriesNoModelProvider()
    {
        if (!CodexOnPath()) return;

        var ledger = new CodexProfileLedger();
        CodexProfileWriter.Apply(
            new CodexApplyRequest(
                "https://agentics.dk", "agentics", "Agentics Factory", null, null,
                new AspProfile
                {
                    Id = "ctx",
                    McpServers = [new AspMcpServer { Name = "p", Url = "https://agentics.dk/p/a/b/mcp" }],
                },
                MakeActive: true),
            ledger);

        // The specific trap: writing `model_provider` for a profile with no provider table fails
        // EVERY invocation with "Model provider `x` not found", not just one thread.
        var result = Run("mcp", "list");
        result.ExitCode.Should().Be(0, $"a provider-less profile must still load; got: {result.Output}");
        result.Output.Should().Contain("https://agentics.dk/p/a/b/mcp");
    }

    private static bool CodexOnPath()
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo("codex", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (probe is null) return false;
            probe.WaitForExit(20_000);

            return probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("codex")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["CODEX_HOME"] = _home;

        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(60_000);

        return (proc.ExitCode, output);
    }

    [Fact]
    public void Codex_StartsAgainAfterWeRepairADanglingModelProvider()
    {
        if (!CodexOnPath()) return;

        // Asserts the repair against the tool rather than against our own string: the claim is
        // "Codex refuses to load this", and only Codex can confirm it.
        File.WriteAllText(Path.Combine(_home, "config.toml"),
            "model_provider = \"azure\"\n\n[mcp_servers.github]\ncommand = \"echo\"\nargs = [\"github\"]\n");

        var broken = Run("mcp", "list");
        broken.ExitCode.Should().NotBe(0, "a dangling model_provider should fail the whole load");
        broken.Output.Should().Contain("azure");

        var ledger = new CodexProfileLedger();
        var result = CodexProfileWriter.Apply(
            new CodexApplyRequest(
                "https://agentics.dk", "agentics", "Agentics Factory", null, null,
                new AspProfile
                {
                    Id = "p",
                    McpServers = [new AspMcpServer { Name = "a", Url = "https://agentics.dk/p/a/b/mcp" }],
                },
                MakeActive: false),
            ledger);

        result.Notes.Should().Contain(n => n.Contains("model_provider", StringComparison.Ordinal));

        var fixedUp = Run("mcp", "list");
        fixedUp.ExitCode.Should().Be(0, $"we should have repaired it; got: {fixedUp.Output}");
        fixedUp.Output.Should().Contain("github", "the reader's own server survives the repair");
    }
}
