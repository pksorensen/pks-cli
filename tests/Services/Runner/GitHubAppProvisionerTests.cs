using System.Text.Json;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// A vault that answers from memory. Records every read so a test can assert that a secret was
/// fetched exactly once, and that nothing else went looking for it.
/// </summary>
internal sealed class FakeVaultCli : IVaultCliService
{
    public string? Binary { get; set; } = "/usr/bin/vault";
    public VaultAgentIdentity? Identity { get; set; }
    public List<VaultGrant> Grants { get; } = new();
    public string FieldValue { get; set; } = "-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----";
    public List<(string VaultId, string ItemId, string Field)> Reads { get; } = new();

    public string? FindBinary() => Binary;

    public Task<VaultAgentIdentity?> WhoAmIAsync(string identityPath, CancellationToken ct = default) =>
        Task.FromResult(Identity);

    public Task<IReadOnlyList<VaultGrant>> GrantsAsync(string identityPath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VaultGrant>>(Grants);

    public Task<string> ReadFieldAsync(
        string identityPath, string vaultId, string itemId, string field,
        TimeSpan wait, Action<string>? onNotice = null, CancellationToken ct = default)
    {
        Reads.Add((vaultId, itemId, field));

        return Task.FromResult(FieldValue);
    }
}

public class GitHubAppProvisionerTests
{
    private const string Server = "https://vault.agentics.dk";

    private static readonly GitHubAppPointer Pointer =
        new("1234567", "si14-x", Server, "pksorensen", "vlt_1", "itm_2", null);

    private static VaultAgentIdentity Enrolled(string server = Server) =>
        new("agt_abc", "pksorensen", server, "none", "/id.json", HasServiceAccount: true);

    private static VaultGrant UsableGrant(string consentMode = "never") => new()
    {
        VaultId = "vlt_1",
        ItemId = "itm_2",
        Usable = true,
        ConsentMode = consentMode,
        ExpiresAt = "2027-01-01T00:00:00Z",
    };

    private static FakeVaultCli ReadyVault()
    {
        var vault = new FakeVaultCli { Identity = Enrolled() };
        vault.Grants.Add(UsableGrant());

        return vault;
    }

    /// <summary>No GITHUB_APP_* in the environment, whatever the test host happens to have set.</summary>
    private static string? NoEnvironment(string _) => null;

    [Fact]
    public async Task Fetches_the_key_from_the_vault_and_keeps_the_public_identity_from_the_pointer()
    {
        var vault = ReadyVault();

        var config = await new GitHubAppProvisioner(vault)
            .ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment);

        Assert.NotNull(config);
        Assert.Equal("1234567", config!.AppId);
        Assert.Equal("si14-x", config.Slug);
        Assert.Contains("PRIVATE KEY", config.PrivateKeyPem);
        // Read once, by id, from the field the pointer named.
        Assert.Equal(("vlt_1", "itm_2", "private_key"), Assert.Single(vault.Reads));
    }

    [Fact]
    public async Task Honours_a_custom_field_name()
    {
        var vault = ReadyVault();
        var pointer = Pointer with { Field = "pem" };

        await new GitHubAppProvisioner(vault).ResolveAsync(pointer, "/id.json", readVariable: NoEnvironment);

        Assert.Equal("pem", Assert.Single(vault.Reads).Field);
    }

    [Fact]
    public async Task No_pointer_and_no_environment_means_no_app_and_no_vault_traffic()
    {
        var vault = ReadyVault();

        var config = await new GitHubAppProvisioner(vault)
            .ResolveAsync(null, "/id.json", readVariable: NoEnvironment);

        Assert.Null(config);
        Assert.Empty(vault.Reads);
    }

    [Fact]
    public async Task The_environment_wins_when_it_is_the_only_declaration()
    {
        // The wrapper form -- `vault agent run --file … -- pks …` -- still exports GITHUB_APP_*.
        // It has to keep working; this is what says so.
        var vault = ReadyVault();

        var config = await new GitHubAppProvisioner(vault).ResolveAsync(null, "/id.json", readVariable: name => name switch
        {
            GitHubAppConfigResolver.AppIdVariable => "999",
            GitHubAppConfigResolver.SlugVariable => "from-env",
            GitHubAppConfigResolver.PrivateKeyVariable => "-----BEGIN RSA PRIVATE KEY-----\\nenv\\n",
            _ => null,
        });

        Assert.Equal("999", config!.AppId);
        Assert.Empty(vault.Reads);
    }

