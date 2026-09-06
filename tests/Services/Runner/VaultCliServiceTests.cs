using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Records what it was asked to run and replays a scripted answer. Everything here is about the
/// shape of the invocation and the parsing of the reply — no vault binary is involved.
/// </summary>
internal sealed class FakeVaultProcessRunner : IVaultProcessRunner
{
    private readonly Queue<ProcessResult> _results = new();

    public List<IReadOnlyList<string>> Invocations { get; } = new();
    public List<string> StderrRelayed { get; } = new();
    public string[] StderrLinesToEmit { get; set; } = [];

    public FakeVaultProcessRunner Returns(int exitCode, string stdout = "", string stderr = "")
    {
        _results.Enqueue(new ProcessResult(exitCode, stdout, stderr));

        return this;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onStderrLine,
        CancellationToken ct)
    {
        Invocations.Add(arguments);
        foreach (var line in StderrLinesToEmit)
        {
            StderrRelayed.Add(line);
            onStderrLine?.Invoke(line);
        }

        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new ProcessResult(0, "", ""));
    }
}

public class VaultCliServiceTests
{
    private const string WhoAmIOutput = """
agent        agt_abc123
owner        pksorensen
server       https://vault.agentics.dk
identity     /home/node/.pks-cli/vault/pksorensen-cc/identity.json
protection   none
host         fp_9911
service acct svc-runner (file)
""";

    /// <summary>
    /// A real file, because <see cref="VaultCliService.FindBinary"/> checks that the override
    /// actually exists — a stale PKS_VAULT_CLI should read as "not installed", not as a binary
    /// that fails to exec later.
    /// </summary>
    private static readonly string FakeBinary = CreateFakeBinary();

    private static string CreateFakeBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pks-fake-vault-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");

