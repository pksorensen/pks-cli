using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.T3;
using Xunit;

namespace PKS.CLI.Tests.Commands.T3;

/// <summary>
/// The bootstrap script is a string that runs as root on someone's VM, so the things worth pinning
/// are the ones that are invisible when wrong: a t3 that binds the public interface still works
/// perfectly, and a secret interpolated into phase 1 still provisions the box.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class T3BootstrapScriptTests
{
    private const string Domain = "t3.example.com";
    private const string Tenant = "11111111-2222-3333-4444-555555555555";

    private static T3BootstrapOptions Options() => new()
    {
        Domain = Domain,
        TenantId = Tenant,
        RemoteUser = "azureuser",
        RemoteHome = "/home/azureuser",
        FoundryDeployment = "gpt-5-codex",
    };

    [Fact]
    public void RedirectUri_matches_the_path_oauth2_proxy_reserves()
    {
        // If these two ever disagree, Entra returns AADSTS50011 and the box looks broken rather than
        // misconfigured — the whole point of registering the URI from the same constant.
        T3BootstrapScript.RedirectUriFor(Domain)
            .Should().Be($"https://{Domain}{T3BootstrapScript.CallbackPath}");
    }

    [Fact]
    public void T3_binds_loopback_only()
    {
        // oauth2-proxy is the only authentication in front of t3. A t3 listening on 0.0.0.0 is a t3
        // with no Entra gate, and it would pass every functional test.
        var script = T3BootstrapScript.Build(Options());

        script.Should().Contain("t3 serve --host 127.0.0.1");
        script.Should().NotContain("--host 0.0.0.0");
    }

    [Fact]
    public void Phase_one_carries_no_credential_field()
    {
        // Phase 1 becomes an ssh argv and therefore a line in the remote shell's history. The client
        // secret and cookie secret must only ever exist in the stdin phase.
        var script = T3BootstrapScript.Build(Options());

        script.Should().NotContain("OAUTH2_PROXY_CLIENT_SECRET");
        script.Should().NotContain("OAUTH2_PROXY_COOKIE_SECRET");
        script.Should().NotContain("PKS_FOUNDRY_PROXY_TOKEN=");
    }

    [Fact]
    public void Interpolated_values_reach_the_generated_config()
    {
        var script = T3BootstrapScript.Build(Options());

        script.Should().Contain($"https://login.microsoftonline.com/{Tenant}/v2.0");
        script.Should().Contain($"redirect_url = \"{T3BootstrapScript.RedirectUriFor(Domain)}\"");
        script.Should().Contain($"upstreams = [\"http://127.0.0.1:{T3BootstrapScript.T3Port}/\"]");
        script.Should().Contain($"model = \"gpt-5-codex\"");
        script.Should().Contain($"base_url = \"http://127.0.0.1:{T3BootstrapScript.FoundryProxyPort}/openai/v1\"");
    }

    [Fact]
    public void Acme_email_falls_back_to_the_domain_rather_than_empty()
    {
        // An empty email in Caddy's global block makes it prompt, and a prompt over non-interactive
        // ssh is a hang, not an error.
        T3BootstrapScript.Build(Options()).Should().Contain($"email admin@{Domain}");
        T3BootstrapScript.Build(Options() with { AcmeEmail = "ops@example.com" })
            .Should().Contain("email ops@example.com");
    }

    [Fact]
    public void Secret_files_land_root_owned_and_unreadable_by_the_agent_user()
    {
        // The Foundry passthrough runs as the VM user, which is what spawns coding agents. If the
        // Entra client secret were in a file that user could read, every agent T3 runs could read it.
        T3BootstrapScript.SecretDeliveryScript()
            .Should().Contain("chmod 0600 /etc/pks-t3/oauth2-proxy.env")
            .And.Contain("chown root:root /etc/pks-t3/oauth2-proxy.env");

        T3BootstrapScript.FoundryTokenDeliveryScript("azureuser")
            .Should().Contain("chown root:azureuser /etc/pks-t3/foundry.env");
    }
}
