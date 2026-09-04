using FluentAssertions;
using PKS.Commands.Agentics;
using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Oidc;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agentics;

/// <summary>
/// `pks agentics init` used to assume the realm sits at <c>keycloak.&lt;server&gt;</c>.
/// That host has never resolved for agentics.dk, and because nothing answers on it
/// the failure arrived as "The SSL connection could not be established" — a message
/// that reads like a broken server rather than a wrong hostname, and cost an evening.
///
/// So the host is discovered, not assumed: every Keycloak serves
/// <c>/.well-known/openid-configuration</c>, which makes "who answers" the same
/// question as "where is it". These tests pin the order and the stopping.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AgenticsInitIssuerDiscoveryTests
{
    [Fact]
    public async Task Login_lives_at_login_not_keycloak()
    {
        var discovery = new RecordingDiscovery();
        await RunAsync(discovery, new AgenticsInitCommand.Settings());

        discovery.Asked.Should().StartWith("https://login.agentics.dk/realms/agentics");
    }

    // A host that answers is the identity provider; there is nothing left to look for.
    [Fact]
    public async Task It_stops_at_the_first_host_that_answers()
    {
        var discovery = new RecordingDiscovery("https://login.agentics.dk/realms/agentics");
        await RunAsync(discovery, new AgenticsInitCommand.Settings());

        discovery.Asked.Should().ContainSingle();
    }

    // `keycloak.` stays in the list: some installs do use it, and dropping it would
    // break them to fix ours.
    [Fact]
    public async Task It_falls_through_to_keycloak_and_then_the_server_itself()
    {
        var discovery = new RecordingDiscovery();
        await RunAsync(discovery, new AgenticsInitCommand.Settings { Server = "example.com" });

        discovery.Asked.Should().Equal(
            "https://login.example.com/realms/agentics",
            "https://keycloak.example.com/realms/agentics",
            "https://example.com/realms/agentics");
    }

    // An operator who names the host has answered the question. Probing past it
    // would send their credentials at two hosts they did not name.
    [Fact]
    public async Task An_explicit_keycloak_url_is_the_only_candidate()
    {
        var discovery = new RecordingDiscovery();
        await RunAsync(discovery, new AgenticsInitCommand.Settings
        {
            Keycloak = "https://id.example.com",
        });

        discovery.Asked.Should().Equal("https://id.example.com/realms/agentics");
    }

    private static Task<int> RunAsync(RecordingDiscovery discovery, AgenticsInitCommand.Settings settings)
    {
        var command = new AgenticsInitCommand(
            new NoopAuthConfig(),
            new TestConsole(),
            discovery,
            new FailingDeviceLogin());

        return command.ExecuteAsync(null!, settings);
    }

    /// <param name="answersFor">
    /// The one issuer that serves discovery, or none at all — the case where the
    /// command has to guess Keycloak's paths.
    /// </param>
    private sealed class RecordingDiscovery(string? answersFor = null) : IOidcDiscovery
    {
        public List<string> Asked { get; } = [];

        public Task<OidcEndpoints?> EndpointsAsync(string issuer, CancellationToken ct = default)
        {
            Asked.Add(issuer);

            return Task.FromResult(issuer == answersFor
                ? new OidcEndpoints(issuer, $"{issuer}/token", $"{issuer}/device", $"{issuer}/auth")
                : null);
        }

        public Task<ProtectedResourceMetadata?> ProtectedResourceAsync(string resourceUrl, CancellationToken ct = default)
            => Task.FromResult<ProtectedResourceMetadata?>(null);
    }

    /// The login itself is not under test, and failing it keeps the command from
    /// writing credentials to the real ~/.pks-cli during a unit test.
    private sealed class FailingDeviceLogin : IDeviceCodeLogin
    {
        public Task<OidcLoginResult> LoginAsync(DeviceLoginRequest request, CancellationToken ct = default)
            => Task.FromResult(OidcLoginResult.Failed("not part of this test"));

        public Task<OidcLoginResult> RefreshAsync(
            OidcEndpoints endpoints, string clientId, string refreshToken, string? resource, CancellationToken ct = default)
            => Task.FromResult(OidcLoginResult.Failed("not part of this test"));
    }

    private sealed class NoopAuthConfig : IAgenticsAuthConfigurationService
    {
        public Task<AgenticsAuthCredentials?> LoadAsync() => Task.FromResult<AgenticsAuthCredentials?>(null);
        public Task SaveAsync(AgenticsAuthCredentials credentials) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
