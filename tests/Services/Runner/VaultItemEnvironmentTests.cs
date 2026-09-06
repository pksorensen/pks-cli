using FluentAssertions;
using PKS.Commands.Agentics.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// The variables a station's container gets for its vault grants
/// (<see cref="AgenticsRunnerRunCommand.BuildVaultItemEnvironment"/>).
///
/// Half of this is a contract with code that cannot be compiled against: www-site derives the
/// same names in <c>src/lib/vault/station-requests.ts</c> (<c>itemEnvName</c>), and its test names
/// the same examples. When the two drift, a station's system.md spells a variable nobody sets,
/// which fails as an empty <c>--item</c> flag rather than as an error — so the examples below are
/// the contract, not illustrations.
/// </summary>
public class VaultItemEnvironmentTests
{
    private static AgenticsRunnerRunCommand.VaultAccessDefinition Access(
        string itemId, params (string Name, string ItemId)[] items) => new()
        {
            Server = "https://vault.example",
            Owner = "acme",
            VaultId = "vlt_1",
            ItemId = itemId,
            Items = items.Length == 0
                ? null
                : [.. items.Select(i => new AgenticsRunnerRunCommand.VaultItemDefinition
                {
                    Name = i.Name,
                    ItemId = i.ItemId,
                })],
        };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnvName_UppercasesAndPrefixes()
    {
        AgenticsRunnerRunCommand.VaultItemEnvName("mitid").Should().Be("VAULT_ITEM_MITID");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnvName_CollapsesAnythingThatCannotBeInAVariable()
    {
        AgenticsRunnerRunCommand.VaultItemEnvName("bank-login").Should().Be("VAULT_ITEM_BANK_LOGIN");
        AgenticsRunnerRunCommand.VaultItemEnvName("e-conomic api.key")
            .Should().Be("VAULT_ITEM_E_CONOMIC_API_KEY");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnvName_LeavesNoLeadingOrTrailingUnderscore()
    {
        AgenticsRunnerRunCommand.VaultItemEnvName("-mitid-").Should().Be("VAULT_ITEM_MITID");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnvName_IsNullWhenNothingSurvives()
    {
        AgenticsRunnerRunCommand.VaultItemEnvName("").Should().BeNull();
        AgenticsRunnerRunCommand.VaultItemEnvName("---").Should().BeNull();
    }

    /// <summary>
    /// The shape every line repo written before <c>items</c> existed sends. It must keep getting
    /// exactly <c>VAULT_ITEM_ID</c> and nothing else — a <c>VAULT_ITEMS</c> here would tempt a
    /// station into looping over a name that was never recorded.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ScalarOnly_GetsTodaysSingleVariable()
    {
        var (env, warnings) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(Access("itm_1"));

        env.Should().Equal(new Dictionary<string, string> { ["VAULT_ITEM_ID"] = "itm_1" });
        warnings.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void SeveralItems_GetANamedVariableEachAndAListToLoopOver()
    {
        var (env, warnings) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(
            Access("itm_1", ("mitid", "itm_1"), ("bank-login", "itm_2")));

        env["VAULT_ITEM_ID"].Should().Be("itm_1");
        env["VAULT_ITEM_MITID"].Should().Be("itm_1");
        env["VAULT_ITEM_BANK_LOGIN"].Should().Be("itm_2");
        // The slugs, not the names: this is the list a station expands with
        // `eval "id=\$VAULT_ITEM_$n"`, so the original spelling would resolve to nothing.
        env["VAULT_ITEMS"].Should().Be("MITID BANK_LOGIN");
        warnings.Should().BeEmpty();
    }

    /// <summary>
    /// The platform deduplicates by name, and <c>bank-login</c> and <c>bank_login</c> are two
    /// names — so the collision only becomes visible here. Overwriting would hand the station the
    /// second item's id under a variable its prompt believes names the first.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void TwoNamesThatCollideKeepTheFirstAndSaySo()
    {
        var (env, warnings) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(
            Access("itm_1", ("bank-login", "itm_1"), ("bank_login", "itm_2")));

        env["VAULT_ITEM_BANK_LOGIN"].Should().Be("itm_1");
        env["VAULT_ITEMS"].Should().Be("BANK_LOGIN");
        warnings.Should().ContainSingle().Which.Should().Contain("VAULT_ITEM_BANK_LOGIN");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void AnUnnamedItemAmongSeveralIsReportedRatherThanGuessedAt()
    {
        var (env, warnings) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(
            Access("itm_1", ("mitid", "itm_1"), ("", "itm_2")));

        env.Should().NotContainKey("VAULT_ITEM_");
        env["VAULT_ITEMS"].Should().Be("MITID");
        warnings.Should().ContainSingle().Which.Should().Contain("itm_2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void AnEmptyPointerSetsNothing()
    {
        var (env, warnings) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(Access(""));

        env.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    /// <summary>
    /// A station whose items were filled in by hand may carry a list but no scalar. It still has
    /// to get <c>VAULT_ITEM_ID</c>, because that is the flag its system.md spells.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ItemsWithoutAScalarStillFillTheScalar()
    {
        var (env, _) = AgenticsRunnerRunCommand.BuildVaultItemEnvironment(
            Access("", ("mitid", "itm_9")));

        env["VAULT_ITEM_ID"].Should().Be("itm_9");
    }

    private static AgenticsRunnerRunCommand.VaultEnrolmentDefinition Enrolment() => new()
    {
        AgentId = "agt_1",
        Token = "tok_secret",
        OwnerHolder = "usr_1",
        OwnerSignPub = "sign+pub",
        OwnerRecipient = "age1abc",
        Anchor = "anch",
        HostLabel = "station 01",
    };

    /// <summary>
    /// <c>vault agent enrol</c> refuses to overwrite an identity, so a second run would fail the
    /// script rather than skip it. The guard is what makes this safe to attempt on every dispatch.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnrolmentScript_SkipsItselfWhenTheIdentityIsAlreadyThere()
    {
        var script = AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
            Access("itm_1"), Enrolment(), "/vault/identity.json", null, null);

        script.Should().Contain("if [ ! -f \"$IDENTITY\" ]; then");
        script.Should().Contain("vault-identity-present");
        script.Should().Contain("IDENTITY='/vault/identity.json'");
    }

    /// <summary>
    /// A passphrase would have to travel with the job and live in the same place as the file it
    /// protects. The volume is the protection here, not a secret nobody types.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnrolmentScript_EnrolsUnattended()
    {
        var script = AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
            Access("itm_1"), Enrolment(), "/vault/identity.json", null, null);

        script.Should().Contain("--protection none");
        script.Should().Contain("--agent 'agt_1'");
        script.Should().Contain("--token 'tok_secret'");
        script.Should().Contain("--host-label 'station 01'");
        // Installed from the same place the station's own prompt names, because a repo's
        // devcontainer has no reason to carry it.
        script.Should().Contain("https://agentics.dk/install/vault.sh");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnrolmentScript_StoresAServiceAccountOnlyWhenItHasBothHalves()
    {
        var without = AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
            Access("itm_1"), Enrolment(), "/vault/identity.json", "vault-agent", null);
        without.Should().NotContain("vault agent credentials");

        var with = AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
            Access("itm_1"), Enrolment(), "/vault/identity.json", "vault-agent", "s3cret");
        with.Should().Contain("vault agent credentials");
        with.Should().Contain("--client-secret 's3cret'");
    }

    /// <summary>
    /// The station's user can read the file while it exists, so it does not outlive the exec — and
    /// the caller's own cleanup does not run when the exec times out.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnrolmentScript_DeletesItself()
    {
        AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
                Access("itm_1"), Enrolment(), "/vault/identity.json", null, null)
            .Should().Contain("rm -f -- \"$0\"");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void EnrolmentScript_QuotesAValueThatWouldOtherwiseCloseTheQuote()
    {
        var enrolment = Enrolment();
        enrolment.HostLabel = "it's a station";

        AgenticsRunnerRunCommand.BuildVaultEnrolmentScript(
                Access("itm_1"), enrolment, "/vault/identity.json", null, null)
            .Should().Contain("--host-label 'it'\\''s a station'");
    }
}
