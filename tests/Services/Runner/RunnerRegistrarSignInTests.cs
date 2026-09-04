using System.Net;
using System.Text;
using FluentAssertions;
using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// `pks agentics runner run --project owner/project` on a machine that has never signed
/// in used to stop at "run `pks agentics init`" — a second command to type before the
/// first one could work, on a box where the answer was always going to be the same
/// device grant. It now runs that grant itself.
///
/// What these pin is *when*: only after the server refuses, only when nobody was signed
/// in, and only when a human is there to read the code. The alternatives are worse than
/// the message it replaces — a daemon waiting ten minutes on a browser that will never
/// open, or a second registration for a project the operator has no rights to anyway.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class RunnerRegistrarSignInTests
{
    [Fact]
    public async Task Nobody_signed_in_and_a_terminal_to_ask_on_signs_in_and_retries()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var config = new InMemoryConfig();
        var signIn = new RecordingSignIn("fresh-user-token");

        var registration = await RunnerRegistrar.ResolveOrRegisterAsync(
            config, "pksorensen/kjeldager-drift", "agentics.dk",
            auth: new NoCredential(), canPrompt: true, handler: handler, signIn: signIn.Invoke);

        signIn.Calls.Should().Be(1);
        handler.Bearers.Should().Equal(null, "fresh-user-token");
        registration.Token.Should().Be("runner-token");
        config.Saved.Should().ContainSingle();
    }

    // A credential that the server refuses is a permission problem, and signing the same
    // user in again would only produce the same refusal — after another ten minutes.
    [Fact]
    public async Task A_user_already_signed_in_is_not_asked_to_sign_in_again()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Forbidden);
        var signIn = new RecordingSignIn("never-used");

        var register = async () => await RunnerRegistrar.ResolveOrRegisterAsync(
            new InMemoryConfig(), "pksorensen/kjeldager-drift", "agentics.dk",
            auth: new SignedIn("stale-user-token"), canPrompt: true, handler: handler, signIn: signIn.Invoke);

        await register.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("403"));
        signIn.Calls.Should().Be(0);
    }

    // The detached runner's tmux pane has a TTY nobody watches; `--no-prompt` closes this
    // gate, and what is left is the message that says which command to run by hand.
    [Fact]
    public async Task With_nobody_watching_it_says_what_to_run_instead_of_waiting()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized);
        var signIn = new RecordingSignIn("never-used");

        var register = async () => await RunnerRegistrar.ResolveOrRegisterAsync(
            new InMemoryConfig(), "pksorensen/kjeldager-drift", "agentics.dk",
            auth: new NoCredential(), canPrompt: false, handler: handler, signIn: signIn.Invoke);

        await register.Should().ThrowAsync<InvalidOperationException>()
            .Where(e => e.Message.Contains("pks agentics init"));
        signIn.Calls.Should().Be(0);
    }

    // A local dev server fronts no public realm: probing login.localhost would spend the
    // operator's attention on a host that cannot answer.
    [Fact]
    public async Task A_loopback_server_is_never_signed_in_to()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Unauthorized);
        var signIn = new RecordingSignIn("never-used");

        var register = async () => await RunnerRegistrar.ResolveOrRegisterAsync(
            new InMemoryConfig(), "pksorensen/kjeldager-drift", "localhost:3000",
            auth: new NoCredential(), canPrompt: true, handler: handler, signIn: signIn.Invoke);

        await register.Should().ThrowAsync<InvalidOperationException>();
        signIn.Calls.Should().Be(0);
    }

    // Signing in is a fallback, not a step: a server that accepts the credential already
    // held must never see a device code.
    [Fact]
    public async Task A_registration_that_succeeds_first_time_asks_nothing()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK);
        var signIn = new RecordingSignIn("never-used");

        await RunnerRegistrar.ResolveOrRegisterAsync(
            new InMemoryConfig(), "pksorensen/kjeldager-drift", "agentics.dk",
            auth: new SignedIn("good-user-token"), canPrompt: true, handler: handler, signIn: signIn.Invoke);

        signIn.Calls.Should().Be(0);
        handler.Bearers.Should().Equal("good-user-token");
    }

    private sealed class RecordingSignIn(string? token)
    {
        public int Calls { get; private set; }

        public Task<string?> Invoke(string serverUrl, Action<string>? onInfo, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(token);
        }
    }

    /// <param name="statuses">One per POST, in order.</param>
    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _calls;

        /// <summary>The bearer presented on each POST — null when the request went out bare.</summary>
        public List<string?> Bearers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bearers.Add(request.Headers.Authorization?.Parameter);
            var status = statuses[Math.Min(_calls++, statuses.Length - 1)];

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK
                        ? """{"id":"r1","name":"box","token":"runner-token"}"""
                        : """{"error":"unauthorized"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class NoCredential : IAgenticsAuthService
    {
        public Task<string?> GetTokenAsync(string audience, string? explicitToken, string owner, string project)
            => Task.FromResult<string?>(null);

        public Task<string?> GetUserTokenAsync(string audience, string? explicitToken)
            => Task.FromResult<string?>(null);

        public Task<string?> ForceRefreshAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class SignedIn(string token) : IAgenticsAuthService
    {
        public Task<string?> GetTokenAsync(string audience, string? explicitToken, string owner, string project)
            => Task.FromResult<string?>(token);

        public Task<string?> GetUserTokenAsync(string audience, string? explicitToken)
            => Task.FromResult<string?>(token);

        public Task<string?> ForceRefreshAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class InMemoryConfig : IAgenticsRunnerConfigurationService
    {
        public List<AgenticsRunnerRegistration> Saved { get; } = [];

        public Task<AgenticsRunnerConfiguration> LoadAsync()
            => Task.FromResult(new AgenticsRunnerConfiguration { Registrations = Saved });

        public Task SaveAsync(AgenticsRunnerConfiguration configuration) => Task.CompletedTask;

        public Task<AgenticsRunnerRegistration> AddRegistrationAsync(AgenticsRunnerRegistration registration)
        {
            Saved.Add(registration);

            return Task.FromResult(registration);
        }

        public Task<List<AgenticsRunnerRegistration>> ListRegistrationsAsync()
            => Task.FromResult(new List<AgenticsRunnerRegistration>());

        public Task<AgenticsRunnerRegistration?> GetRegistrationAsync(string registrationId)
            => Task.FromResult<AgenticsRunnerRegistration?>(null);
    }
}
