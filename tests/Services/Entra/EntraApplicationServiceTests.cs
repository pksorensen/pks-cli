using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PKS.CLI.Tests.Security;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Entra;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Entra;

/// <summary>
/// `pks entra app init` against a Graph that is not the company directory.
///
/// These are the cases that cannot be discovered by running it once: the second run must adopt rather
/// than mint a twin, a redirect URI must be added to what is already registered rather than replace it,
/// and a rotation must actually remove the credential it replaced. Every one of those failures looks
/// like success in a terminal and shows up days later as an app that cannot sign in — or, for the
/// duplicate, as two registrations with the same name in a tenant somebody else administers.
/// </summary>
public class EntraApplicationServiceTests
{
    // ─────────────────────────────────────────────
    //  A Graph that answers from a script
    // ─────────────────────────────────────────────

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            return handle(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private const string ApplicationJson = """
        {"id":"obj-1","appId":"app-1","displayName":"Margin v1 (dev)","signInAudience":"AzureADMyOrg",
         "web":{"redirectUris":["http://localhost:3200/existing"]}}
        """;

    /// <summary>A store that keeps what was written, so a second call can read the first one's result —
    /// the whole adopt-or-rotate decision hangs on that.</summary>
    private sealed class MemoryStore
    {
        private readonly Dictionary<string, string> _values = new();

        public IConfigurationService Configuration
        {
            get
            {
                var mock = new Mock<IConfigurationService>();
                mock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
                    .Callback<string, string, bool, bool>((k, v, _, _) => _values[k] = v)
                    .Returns(Task.CompletedTask);
                mock.Setup(c => c.DeleteAsync(It.IsAny<string>()))
                    .Callback<string>(k => _values.Remove(k))
                    .Returns(Task.CompletedTask);
                return mock.Object;
            }
        }

        public ISecretStore Secrets
        {
            get
            {
                var mock = new Mock<ISecretStore>();
                mock.Setup(s => s.HasAsync(It.IsAny<string>()))
                    .Returns<string>(k => Task.FromResult(_values.ContainsKey(k)));
                mock.Setup(s => s.ListAsync())
                    .Returns(() => Task.FromResult<IReadOnlyList<SecretDescriptor>>(
                        _values.Keys.Select(k => new SecretDescriptor(k, DateTime.UtcNow, "fp")).ToList()));
                return mock.Object;
            }
        }

        public ISecretResolver Resolver =>
            FakeSecretResolver.BackedBy(k => Task.FromResult(_values.TryGetValue(k, out var v) ? v : null));

        public string? Raw(string key) => _values.TryGetValue(key, out var v) ? v : null;
    }

    private static EntraApplicationService Build(Handler handler, MemoryStore store)
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("graph-token");
        auth.Setup(a => a.GetStoredCredentialsAsync())
            .ReturnsAsync(new FoundryStoredCredentials { TenantId = "tenant-1" });

        return new EntraApplicationService(
            new HttpClient(handler),
            auth.Object,
            store.Configuration,
            store.Secrets,
            store.Resolver,
            NullLogger<EntraApplicationService>.Instance);
    }

    // ─────────────────────────────────────────────
    //  Adopt, never duplicate
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_existing_registration_is_adopted_rather_than_duplicated()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
            "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
            "/v1.0/applications/obj-1/addPassword" => Json(
                """{"keyId":"key-1","secretText":"the-secret","endDateTime":"2027-01-01T00:00:00Z"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
        });

        var result = await Build(handler, new MemoryStore()).InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
        });

        result.CreatedApplication.Should().BeFalse();
        result.App.AppId.Should().Be("app-1");
        handler.Calls.Should().NotContain(c => c.Method == "POST" && c.Url.EndsWith("/v1.0/applications"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_new_registration_gets_a_service_principal_too()
    {
        // An app registration with no service principal is a definition nothing can sign in against.
        // `az ad app create` leaves exactly that state behind and the error arrives much later.
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1.0/applications" && request.Method == HttpMethod.Get)
                return Json("""{"value":[]}""");
            if (path == "/v1.0/applications" && request.Method == HttpMethod.Post)
                return Json(ApplicationJson);
            if (path == "/v1.0/servicePrincipals" && request.Method == HttpMethod.Get)
                return Json("""{"value":[]}""");
            if (path == "/v1.0/servicePrincipals" && request.Method == HttpMethod.Post)
                return Json("""{"id":"sp-1"}""");
            if (path == "/v1.0/applications/obj-1/addPassword")
                return Json("""{"keyId":"key-1","secretText":"the-secret","endDateTime":"2027-01-01T00:00:00Z"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });

