using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Models;

/// <summary>
/// The catalog a tool reads at /pks/v1/models: the resource's deployments in the OpenAI list shape,
/// joined with what pks foundry select chose. The join is what a tool relies on — the default and
/// the roles — so that is what is pinned here, against canned data-plane answers.
/// </summary>
public class ModelCatalogTests
{
    private const string Deployments = """
        {"data":[
          {"id":"gpt-5.5","model":"gpt-5.5","status":"succeeded","created_at":1760000000,"scale_settings":{"scale_type":"standard"}},
          {"id":"gpt-5.6-luna","model":"gpt-5.6-luna","status":"succeeded","created_at":1761000000},
          {"id":"Decision-1","model":"Microsoft-Decision-1","status":"succeeded"},
          {"id":"mai-transcribe-2","model":"MAI-Transcribe-2","status":"succeeded"},
          {"id":"text-embedding-3-large","model":"text-embedding-3-large","status":"succeeded"},
          {"id":"broken","model":"gpt-4o","status":"failed"}
        ],"object":"list"}
        """;

    private const string Models = """
        {"data":[
          {"id":"gpt-5.5","model":{"version":"2026-06-01"},"capabilities":{"chat_completion":true,"embeddings":false}},
          {"id":"text-embedding-3-large","capabilities":{"embeddings":true}}
        ],"object":"list"}
        """;

    private sealed class CannedHandler : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public readonly List<string?> Auth = new();
        public HttpStatusCode ModelsStatus = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Auth.Add(request.Headers.Authorization?.ToString());
            var path = request.RequestUri.AbsolutePath;
            var (status, body) = path switch
            {
                "/openai/deployments" => (HttpStatusCode.OK, Deployments),
                "/openai/models" => (ModelsStatus, Models),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static FoundryStoredCredentials Creds() => new()
    {
        SelectedResourceEndpoint = "https://res.services.ai.azure.com/",
        DefaultModel = "gpt-5.5",
        EnabledModels = new List<string> { "gpt-5.5", "gpt-5.6-luna" },
        VoiceClassifierModel = "Decision-1",
    };

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Deployments_become_the_list_with_default_roles_and_enabled_from_the_stored_selection()
    {
        var handler = new CannedHandler();
        using var http = new HttpClient(handler);

        var list = await ModelCatalog.BuildAsync(http, "https://res.services.ai.azure.com/", SecretValue.From("tok"), Creds(), CancellationToken.None);

        list.Object.Should().Be("list");
        list.Data.Select(d => d.Id).Should().Equal("gpt-5.5", "gpt-5.6-luna", "Decision-1", "mai-transcribe-2", "text-embedding-3-large", "broken");
        handler.Auth.Should().OnlyContain(a => a == "Bearer tok");
        handler.Paths.Should().Equal(
            "/openai/deployments?api-version=2023-03-15-preview",
            "/openai/models?api-version=2024-10-21");

        var def = list.Data.Single(d => d.Id == "gpt-5.5");
        def.Pks.Default.Should().BeTrue();
        def.Pks.Enabled.Should().BeTrue();
        def.Pks.Roles.Should().Equal("text");
        def.Pks.Model.Version.Should().Be("2026-06-01");
        def.Pks.Capabilities.Should().Contain("chat_completion", true);
        def.Created.Should().Be(1760000000);
        def.Architecture.Modality.Should().Be("text->text");

        var decision = list.Data.Single(d => d.Id == "Decision-1");
        decision.Pks.Roles.Should().Equal("decision");
        decision.Pks.Enabled.Should().BeFalse("it was not ticked in pks foundry select");
        decision.Pks.Default.Should().BeFalse();
        decision.Architecture.Modality.Should().Be("text->choice");

        list.Data.Single(d => d.Id == "mai-transcribe-2").Architecture.Modality.Should().Be("audio->text");
        list.Data.Single(d => d.Id == "text-embedding-3-large").Architecture.Modality.Should().Be("text->embedding");
        list.Data.Single(d => d.Id == "broken").Pks.Status.Should().Be("failed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Nothing_ticked_means_every_deployment_is_enabled_and_no_roles()
    {
        using var http = new HttpClient(new CannedHandler());

        var list = await ModelCatalog.BuildAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"),
            new FoundryStoredCredentials { SelectedResourceEndpoint = "https://res.services.ai.azure.com" }, CancellationToken.None);

        list.Data.Should().OnlyContain(d => d.Pks.Enabled);
        list.Data.Should().OnlyContain(d => !d.Pks.Default && d.Pks.Roles.Length == 0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_failed_models_read_still_yields_the_catalog_without_capabilities()
    {
        var handler = new CannedHandler { ModelsStatus = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler);

        var list = await ModelCatalog.BuildAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"), Creds(), CancellationToken.None);

        list.Data.Should().HaveCount(6);
        list.Data.Single(d => d.Id == "gpt-5.5").Pks.Capabilities.Should().BeNull();
        list.Data.Single(d => d.Id == "gpt-5.5").Pks.Model.Version.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_json_has_the_OpenAI_envelope_and_a_pks_object_per_entry()
    {
        using var http = new HttpClient(new CannedHandler());
        var list = await ModelCatalog.BuildAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"), Creds(), CancellationToken.None);

        using var doc = JsonDocument.Parse(ModelCatalog.ToJson(list));
        var root = doc.RootElement;
        root.GetProperty("object").GetString().Should().Be("list");
        var first = root.GetProperty("data")[0];
        first.GetProperty("id").GetString().Should().Be("gpt-5.5");
        first.GetProperty("object").GetString().Should().Be("model");
        first.GetProperty("owned_by").GetString().Should().Be("azure");
        first.GetProperty("architecture").GetProperty("modality").GetString().Should().Be("text->text");
        var pks = first.GetProperty("pks");
        pks.GetProperty("default").GetBoolean().Should().BeTrue();
        pks.GetProperty("roles")[0].GetString().Should().Be("text");
        pks.GetProperty("model").GetProperty("name").GetString().Should().Be("gpt-5.5");
        first.TryGetProperty("description", out _).Should().BeTrue();
    }
}
