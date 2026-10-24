using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// The model catalog pks exposes to tools: what the selected Foundry resource can serve, in the
/// OpenAI <c>GET /v1/models</c> list shape with OpenRouter's descriptive fields on top and the
/// pks-specific facts (what <c>pks foundry select</c> enabled, the default, the roles) under one
/// <c>pks</c> object. There is no standard for model catalogs beyond that list shape; OpenRouter
/// and models.dev agree on the useful extras, so nothing here is invented.
///
/// Served as <c>GET /pks/v1/models</c> on the exec credential proxy (a tool started by
/// <c>pks exec</c> reads it with the endpoint and token it already holds) and printed by
/// <c>pks foundry models</c>.
/// </summary>
public static class ModelCatalog
{
    public const string Path = "/pks/v1/models";

    /// <summary>Manifest role vocabulary for the deployments <c>pks foundry select</c> chose.</summary>
    public const string TextRole = "text";
    public const string DecisionRole = "decision";

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public sealed class List
    {
        [JsonPropertyName("object")] public string Object { get; init; } = "list";
        [JsonPropertyName("data")] public List<Entry> Data { get; init; } = new();
    }

    public sealed class Entry
    {
        /// <summary>The deployment name: what a request's <c>model</c> field takes.</summary>
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("object")] public string Object { get; init; } = "model";
        [JsonPropertyName("created")] public long? Created { get; init; }
        [JsonPropertyName("owned_by")] public string OwnedBy { get; init; } = "azure";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("architecture")] public Architecture Architecture { get; init; } = new();
        [JsonPropertyName("pks")] public PksFacts Pks { get; init; } = new();
    }

    /// <summary>OpenRouter's shape: <c>modality</c> is <c>input->output</c> for a glance.</summary>
    public sealed class Architecture
    {
        [JsonPropertyName("modality")] public string Modality { get; init; } = "text->text";
        [JsonPropertyName("input_modalities")] public string[] InputModalities { get; init; } = ["text"];
        [JsonPropertyName("output_modalities")] public string[] OutputModalities { get; init; } = ["text"];
    }

    public sealed class PksFacts
    {
        /// <summary>Ticked in <c>pks foundry select</c>; every deployment when nothing was ticked.</summary>
        [JsonPropertyName("enabled")] public bool Enabled { get; init; }
        /// <summary>The default model from <c>pks foundry select</c>.</summary>
        [JsonPropertyName("default")] public bool Default { get; init; }
        /// <summary>Manifest roles this deployment fills: <c>text</c> (the default model),
        /// <c>decision</c> (the voice classifier).</summary>
        [JsonPropertyName("roles")] public string[] Roles { get; init; } = [];
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("model")] public BaseModel Model { get; init; } = new();
        /// <summary>Azure's own capability flags for the base model, when the models list had them.</summary>
        [JsonPropertyName("capabilities")] public Dictionary<string, bool>? Capabilities { get; init; }
    }

    public sealed class BaseModel
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("format")] public string? Format { get; init; }
    }

    /// <summary>
    /// Reads the resource's deployments (data plane, so the Cognitive Services token is enough) and
    /// the base-model capabilities, and joins them with what is stored for the resource. A failure
    /// to read capabilities is not a failure of the catalog: the modality is then inferred from the
    /// name, which is what heypoul did before pks served this list.
    /// </summary>
    public static async Task<List> BuildAsync(
        HttpClient http, string endpoint, SecretValue token, FoundryStoredCredentials? creds, CancellationToken ct)
    {
        endpoint = endpoint.TrimEnd('/');
        var deployments = await GetJsonAsync(http, $"{endpoint}/openai/deployments?api-version=2023-03-15-preview", token, ct)
            ?? throw new InvalidOperationException("could not list the resource's deployments");
        JsonNode? models = null;
        try
        {
            models = await GetJsonAsync(http, $"{endpoint}/openai/models?api-version=2024-10-21", token, ct);
        }
        catch (HttpRequestException)
        {
            // Capabilities are a nicety; the deployments are the catalog.
        }

        var capabilities = new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models?["data"]?.AsArray() ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var caps = new Dictionary<string, bool>();
            foreach (var kv in m?["capabilities"]?.AsObject() ?? [])
                if (kv.Value is JsonValue v && v.TryGetValue<bool>(out var b)) caps[kv.Key] = b;
            capabilities[id] = caps;
            if (m?["model"]?["version"]?.GetValue<string>() is { } version) versions[id] = version;
        }

        var enabled = creds?.EnabledModels ?? new List<string>();
        var list = new List();
        foreach (var d in deployments["data"]?.AsArray() ?? [])
        {
            var id = d?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var baseName = d?["model"]?.GetValue<string>() ?? "";
            var caps = capabilities.TryGetValue(baseName, out var c) ? c
                : capabilities.FirstOrDefault(kv => kv.Key.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase)).Value;
            var roles = new List<string>();
            if (Is(creds?.DefaultModel, id)) roles.Add(TextRole);
            if (Is(creds?.VoiceClassifierModel, id)) roles.Add(DecisionRole);
            var status = d?["status"]?.GetValue<string>() ?? "";
            long? created = d?["created_at"] is JsonValue cv && cv.TryGetValue<long>(out var epoch) ? epoch : null;
            list.Data.Add(new Entry
            {
                Id = id,
                Created = created,
                Name = baseName == "" ? id : baseName,
                Description = status == "" ? null : $"{baseName} deployment, {status}",
                Architecture = Modality(id, baseName, caps),
                Pks = new PksFacts
                {
                    Enabled = enabled.Count == 0 || enabled.Any(e => Is(e, id)),
                    Default = Is(creds?.DefaultModel, id),
                    Roles = roles.ToArray(),
                    Status = status,
                    Model = new BaseModel
                    {
                        Name = baseName,
                        Version = versions.TryGetValue(baseName, out var ver) ? ver : null,
                        Format = d?["scale_settings"] is null ? null : "OpenAI",
                    },
                    Capabilities = caps,
                },
            });
        }
        return list;
    }

    public static string ToJson(List list) => JsonSerializer.Serialize(list, Json);

    private static bool Is(string? a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What goes in and what comes out. Azure's capability flags settle chat and embeddings; the
    /// rest is read from the name, which is the only signal the deployments list carries.
    /// </summary>
    public static Architecture Modality(string id, string baseName, Dictionary<string, bool>? caps)
    {
        var n = (id + " " + baseName).ToLowerInvariant();
        string[] inputs, outputs;
        if (n.Contains("transcribe") || n.Contains("whisper") || n.Contains("diarize"))
            (inputs, outputs) = (["audio"], ["text"]);
        else if (n.Contains("tts") || n.Contains("speech"))
            (inputs, outputs) = (["text"], ["audio"]);
        else if (n.Contains("realtime"))
            (inputs, outputs) = (["audio", "text"], ["audio", "text"]);
        else if (n.Contains("embedding") || n.Contains("embed-") || (caps?.TryGetValue("embeddings", out var e) == true && e))
            (inputs, outputs) = (["text"], ["embedding"]);
        else if (n.Contains("image") || n.Contains("dall-e"))
            (inputs, outputs) = (["text", "image"], ["image"]);
        else if (n.Contains("sora") || n.Contains("video"))
            (inputs, outputs) = (["text", "image"], ["video"]);
        else if (n.Contains("decision"))
            (inputs, outputs) = (["text"], ["choice"]);
        else if (n.Contains("rerank") || n.Contains("moderation"))
            (inputs, outputs) = (["text"], ["score"]);
        else
            (inputs, outputs) = (["text"], ["text"]);
        return new Architecture
        {
            Modality = string.Join("+", inputs) + "->" + string.Join("+", outputs),
            InputModalities = inputs,
            OutputModalities = outputs,
        };
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, string url, SecretValue token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!SecretSink.SetBearerToken(request, token))
            throw new InvalidOperationException("no Foundry token");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: HTTP {(int)response.StatusCode}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }
}
