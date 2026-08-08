using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Agentics;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Discovery;
using PKS.Infrastructure.Services.Oidc;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// `ForceRefresh` and what it protects: a push long enough to outlive its own
/// access token. The rest of the resolver — discovery, the host rule, the
/// interactive grants — is exercised by `pks brain push` against a real
/// receiver; these tests cover the arithmetic that decides whether a 401 is
/// recoverable.
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class BrainTokenResolverTests : IDisposable
{
    private const string Issuer = "https://kc.example.com/realms/agentics";
    private const string Origin = "https://brain.example.com";
    private const string ApiBase = "https://brain.example.com/api/brain/v1";

    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(BrainTokenResolver.EnvVar);

    public BrainTokenResolverTests()
        // Step 2 short-circuits everything below it, so a developer machine with
        // a token exported would pass these tests without running any of them.
        => Environment.SetEnvironmentVariable(BrainTokenResolver.EnvVar, null);

    public void Dispose() => Environment.SetEnvironmentVariable(BrainTokenResolver.EnvVar, _savedEnv);

    [Fact]
    public async Task Saved_login_is_reused_while_it_is_still_valid()
    {
        var (resolver, _, deviceLogin, store) = Build(StoredCredentials("still-good"));

        var token = await resolver.ResolveAsync(Request());

        token!.Value.Should().Be("still-good");
        deviceLogin.Refreshes.Should().Be(0, "an unexpired token needs no round trip");
        store.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task ForceRefresh_renews_a_saved_login_the_server_rejected()
    {
        // The bug this exists for: a push dies at 12,000 of 13,249 blobs, the
        // 401 handler re-asks the resolver, and the resolver's own clock says
        // the token is fine — so it hands back the token the server just
        // refused and the retry fails identically.
        var (resolver, _, deviceLogin, store) = Build(StoredCredentials("rejected-by-server"));
        deviceLogin.Next = new OidcLoginResult("renewed", "rt-2", null, Future, null);

        var token = await resolver.ResolveAsync(Request() with { ForceRefresh = true });

        token!.Value.Should().Be("renewed");
        token.Origin.Should().Contain("refreshed");
        deviceLogin.Refreshes.Should().Be(1);
        store.Saved.Should().ContainSingle().Which.AccessToken.Should().Be("renewed");
    }

    [Fact]
    public async Task A_refresh_that_returns_no_new_refresh_token_keeps_the_old_one()
    {
        // Rotation is a provider choice. Overwriting with the null a
        // non-rotating provider sends erases the only thing that can renew
        // anything, so the *second* refresh of a long push would need a device
        // login the push cannot perform.
        var (resolver, _, deviceLogin, store) = Build(StoredCredentials("rejected-by-server"));
        deviceLogin.Next = new OidcLoginResult("renewed", null, null, Future, null);

        await resolver.ResolveAsync(Request() with { ForceRefresh = true });

        store.Saved.Should().ContainSingle().Which.RefreshToken.Should().Be("rt-1");
    }

    [Fact]
    public async Task ForceRefresh_renews_the_single_login_credential_too()
    {
        var (resolver, auth, _, _) = Build(stored: null, single: new AgenticsAuthCredentials
        {
            Server = "brain.example.com",
            AccessToken = "rejected-by-server",
            RefreshToken = "rt-1",
            ExpiresAt = Future,
        });
        auth.Forced = "renewed";

        var token = await resolver.ResolveAsync(Request() with { ForceRefresh = true });

        token!.Value.Should().Be("renewed");
        auth.ForceRefreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task An_explicit_token_is_returned_unchanged_even_under_ForceRefresh()
    {
        // There is nothing behind a `bkt_` token to refresh, so the honest
        // answer to its 401 is the same token and a failed push.
        var (resolver, auth, deviceLogin, _) = Build(StoredCredentials("unused"));

        var token = await resolver.ResolveAsync(
            Request() with { ExplicitToken = "bkt_abc", ForceRefresh = true });

        token!.Value.Should().Be("bkt_abc");
        auth.ForceRefreshCalls.Should().Be(0);
        deviceLogin.Refreshes.Should().Be(0);
    }

    private static long Future => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;

    private static BrainTokenRequest Request() => new(
        ExplicitToken: null,
        Endpoint: new BrainEndpoint(Origin, ApiBase, "", "", null, null, Discovered: true));

    private static IssuerCredentials StoredCredentials(string accessToken) => new()
    {
        Issuer = Issuer,
        Resource = ApiBase,
        ClientId = "client",
        AccessToken = accessToken,
        RefreshToken = "rt-1",
        ExpiresAt = Future,
    };

    private static (BrainTokenResolver, FakeAuth, FakeDeviceLogin, FakeStore) Build(
        IssuerCredentials? stored,
        AgenticsAuthCredentials? single = null)
    {
        var auth = new FakeAuth();
        var deviceLogin = new FakeDeviceLogin();
        var store = new FakeStore(stored);

        var resolver = new BrainTokenResolver(
            new FakeAuthConfig(single), auth, new FakeDiscovery(), store, deviceLogin, new FakeLoopback());

        return (resolver, auth, deviceLogin, store);
    }

    private sealed class FakeAuthConfig(AgenticsAuthCredentials? creds) : IAgenticsAuthConfigurationService
    {
        public Task<AgenticsAuthCredentials?> LoadAsync() => Task.FromResult(creds);
        public Task SaveAsync(AgenticsAuthCredentials credentials) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    private sealed class FakeAuth : IAgenticsAuthService
    {
        public string? Forced { get; set; }
        public int ForceRefreshCalls { get; private set; }

        public Task<string?> GetTokenAsync(string audience, string? explicitToken, string owner, string project)
            => Task.FromResult<string?>(null);

        public Task<string?> ForceRefreshAsync(CancellationToken ct = default)
        {
            ForceRefreshCalls++;

            return Task.FromResult(Forced);
        }
    }

    private sealed class FakeDiscovery : IOidcDiscovery
    {
        public Task<ProtectedResourceMetadata?> ProtectedResourceAsync(string resourceUrl, CancellationToken ct = default)
            => Task.FromResult<ProtectedResourceMetadata?>(new ProtectedResourceMetadata(ApiBase, [Issuer], []));

        public Task<OidcEndpoints?> EndpointsAsync(string issuer, CancellationToken ct = default)
            => Task.FromResult<OidcEndpoints?>(new OidcEndpoints(
                Issuer, $"{Issuer}/protocol/openid-connect/token", null, null));
    }

    private sealed class FakeStore(IssuerCredentials? stored) : IIssuerCredentialStore
    {
        public List<IssuerCredentials> Saved { get; } = [];

        public Task<IssuerCredentials?> LoadAsync(string issuer, CancellationToken ct = default)
            => Task.FromResult(stored);

        public Task SaveAsync(IssuerCredentials credentials, CancellationToken ct = default)
        {
            Saved.Add(credentials);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IssuerCredentials>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IssuerCredentials>>(Saved);

        public Task<bool> DeleteAsync(string issuer, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeDeviceLogin : IDeviceCodeLogin
    {
        public OidcLoginResult Next { get; set; } = OidcLoginResult.Failed("not configured");
        public int Refreshes { get; private set; }

        public Task<OidcLoginResult> LoginAsync(DeviceLoginRequest request, CancellationToken ct = default)
            => Task.FromResult(OidcLoginResult.Failed("interactive login not expected here"));

        public Task<OidcLoginResult> RefreshAsync(
            OidcEndpoints endpoints, string clientId, string refreshToken, string? resource, CancellationToken ct = default)
        {
            Refreshes++;

            return Task.FromResult(Next);
        }
    }

    private sealed class FakeLoopback : ILoopbackAuthCodeLogin
    {
        public Task<OidcLoginResult> LoginAsync(LoopbackLoginRequest request, CancellationToken ct = default)
            => Task.FromResult(OidcLoginResult.Failed("interactive login not expected here"));
    }
}