        var result = await Build(handler, new MemoryStore()).InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
        });

        result.CreatedApplication.Should().BeTrue();
        result.CreatedServicePrincipal.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Redirect_uris_are_added_to_the_ones_already_registered()
    {
        // A PATCH replaces the whole collection. Sending only the new URI silently unregisters the one
        // somebody else added, and every existing sign-in breaks.
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
            "/v1.0/applications/obj-1" => Json(ApplicationJson),
            "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
            "/v1.0/applications/obj-1/addPassword" => Json(
                """{"keyId":"key-1","secretText":"the-secret","endDateTime":"2027-01-01T00:00:00Z"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
        });

        var result = await Build(handler, new MemoryStore()).InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
            RedirectUris = { "http://localhost:3200/new" },
        });

        result.AddedRedirectUris.Should().BeTrue();

        var patch = handler.Calls.Single(c => c.Method == "PATCH");
        patch.Body.Should().Contain("http://localhost:3200/existing");
        patch.Body.Should().Contain("http://localhost:3200/new");
    }

    // ─────────────────────────────────────────────
    //  The secret
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_client_secret_is_stored_and_never_returned_as_a_string()
    {
        var store = new MemoryStore();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
            "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
            "/v1.0/applications/obj-1/addPassword" => Json(
                """{"keyId":"key-1","secretText":"the-secret","endDateTime":"2027-01-01T00:00:00Z"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
        });

        var result = await Build(handler, store).InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
        });

        result.MintedSecret.Should().BeTrue();
        result.App.ClientSecret.HasValue.Should().BeTrue();

        // What a careless command would print — the DTO, the field, an interpolation — is a mask.
        result.App.ClientSecret.ToString().Should().Be("***");
        JsonSerializer.Serialize(result.App).Should().NotContain("the-secret");

        // It is in the store, though, or the next run could not use it. The key ends in
        // ".credentials", which is what routes it into the encrypted half rather than settings.json.
        store.Raw("entra.app.margin-dev.credentials").Should().Contain("the-secret");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_second_run_reuses_the_stored_secret_instead_of_minting_another()
    {
        // Graph allows any number of credentials on one registration, so a command that mints on every
        // run quietly fills the registration up and leaves live secrets nobody is tracking.
        var store = new MemoryStore();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
            "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
            "/v1.0/applications/obj-1/addPassword" => Json(
                """{"keyId":"key-1","secretText":"the-secret","endDateTime":"2027-01-01T00:00:00Z"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
        });

        var service = Build(handler, store);
        var request = new EntraAppRequest { DisplayName = "Margin v1 (dev)", Alias = "margin-dev" };

        await service.InitAsync(request);
        var second = await service.InitAsync(request);

        second.MintedSecret.Should().BeFalse();
        handler.Calls.Count(c => c.Url.EndsWith("addPassword")).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Rotating_removes_the_credential_it_replaced()
    {
        var store = new MemoryStore();
        var minted = 0;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("addPassword"))
            {
                minted++;
                return Json($$"""{"keyId":"key-{{minted}}","secretText":"secret-{{minted}}","endDateTime":"2027-01-01T00:00:00Z"}""");
            }
            return path switch
            {
                "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
                "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
                "/v1.0/applications/obj-1/removePassword" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
            };
        });

        var service = Build(handler, store);
        await service.InitAsync(new EntraAppRequest { DisplayName = "Margin v1 (dev)", Alias = "margin-dev" });
        var rotated = await service.InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
            Rotate = true,
        });

        rotated.MintedSecret.Should().BeTrue();
        rotated.RemovedSecretKeyId.Should().Be("key-1");
        handler.Calls.Single(c => c.Url.EndsWith("removePassword")).Body.Should().Contain("key-1");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_expired_stored_secret_is_replaced_without_being_asked()
    {
        // The stored credential outlives its end date silently; the first thing anybody sees is
        // AADSTS7000222 on a Tuesday. If it is dead, mint a live one.
        var store = new MemoryStore();
        var minted = 0;
        var handler = new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("addPassword"))
            {
                minted++;
                var end = minted == 1 ? "2020-01-01T00:00:00Z" : "2027-01-01T00:00:00Z";
                return Json($$"""{"keyId":"key-{{minted}}","secretText":"secret-{{minted}}","endDateTime":"{{end}}"}""");
            }
            return path switch
            {
                "/v1.0/applications" => Json($$"""{"value":[{{ApplicationJson}}]}"""),
                "/v1.0/servicePrincipals" => Json("""{"value":[{"id":"sp-1"}]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") },
            };
        });

        var service = Build(handler, store);
        var request = new EntraAppRequest { DisplayName = "Margin v1 (dev)", Alias = "margin-dev" };

        await service.InitAsync(request);
        var second = await service.InitAsync(request);

        second.MintedSecret.Should().BeTrue();
        second.App.IsExpired.Should().BeFalse();
    }

    // ─────────────────────────────────────────────
    //  Failure surfaces
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_graph_refusal_keeps_its_own_words()
    {
        // "403" tells nobody anything. "Authorization_RequestDenied: Insufficient privileges to
        // complete the operation" is the difference between asking an admin and debugging for an hour.
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}"""),
        });

        var act = async () => await Build(handler, new MemoryStore()).InitAsync(new EntraAppRequest
        {
            DisplayName = "Margin v1 (dev)",
            Alias = "margin-dev",
        });

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Authorization_RequestDenied*Insufficient privileges*");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData("Margin v1 (dev)", "margin-v1-dev")]
    [InlineData("  Margin  ", "margin")]
    [InlineData("ctx/margin", "ctx-margin")]
    public void An_alias_is_the_same_alias_however_it_was_typed(string input, string expected)
        => EntraApplicationService.Slug(input).Should().Be(expected);
}