    [Fact]
    public async Task Two_disagreeing_declarations_refuse_rather_than_pick_a_winner()
    {
        // Two Apps means two bots and two permission sets, and the operator believes one of them is
        // in force. Guessing produces commits attributed to a bot nobody configured.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(ReadyVault()).ResolveAsync(Pointer, "/id.json", readVariable: name => name switch
            {
                GitHubAppConfigResolver.AppIdVariable => "999",
                GitHubAppConfigResolver.SlugVariable => "from-env",
                GitHubAppConfigResolver.PrivateKeyVariable => "-----BEGIN RSA PRIVATE KEY-----\\nenv\\n",
                _ => null,
            }));

        Assert.Contains("999", ex.Message);
        Assert.Contains("1234567", ex.Message);
    }

    [Fact]
    public async Task An_unenrolled_host_gets_the_ceremony_not_a_stack_trace()
    {
        var vault = new FakeVaultCli { Identity = null };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(vault).ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment));

        Assert.Contains("vault agent add", ex.Message);
        Assert.Contains("--identity /id.json", ex.Message);
        Assert.Empty(vault.Reads);
    }

    [Fact]
    public async Task A_host_with_no_service_account_is_told_to_store_one_not_to_re_enrol()
    {
        // Re-enrolling mints a NEW agent id and orphans every grant already made. The message has
        // to steer away from the obvious wrong fix.
        var vault = new FakeVaultCli
        {
            Identity = new VaultAgentIdentity("agt_abc", "pksorensen", Server, "none", "/id.json", HasServiceAccount: false),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(vault).ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment));

        Assert.Contains("vault agent credentials", ex.Message);
        Assert.Contains("Do not re-enrol", ex.Message);
    }

    [Fact]
    public async Task An_identity_pinned_to_another_vault_is_refused()
    {
        var vault = new FakeVaultCli { Identity = Enrolled("https://vault.example.com") };
        vault.Grants.Add(UsableGrant());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(vault).ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment));

        Assert.Contains("vault.example.com", ex.Message);
        Assert.Empty(vault.Reads);
    }

    [Fact]
    public async Task A_missing_grant_prints_the_line_the_owner_has_to_run()
    {
        var vault = new FakeVaultCli { Identity = Enrolled() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(vault).ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment));

        Assert.Contains("vault agent grant vlt_1 itm_2 --agent agt_abc", ex.Message);
        Assert.Empty(vault.Reads);
    }

    [Fact]
    public async Task An_unusable_grant_reports_the_vault_s_own_reason()
    {
        var vault = new FakeVaultCli { Identity = Enrolled() };
        vault.Grants.Add(new VaultGrant
        {
            VaultId = "vlt_1", ItemId = "itm_2", Usable = false, Reason = "expired",
            ExpiresAt = "2026-01-01T00:00:00Z",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubAppProvisioner(vault).ResolveAsync(Pointer, "/id.json", readVariable: NoEnvironment));

        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task Says_out_loud_whether_the_grant_is_standing_or_pages_a_human()
    {
        var standing = new List<string>();
        await new GitHubAppProvisioner(ReadyVault())
            .ResolveAsync(Pointer, "/id.json", standing.Add, NoEnvironment);
        Assert.Contains(standing, m => m.Contains("standing"));

        var interactive = new FakeVaultCli { Identity = Enrolled() };
        interactive.Grants.Add(UsableGrant("always"));
        var notices = new List<string>();
        await new GitHubAppProvisioner(interactive)
            .ResolveAsync(Pointer, "/id.json", notices.Add, NoEnvironment);
        // Every restart waits for someone to tap approve. Better said at startup than found at 2am.
        Assert.Contains(notices, m => m.Contains("needs approval"));
    }

    /// <summary>
    /// The one invariant the wrapper used to enforce with an output filter and nothing enforces now
    /// except this: the PEM does not appear in anything a person or a log can read.
    /// </summary>
    [Fact]
    public async Task The_private_key_never_appears_in_a_notice_or_an_error()
    {
        const string pem = "-----BEGIN RSA PRIVATE KEY-----\nSUPERSECRETMATERIAL\n-----END RSA PRIVATE KEY-----";
        var vault = ReadyVault();
        vault.FieldValue = pem;
        var notices = new List<string>();

        var config = await new GitHubAppProvisioner(vault)
            .ResolveAsync(Pointer, "/id.json", notices.Add, NoEnvironment);

        Assert.Equal(pem, config!.PrivateKeyPem);
        Assert.DoesNotContain(notices, n => n.Contains("SUPERSECRET"));
        // GitHubAppConfig's own ToString is the accident waiting to happen: a record prints every
        // property, so a single interpolated log line would put the key in a job log forever.
        Assert.DoesNotContain("SUPERSECRET", config.ToString());
    }

    [Fact]
    public void FromRepoInfo_reads_a_complete_pointer()
    {
        using var doc = JsonDocument.Parse("""
        {
          "requiresGitHub": true,
          "gitUrl": "https://github.com/pksorensen/commuteconnects",
          "gitHubApp": {
            "appId": "1234567", "slug": "si14-x",
            "vault": {
              "server": "https://vault.agentics.dk", "owner": "pksorensen",
              "vaultId": "vlt_1", "itemId": "itm_2"
            }
          }
        }
        """);

        var pointer = GitHubAppPointer.FromRepoInfo(doc.RootElement);

        Assert.Equal("si14-x", pointer!.Slug);
        Assert.Equal("private_key", pointer.FieldOrDefault);
    }

    [Theory]
    [InlineData("""{"requiresGitHub":true}""")]
    [InlineData("""{"gitHubApp":{"appId":"1","slug":"s"}}""")]
    [InlineData("""{"gitHubApp":{"appId":"1","vault":{"server":"https://v","owner":"o","vaultId":"v1","itemId":"i1"}}}""")]
    public void FromRepoInfo_treats_an_absent_or_partial_pointer_as_no_app(string json)
    {
        // A half-pointer is not an error here. The runner's preflight is a better place to explain
        // a misconfigured App than a JSON parser is -- and an older platform simply omits the field.
        using var doc = JsonDocument.Parse(json);

        Assert.Null(GitHubAppPointer.FromRepoInfo(doc.RootElement));
    }

    [Fact]
    public void Runner_identity_is_per_project_not_per_host()
    {
        // Two projects on one box get two identities, so a grant made for one is not silently
        // usable by the other. ADR 0011's instinct, carried into the runner plane (ADR 0012).
        var a = RunnerVaultIdentity.ResolvePath("pksorensen", "commuteconnects", "/home/node");
        var b = RunnerVaultIdentity.ResolvePath("pksorensen", "agentics", "/home/node");

        Assert.NotEqual(a, b);
        Assert.StartsWith(Path.Combine("/home/node", ".pks-cli", "vault"), a);
        Assert.EndsWith("identity.json", a);
    }
}
