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
public class ProviderCatalogTests
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

    private const string OpenRouterModels = """
        {"data":[
          {"id":"openai/gpt-5.5","name":"OpenAI: GPT-5.5","created":1759000000,"description":"Frontier model",
           "architecture":{"modality":"text+image->text","input_modalities":["text","image"],"output_modalities":["text"]},
           "context_length":400000},
          {"id":"openai/text-embedding-3-small","created":1700000000}
        ],"object":"list"}
        """;

    private const string GeminiModels = """
        {"models":[
          {"name":"models/gemini-2.5-pro","displayName":"Gemini 2.5 Pro","version":"2.5","description":"Our most capable model",
           "inputTokenLimit":1048576,"outputTokenLimit":65536,"supportedGenerationMethods":["generateContent","countTokens"]},
          {"name":"models/gemini-embedding-001","displayName":"Gemini Embedding","supportedGenerationMethods":["embedContent"]},
          {"name":"models/gemini-2.5-flash-preview-tts","displayName":"Gemini 2.5 Flash TTS","supportedGenerationMethods":["generateContent"]}
        ]}
        """;

    private sealed class CannedHandler : HttpMessageHandler
    {
        public readonly List<string> Paths = new();
        public readonly List<string?> Auth = new();
        public readonly List<string?> GoogleKey = new();
        public HttpStatusCode ModelsStatus = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            Auth.Add(request.Headers.Authorization?.ToString());
            GoogleKey.Add(request.Headers.TryGetValues("x-goog-api-key", out var keys) ? string.Join(",", keys) : null);
            var path = request.RequestUri.AbsolutePath;
            var (status, body) = path switch
            {
                "/openai/deployments" => (HttpStatusCode.OK, Deployments),
                "/openai/models" => (ModelsStatus, Models),
                "/api/v1/models" => (HttpStatusCode.OK, OpenRouterModels),
                "/v1beta/models" => (HttpStatusCode.OK, GeminiModels),
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

        var list = await ProviderCatalog.BuildFoundryAsync(http, "https://res.services.ai.azure.com/", SecretValue.From("tok"), Creds(), CancellationToken.None);

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

        var list = await ProviderCatalog.BuildFoundryAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"),
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

        var list = await ProviderCatalog.BuildFoundryAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"), Creds(), CancellationToken.None);

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
        var list = await ProviderCatalog.BuildFoundryAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"), Creds(), CancellationToken.None);

        using var doc = JsonDocument.Parse(ProviderCatalog.ToJson(list));
        var root = doc.RootElement;
        root.GetProperty("object").GetString().Should().Be("list");
        var first = root.GetProperty("data")[0];
        first.GetProperty("id").GetString().Should().Be("gpt-5.5");
        first.GetProperty("object").GetString().Should().Be("model");
        first.GetProperty("owned_by").GetString().Should().Be("foundry");
        first.GetProperty("pks").GetProperty("provider").GetString().Should().Be("foundry");
        first.GetProperty("architecture").GetProperty("modality").GetString().Should().Be("text->text");
        var pks = first.GetProperty("pks");
        pks.GetProperty("default").GetBoolean().Should().BeTrue();
        pks.GetProperty("roles")[0].GetString().Should().Be("text");
        pks.GetProperty("model").GetProperty("name").GetString().Should().Be("gpt-5.5");
        first.TryGetProperty("description", out _).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_OpenAI_compatible_endpoint_is_listed_at_its_models_route_with_OpenRouter_extras_kept()
    {
        var handler = new CannedHandler();
        using var http = new HttpClient(handler);

        var list = await ProviderCatalog.BuildOpenAiCompatibleAsync(http, "https://openrouter.ai/api/v1/", SecretValue.From("sk-or"), CancellationToken.None);

        handler.Paths.Should().Equal("/api/v1/models");
        handler.Auth.Should().Equal("Bearer sk-or");
        list.Data.Select(d => d.Id).Should().Equal("openai/gpt-5.5", "openai/text-embedding-3-small");

        var gpt = list.Data[0];
        gpt.OwnedBy.Should().Be("openai-compatible");
        gpt.Pks.Provider.Should().Be("openai-compatible");
        gpt.Name.Should().Be("OpenAI: GPT-5.5");
        gpt.Architecture.Modality.Should().Be("text+image->text");
        gpt.Architecture.InputModalities.Should().Equal("text", "image");
        gpt.Pks.Limit!.Context.Should().Be(400000);
        gpt.Pks.Enabled.Should().BeTrue();
        gpt.Pks.Status.Should().Be("available");
        gpt.Pks.Roles.Should().BeEmpty();

        list.Data[1].Architecture.Modality.Should().Be("text->embedding", "no architecture given, so the name decides");
        list.Data[1].Name.Should().Be("openai/text-embedding-3-small");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Gemini_models_are_listed_with_the_key_in_a_header_and_their_limits_kept()
    {
        var handler = new CannedHandler();
        using var http = new HttpClient(handler);

        var list = await ProviderCatalog.BuildGeminiAsync(http, SecretValue.From("AIza"), CancellationToken.None, "https://gemini.test/v1beta");

        handler.Paths.Should().Equal("/v1beta/models?pageSize=1000");
        handler.GoogleKey.Should().Equal("AIza");
        handler.Auth.Should().Equal((string?)null);
        list.Data.Select(d => d.Id).Should().Equal("gemini-2.5-pro", "gemini-embedding-001", "gemini-2.5-flash-preview-tts");

        var pro = list.Data[0];
        pro.OwnedBy.Should().Be("gemini");
        pro.Pks.Provider.Should().Be("gemini");
        pro.Name.Should().Be("Gemini 2.5 Pro");
        pro.Pks.Model.Version.Should().Be("2.5");
        pro.Architecture.Modality.Should().Be("text+image->text");
        pro.Pks.Limit!.Context.Should().Be(1048576);
        pro.Pks.Limit.Output.Should().Be(65536);
        pro.Pks.Capabilities.Should().ContainKey("generateContent");

        list.Data[1].Architecture.Modality.Should().Be("text->embedding");
        list.Data[2].Architecture.Modality.Should().Be("text->audio");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Merged_lists_keep_provider_order_and_each_entry_names_its_provider()
    {
        using var http = new HttpClient(new CannedHandler());
        var foundry = await ProviderCatalog.BuildFoundryAsync(http, "https://res.services.ai.azure.com", SecretValue.From("tok"), Creds(), CancellationToken.None);
        var gemini = await ProviderCatalog.BuildGeminiAsync(http, SecretValue.From("AIza"), CancellationToken.None, "https://gemini.test/v1beta");

        var merged = ProviderCatalog.Merge([foundry, gemini]);

        merged.Data.Should().HaveCount(foundry.Data.Count + gemini.Data.Count);
        merged.Data.Take(foundry.Data.Count).Should().OnlyContain(d => d.Pks.Provider == "foundry");
        merged.Data.Skip(foundry.Data.Count).Should().OnlyContain(d => d.Pks.Provider == "gemini");
    }
}