        return path;
    }

    private static VaultCliService Service(FakeVaultProcessRunner processes, string? binary = null) =>
        new(processes, name => name == VaultCliService.BinaryPathVariable ? binary ?? FakeBinary : null);

    [Fact]
    public void ParseWhoAmI_reads_every_field_including_the_two_word_key()
    {
        var identity = VaultCliService.ParseWhoAmI(WhoAmIOutput, "/fallback");

        Assert.Equal("agt_abc123", identity.AgentId);
        Assert.Equal("pksorensen", identity.Owner);
        Assert.Equal("https://vault.agentics.dk", identity.Server);
        Assert.Equal("/home/node/.pks-cli/vault/pksorensen-cc/identity.json", identity.IdentityPath);
        Assert.True(identity.IsUnprotected);
        // "service acct" is two words. A generic "first token is the key" parser reads the value
        // as "acct" and reports every enrolled host as having no service account.
        Assert.True(identity.HasServiceAccount);
    }

    [Fact]
    public void ParseWhoAmI_treats_the_no_service_account_marker_as_absent()
    {
        // The host that cannot call the server at all used to be indistinguishable from a working
        // one. "(none)" is how the CLI says so, and it must not read as an account named "(none)".
        var identity = VaultCliService.ParseWhoAmI(
            WhoAmIOutput.Replace("service acct svc-runner (file)", "service acct (none)"), "/fallback");

        Assert.False(identity.HasServiceAccount);
    }

    [Fact]
    public void ParseWhoAmI_ignores_the_prose_the_cli_appends()
    {
        // whoami follows its key/value block with paragraphs of advice. Indented and blank lines
        // are skipped; a sentence starting at column 0 must not become a field.
        var identity = VaultCliService.ParseWhoAmI(
            WhoAmIOutput + "\n\nThis identity file is unprotected: anyone who can read it\ncan be this agent.\n",
            "/fallback");

        Assert.Equal("agt_abc123", identity.AgentId);
        Assert.Equal("none", identity.Protection);
    }

    [Fact]
    public async Task ReadFieldAsync_passes_the_ids_as_separate_arguments()
    {
        var processes = new FakeVaultProcessRunner().Returns(0, "-----BEGIN RSA PRIVATE KEY-----\nabc\n");
        var value = await Service(processes).ReadFieldAsync(
            "/id.json", "vlt_1", "itm_2", "private_key", TimeSpan.FromMinutes(2));

        var args = Assert.Single(processes.Invocations);
        Assert.Equal(["agent", "read", "--vault", "vlt_1", "--item", "itm_2", "--field", "private_key",
            "--wait", "120s", "--identity", "/id.json"], args);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", value);
    }

    [Fact]
    public async Task ReadFieldAsync_does_not_trim_the_value()
    {
        // The CLI deliberately emits no trailing newline so the value survives capture. Trimming
        // here would silently alter a secret whose own edges are significant.
        var processes = new FakeVaultProcessRunner().Returns(0, "  padded  ");
        var value = await Service(processes).ReadFieldAsync(
            "/id.json", "v", "i", "f", TimeSpan.FromSeconds(30));

        Assert.Equal("  padded  ", value);
    }

    [Fact]
    public async Task ReadFieldAsync_relays_approval_notices_while_it_waits()
    {
        var processes = new FakeVaultProcessRunner { StderrLinesToEmit = ["vault: waiting for approval"] };
        processes.Returns(0, "key");
        var notices = new List<string>();

        await Service(processes).ReadFieldAsync(
            "/id.json", "v", "i", "f", TimeSpan.FromSeconds(30), notices.Add);

        // The CLI's own prefix is stripped so the runner console reads as one voice.
        Assert.Equal(["waiting for approval"], notices);
    }

    [Fact]
    public async Task ReadFieldAsync_failure_message_carries_stderr_and_never_stdout()
    {
        // stdout on a partial success can hold part of a key. The exception message is built from
        // stderr alone, and this test is what keeps it that way.
        var processes = new FakeVaultProcessRunner()
            .Returns(1, "-----BEGIN RSA PRIVATE KEY-----", "no grant for this item");

        var ex = await Assert.ThrowsAsync<VaultCliException>(() => Service(processes).ReadFieldAsync(
            "/id.json", "v", "itm_2", "private_key", TimeSpan.FromSeconds(30)));

        Assert.Contains("no grant for this item", ex.Message);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message);
    }

    [Fact]
    public async Task ReadFieldAsync_rejects_an_empty_value()
    {
        var processes = new FakeVaultProcessRunner().Returns(0, "");

        await Assert.ThrowsAsync<VaultCliException>(() => Service(processes).ReadFieldAsync(
            "/id.json", "v", "i", "f", TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task GrantsAsync_parses_the_json_the_cli_emits()
    {
        var json = """
        [
          {
            "policyId": "pol_1", "vaultId": "vlt_1", "itemId": "itm_2",
            "purpose": "GitHub App", "expiresAt": "2027-01-01T00:00:00Z",
            "usesLeft": 4, "maxUses": 5, "maxUsesPerHour": 2,
            "consentMode": "never", "usable": true
          }
        ]
        """;
        var grants = await Service(new FakeVaultProcessRunner().Returns(0, json)).GrantsAsync("/id.json");

        var grant = Assert.Single(grants);
        Assert.Equal("itm_2", grant.ItemId);
        Assert.True(grant.Usable);
        Assert.False(grant.NeedsApproval);
    }

    [Fact]
    public async Task GrantsAsync_treats_no_grants_as_an_answer_not_an_error()
    {
        var grants = await Service(new FakeVaultProcessRunner().Returns(0, "[]")).GrantsAsync("/id.json");

        Assert.Empty(grants);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("session")]
    public async Task GrantsAsync_flags_a_grant_that_pages_a_human(string consentMode)
    {
        var json = $$"""[{"vaultId":"v","itemId":"i","consentMode":"{{consentMode}}","usable":true}]""";
        var grants = await Service(new FakeVaultProcessRunner().Returns(0, json)).GrantsAsync("/id.json");

        Assert.True(Assert.Single(grants).NeedsApproval);
    }

    [Fact]
    public async Task A_missing_binary_says_how_to_install_it()
    {
        var service = new VaultCliService(new FakeVaultProcessRunner(), _ => null);

        var ex = await Assert.ThrowsAsync<VaultCliException>(() => service.GrantsAsync("/id.json"));

        Assert.Contains(VaultCliService.InstallHint, ex.Message);
    }

    [Fact]
    public async Task WhoAmIAsync_returns_null_when_this_host_was_never_enrolled()
    {
        // Not an exception: an un-enrolled host is the ordinary starting state, and the preflight
        // answers it with an enrolment ceremony rather than an error.
        var identity = await Service(new FakeVaultProcessRunner())
            .WhoAmIAsync(Path.Combine(Path.GetTempPath(), $"pks-no-such-identity-{Guid.NewGuid():N}.json"));

        Assert.Null(identity);
    }
}
