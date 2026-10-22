using System.Diagnostics;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Entra;
using PKS.Infrastructure.Services.Exec;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Services.Exec;

/// <summary>
/// <c>{proxy:endpoint}</c> and <c>{proxy:token}</c>: the resolver starts a loopback credential proxy in
/// front of the Foundry resource, the child gets its URL and a throwaway token, and the Entra token
/// that actually reaches Foundry never enters the child's environment at all.
/// </summary>
public class ManifestResolverProxyTests : IAsyncLifetime
{
    private const string EntraToken = "entra-access-token-xyz";

    private WebApplication _foundry = null!;
    private string _foundryEndpoint = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _foundry = builder.Build();
        _foundry.MapGet("/speechtotext/whoami", (HttpContext ctx) =>
            Results.Text(ctx.Request.Headers.Authorization.ToString()));
        await _foundry.StartAsync();
        _foundryEndpoint = _foundry.Urls.Single() + "/";
    }

    public async Task DisposeAsync() => await _foundry.DisposeAsync();

    private Mock<IAzureFoundryAuthService> FoundrySignedIn()
    {
        var mock = new Mock<IAzureFoundryAuthService>();
        mock.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        mock.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(new FoundryStoredCredentials
        {
            TenantId = "test-tenant",
            RefreshToken = SecretValue.From("refresh"),
            SelectedResourceEndpoint = _foundryEndpoint,
            SelectedResourceName = "test-foundry",
            DefaultModel = "mai-transcribe-1",
        });
        mock.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EntraToken);
        return mock;
    }

    private static ManifestResolver Resolver(Mock<IAzureFoundryAuthService> auth, TestConsole console)
    {
        var entra = new Mock<IEntraApplicationService>();
        entra.Setup(x => x.GetStoredAsync(It.IsAny<string>())).ReturnsAsync((EntraStoredApp?)null);
        return new ManifestResolver(auth.Object, new AzureFoundryAuthConfig(), entra.Object, console);
    }

    private static PksManifest OneCapability(string kind, Dictionary<string, string> env) => new()
    {
        ManifestVersion = "v1",
        Name = "test",
        Capabilities =
        {
            new PksCapabilityManifest
            {
                Id = "speech",
                Providers = { new PksProviderManifest { Kind = kind, Env = env } },
            },
        },
    };

    private static ManifestResolveOptions Unattended => new() { NonInteractive = true, AcceptOptional = true };

    private static Dictionary<string, string> ProxyEnv => new()
    {
        ["FOUNDRY_ENDPOINT"] = "{proxy:endpoint}",
        ["FOUNDRY_TOKEN"] = "{proxy:token}",
    };

    private static IDictionary<string, string?> ChildEnvironment(ResolvedEnvironment resolved)
    {
        var startInfo = new ProcessStartInfo();
        resolved.ApplyTo(startInfo);
        return startInfo.Environment;
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Proxy_placeholders_give_the_child_a_loopback_url_and_a_token_that_foundry_never_sees()
    {
        var auth = FoundrySignedIn();
        var resolver = Resolver(auth, new TestConsole());
        try
        {
            var resolved = await resolver.ResolveAsync(OneCapability("foundry", ProxyEnv), Unattended);
            var env = ChildEnvironment(resolved!);

            env["FOUNDRY_ENDPOINT"].Should().MatchRegex(@"^http://127\.0\.0\.1:\d+$");
            env["FOUNDRY_TOKEN"].Should().NotBeNullOrEmpty();
            env.Values.Should().NotContain(v => v != null && v.Contains(EntraToken),
                "the child must never hold the Entra token");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env["FOUNDRY_TOKEN"]);
            var seenByFoundry = await client.GetStringAsync($"{env["FOUNDRY_ENDPOINT"]}/speechtotext/whoami");

            seenByFoundry.Should().Be($"Bearer {EntraToken}");
            auth.Verify(x => x.GetAccessTokenAsync(new AzureFoundryAuthConfig().CognitiveScope, It.IsAny<CancellationToken>()));
        }
        finally
        {
            resolver.Release();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_proxy_token_is_hidden_in_a_dry_run_and_the_endpoint_is_not()
    {
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        try
        {
            var resolved = await resolver.ResolveAsync(OneCapability("foundry", ProxyEnv), Unattended);

            var described = resolved!.Describe().ToDictionary(d => d.Name, d => d.Display);
            described["FOUNDRY_TOKEN"].Should().Be("(set, hidden)");
            described["FOUNDRY_ENDPOINT"].Should().StartWith("http://127.0.0.1:");
        }
        finally
        {
            resolver.Release();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Two_capabilities_share_one_proxy_and_one_token()
    {
        var manifest = OneCapability("foundry", ProxyEnv);
        manifest.Capabilities.Add(new PksCapabilityManifest
        {
            Id = "chat",
            Providers =
            {
                new PksProviderManifest
                {
                    Kind = "foundry",
                    Env = new() { ["CHAT_URL"] = "{proxy:endpoint}/openai", ["CHAT_TOKEN"] = "{proxy:token}" },
                },
            },
        });
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        try
        {
            var env = ChildEnvironment((await resolver.ResolveAsync(manifest, Unattended))!);

            env["CHAT_URL"].Should().Be(env["FOUNDRY_ENDPOINT"] + "/openai");
            env["CHAT_TOKEN"].Should().Be(env["FOUNDRY_TOKEN"]);
        }
        finally
        {
            resolver.Release();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Release_stops_the_proxy()
    {
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        var env = ChildEnvironment((await resolver.ResolveAsync(OneCapability("foundry", ProxyEnv), Unattended))!);

        resolver.Release();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env["FOUNDRY_TOKEN"]);
        var call = () => client.GetAsync($"{env["FOUNDRY_ENDPOINT"]}/speechtotext/whoami");
        await call.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_provider_with_nothing_to_proxy_resolves_both_placeholders_to_empty()
    {
        var previous = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "gemini-key");
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        try
        {
            var resolved = await resolver.ResolveAsync(OneCapability("gemini", ProxyEnv), Unattended);

            var described = resolved!.Describe().ToDictionary(d => d.Name, d => d.Display);
            described["FOUNDRY_ENDPOINT"].Should().BeEmpty();
            described["FOUNDRY_TOKEN"].Should().Be("(not set)");
        }
        finally
        {
            resolver.Release();
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previous);
        }
    }
}
