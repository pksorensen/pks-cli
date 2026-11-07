using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Codex;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// The ledger is the CLI's only memory of what it connected. If a round trip through it loses a
/// profile, every no-network command ("profiles", "use", "remove") reports an empty machine and
/// the reader's only recourse is to connect again — so these guard the round trip itself.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public sealed class CodexProfileLedgerTests
{
    [Fact]
    public void SaveThenLoad_KeepsEveryFieldTheCommandsRelyOn()
    {
        // Documents where the file actually lands, which is the thing an isolated manual run
        // gets wrong: the path comes from the user profile, not from CODEX_HOME.
        var path = CodexProfileLedger.Path();
        path.Should().EndWith("codex-profiles.json");

        var ledger = new CodexProfileLedger { Active = "agentics-my-project" };
        ledger.Sources["https://agentics.dk"] = new CodexLedgerSource
        {
            Id = "agentics",
            DisplayName = "Agentics Factory",
            Issuer = "https://login.agentics.dk/realms/agentics",
            Profiles =
            {
                ["my-project"] = new CodexLedgerProfile
                {
                    Id = "my-project",
                    Namespace = "agentics-my-project",
                    LayerFile = "agentics-my-project.config.toml",
                    LayerTables = ["mcp_servers.agentics-project"],
                    BaseTables = ["marketplaces.agentics-skills"],
                },
            },
        };

        // Round trip through the real serializer options, without touching the real file.
        var json = System.Text.Json.JsonSerializer.Serialize(ledger, LedgerJson());
        var back = System.Text.Json.JsonSerializer.Deserialize<CodexProfileLedger>(json, LedgerJson())!;

        back.Active.Should().Be("agentics-my-project");
        var row = back.AllProfiles().Should().ContainSingle().Subject;

        // The origin is a dictionary KEY: a camelCase policy applied to keys would corrupt it,
        // and `connect --all` re-reads exactly this string to know what to re-fetch.
        row.Origin.Should().Be("https://agentics.dk");
        row.Source.Id.Should().Be("agentics");
        row.Profile.Namespace.Should().Be("agentics-my-project");
        row.Profile.LayerTables.Should().ContainSingle().Which.Should().Be("mcp_servers.agentics-project");
        row.Profile.BaseTables.Should().ContainSingle().Which.Should().Be("marketplaces.agentics-skills");
    }

    [Fact]
    public void Load_AcceptsPascalCase_SoAnUpgradeDoesNotLoseProfiles()
    {
        // Earlier builds wrote PascalCase. Reading must be case-insensitive or an upgrade looks
        // exactly like "nothing is connected" — the worst possible failure for a state file.
        const string pascal = """
            {"V":1,"Active":"agentics-x","Sources":{"https://agentics.dk":{"Id":"agentics",
            "Profiles":{"x":{"Id":"x","Namespace":"agentics-x","LayerFile":"agentics-x.config.toml"}}}}}
            """;

        var back = System.Text.Json.JsonSerializer.Deserialize<CodexProfileLedger>(pascal, LedgerJson())!;

        back.Active.Should().Be("agentics-x");
        back.AllProfiles().Should().ContainSingle()
            .Which.Profile.LayerFile.Should().Be("agentics-x.config.toml");
    }

    private static System.Text.Json.JsonSerializerOptions LedgerJson() => new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Load_MovesACorruptLedgerAside_RatherThanLettingItBeOverwritten()
    {
        // The data-loss path: Load() returns empty on a parse failure (correct — a CLI that
        // cannot run is worse than one that has forgotten), but the next Save() would then write
        // {} over the reader's only record of every profile they ever connected. Observed for
        // real: a failed `connect` left {"V":1,"Sources":{}} behind in a live ~/.pks-cli.
        //
        // Isolated by moving HOME, which .NET's SpecialFolder.UserProfile re-reads per call
        // (verified: it even returns empty for a HOME that does not exist). Backing the real
        // ledger up and restoring it would be one crash away from destroying the thing under test.
        var prior = Environment.GetEnvironmentVariable("HOME");
        var home = Path.Combine(Path.GetTempPath(), "pks-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);

        try
        {
            var path = CodexProfileLedger.Path();
            path.Should().StartWith(home, "the test must not be able to reach the real ledger");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");

            var loaded = CodexProfileLedger.Load();

            loaded.AllProfiles().Should().BeEmpty("an unreadable ledger must not stop the CLI");
            File.Exists(path).Should().BeFalse("the unreadable file is moved, not copied");
            var aside = Directory.GetFiles(Path.GetDirectoryName(path)!, "codex-profiles.json.corrupt-*")
                .Should().ContainSingle("the bytes must be kept, not dropped").Subject;

            File.ReadAllText(aside).Should().Be("{ this is not json", "kept verbatim, for recovery");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prior);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

}
