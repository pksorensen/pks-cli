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
    private bool _upstreamSawCatalogRoute;
    private bool _upstreamSawChatRoute;
    private readonly List<(string Path, string Auth, string Model)> _chatCalls = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _foundry = builder.Build();
        _foundry.MapGet("/speechtotext/whoami", (HttpContext ctx) =>
            Results.Text(ctx.Request.Headers.Authorization.ToString()));
        // Doubles as an OpenAI-compatible upstream: its own /models listing, and a record of
        // whether pks's catalog route ever leaked through to it.
        _foundry.MapGet("/v1/models", (HttpContext ctx) =>
            Results.Text("""{"object":"list","data":[{"id":"upstream-chat","object":"model","owned_by":"upstream"}]}""", "application/json"));
        _foundry.MapGet("/v1/pks/v1/models", () => { _upstreamSawCatalogRoute = true; return Results.NotFound(); });
        // The Foundry data plane the catalog reads, and the two chat endpoints a routed request may land on.
        _foundry.MapGet("/openai/deployments", () =>
            Results.Text("""{"object":"list","data":[{"id":"gpt-test","model":"gpt-test","status":"succeeded"},{"id":"upstream-chat","model":"gpt-4o","status":"succeeded"}]}""", "application/json"));
        _foundry.MapPost("/openai/v1/chat/completions", RecordChatAsync);
        _foundry.MapPost("/v1/chat/completions", RecordChatAsync);
        _foundry.MapPost("/pks/v1/chat/completions", () => { _upstreamSawChatRoute = true; return Results.NotFound(); });
        _foundry.MapPost("/v1/pks/v1/chat/completions", () => { _upstreamSawChatRoute = true; return Results.NotFound(); });
        await _foundry.StartAsync();
        _foundryEndpoint = _foundry.Urls.Single() + "/";
    }

    public async Task DisposeAsync() => await _foundry.DisposeAsync();

    private async Task RecordChatAsync(HttpContext ctx)
    {
        var body = await System.Text.Json.Nodes.JsonNode.ParseAsync(ctx.Request.Body);
        _chatCalls.Add((ctx.Request.Path.ToString(), ctx.Request.Headers.Authorization.ToString(), body?["model"]?.GetValue<string>() ?? ""));
        ctx.Request.ContentLength.Should().NotBeNull("a routed body goes upstream with a Content-Length");
        var model = body?["model"]?.GetValue<string>() ?? "";
        var answer = "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"model\":\"" + model
            + "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok from " + ctx.Request.Path + "\"}}]}";
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(answer);
    }

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

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_openai_compatible_proxy_serves_the_catalog_itself_from_the_endpoints_models_and_never_forwards_the_route()
    {
        var previousBase = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", _foundryEndpoint + "v1");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-upstream");
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        try
        {
            var env = ChildEnvironment((await resolver.ResolveAsync(OneCapability("openai-compatible", ProxyEnv), Unattended))!);
            env["FOUNDRY_ENDPOINT"].Should().MatchRegex(@"^http://127\.0\.0\.1:\d+$");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env["FOUNDRY_TOKEN"]);
            var response = await client.GetAsync($"{env["FOUNDRY_ENDPOINT"]}{ProviderCatalog.Path}");

            response.IsSuccessStatusCode.Should().BeTrue();
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("\"id\":\"upstream-chat\"").And.Contain("\"provider\":\"openai-compatible\"");
            _upstreamSawCatalogRoute.Should().BeFalse("pks's catalog route must never be forwarded to the provider");
        }
        finally
        {
            resolver.Release();
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_chat_route_sends_each_model_to_its_own_provider_with_that_providers_credential()
    {
        var previousBase = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_BASE_URL", _foundryEndpoint + "v1");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-upstream");
        var resolver = Resolver(FoundrySignedIn(), new TestConsole());
        try
        {
            // The child is bound to Foundry; the openai-compatible endpoint is registered too.
            var env = ChildEnvironment((await resolver.ResolveAsync(OneCapability("foundry", ProxyEnv), Unattended))!);
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env["FOUNDRY_TOKEN"]);
            var chat = $"{env["FOUNDRY_ENDPOINT"]}{ProviderCatalog.ChatPath}";

            // The union is served on the Foundry proxy, provider-tagged.
            var catalog = await (await client.GetAsync($"{env["FOUNDRY_ENDPOINT"]}{ProviderCatalog.Path}")).Content.ReadAsStringAsync();
            catalog.Should().Contain("\"id\":\"gpt-test\"").And.Contain("\"provider\":\"foundry\"").And.Contain("\"provider\":\"openai-compatible\"");

            // A qualified id pins the provider and the prefix is stripped before it goes upstream.
            var pinned = await client.PostAsync(chat, Json("""{"model":"openai-compatible/upstream-chat","messages":[{"role":"user","content":"hi"}]}"""));
            pinned.IsSuccessStatusCode.Should().BeTrue(await pinned.Content.ReadAsStringAsync());
            _chatCalls.Should().ContainSingle().Which.Should().Be(("/v1/chat/completions", "Bearer sk-upstream", "upstream-chat"));

            // A bare id that both providers list goes to the first in catalog order — Foundry — with its token.
            _chatCalls.Clear();
            var bare = await client.PostAsync(chat, Json("""{"model":"upstream-chat","messages":[]}"""));
            var bareBody = await bare.Content.ReadAsStringAsync();
            bare.IsSuccessStatusCode.Should().BeTrue(bareBody);
            _chatCalls.Should().ContainSingle().Which.Should().Be(("/openai/v1/chat/completions", $"Bearer {EntraToken}", "upstream-chat"));
            bareBody.Should().Contain("ok from /openai/v1/chat/completions", "the provider's answer is relayed");

            // An unknown model is an OpenAI-shaped 404, and nothing is forwarded.
            _chatCalls.Clear();
            var unknown = await client.PostAsync(chat, Json("""{"model":"no-such-model","messages":[]}"""));
            ((int)unknown.StatusCode).Should().Be(404);
            var error = await unknown.Content.ReadAsStringAsync();
            error.Should().Contain("\"type\":\"invalid_request_error\"").And.Contain("not in the pks catalog").And.Contain("\"code\":\"model_not_found\"");
            _chatCalls.Should().BeEmpty();

            // A prefix naming a kind pks knows but has no credential for says so, rather than searching the others.
            var unregistered = await client.PostAsync(chat, Json("""{"model":"moonshot/kimi-k2","messages":[]}"""));
            ((int)unregistered.StatusCode).Should().Be(404);
            (await unregistered.Content.ReadAsStringAsync()).Should().Contain("'moonshot' is not registered");

            // Without a token the route is as closed as every other.
            using var anonymous = new HttpClient();
            ((int)(await anonymous.PostAsync(chat, Json("""{"model":"gpt-test"}"""))).StatusCode).Should().Be(401);

            _upstreamSawChatRoute.Should().BeFalse("pks's chat route must never be forwarded to the bound provider");
        }
        finally
        {
            resolver.Release();
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", previousBase);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    private static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");
}
