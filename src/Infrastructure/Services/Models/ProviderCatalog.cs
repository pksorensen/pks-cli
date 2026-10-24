using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// The model catalog pks exposes to tools: the models reachable through the providers registered
/// with pks, in the OpenAI <c>GET /v1/models</c> list shape with OpenRouter's descriptive fields on
/// top and the pks-specific facts (which provider, what <c>pks foundry select</c> enabled, the
/// default, the roles) under one <c>pks</c> object. There is no standard for model catalogs beyond
/// that list shape; OpenRouter and models.dev agree on the useful extras, so nothing here is invented.
///
/// pks is the gateway: a tool never lists models at a provider itself. Every exec credential proxy
/// serves the union across every registered provider at <c>GET /pks/v1/models</c> and routes
/// <c>POST /pks/v1/chat/completions</c> to the provider that owns the requested model (see
/// <see cref="ChatPath"/>); <c>pks models</c> prints the same union.
/// (Not to be confused with <see cref="PKS.Infrastructure.Models.ModelCatalog"/>, the on-device
/// speech models of <c>pks model</c>.)
/// </summary>
public static class ProviderCatalog
{
    public const string Path = "/pks/v1/models";

    /// <summary>
    /// The routed chat route: an OpenAI chat-completions request whose <c>model</c> is either a bare
    /// catalog id (the first provider listing it wins) or <c>provider/id</c>. The proxy forwards it to
    /// that provider's OpenAI-compatible chat endpoint with that provider's credential.
    /// </summary>
    public const string ChatPath = "/pks/v1/chat/completions";

    /// <summary>Provider kinds, as <c>pks exec</c> manifests and <c>pks &lt;provider&gt; init</c> name them.</summary>
    public const string Foundry = "foundry";
    public const string Gemini = "gemini";
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string Moonshot = "moonshot";
    public const string TypeSafe = "typesafe";
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>Every kind the catalog knows, in catalog order — the order a bare model id is resolved in.</summary>
    public static readonly string[] Kinds = [Foundry, Anthropic, OpenAi, Gemini, Moonshot, TypeSafe, OpenAiCompatible];

    /// <summary>Manifest role vocabulary for the deployments <c>pks foundry select</c> chose.</summary>
    public const string TextRole = "text";
    public const string DecisionRole = "decision";

    public const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    public const string AnthropicBaseUrl = "https://api.anthropic.com";
    public const string OpenAiBaseUrl = "https://api.openai.com/v1";
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// Where a provider answers an OpenAI chat-completions request. Every provider pks registers has
    /// such a route — Gemini and Anthropic publish OpenAI-compatible layers — except TypeSafe, whose
    /// API judges (<c>/v1/systemone</c>) rather than chats, so it returns null.
    /// </summary>
    public static string? ChatCompletionsUrl(string kind, string? baseUrl) => kind.ToLowerInvariant() switch
    {
        Foundry => string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/openai/v1/chat/completions",
        Gemini => $"{(string.IsNullOrEmpty(baseUrl) ? GeminiBaseUrl : baseUrl.TrimEnd('/'))}/openai/chat/completions",
        Anthropic => $"{(string.IsNullOrEmpty(baseUrl) ? AnthropicBaseUrl : baseUrl.TrimEnd('/'))}/v1/chat/completions",
        OpenAi => $"{(string.IsNullOrEmpty(baseUrl) ? OpenAiBaseUrl : baseUrl.TrimEnd('/'))}/chat/completions",
        Moonshot or OpenAiCompatible => string.IsNullOrEmpty(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}/chat/completions",
        _ => null,
    };

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
        /// <summary>The model name a request's <c>model</c> field takes at that provider.</summary>
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("object")] public string Object { get; init; } = "model";
        [JsonPropertyName("created")] public long? Created { get; init; }
        /// <summary>The provider kind the model is reachable through.</summary>
        [JsonPropertyName("owned_by")] public string OwnedBy { get; init; } = "";
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
        /// <summary>The provider kind the model is reachable through — one of <see cref="Kinds"/>. Prefix a
        /// chat request's <c>model</c> with it (<c>provider/id</c>) to pin the provider.</summary>
        [JsonPropertyName("provider")] public string Provider { get; init; } = "";
        /// <summary>Ticked in <c>pks foundry select</c>; every model when the provider has no selection.</summary>
        [JsonPropertyName("enabled")] public bool Enabled { get; init; }
        /// <summary>The default model from <c>pks foundry select</c>.</summary>
        [JsonPropertyName("default")] public bool Default { get; init; }
        /// <summary>Manifest roles this model fills: <c>text</c> (the default model),
        /// <c>decision</c> (the voice classifier).</summary>
        [JsonPropertyName("roles")] public string[] Roles { get; init; } = [];
        /// <summary>Azure's provisioning status for a deployment; <c>available</c> elsewhere.</summary>
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("model")] public BaseModel Model { get; init; } = new();
        /// <summary>The provider's own capability flags, when its listing had them.</summary>
        [JsonPropertyName("capabilities")] public Dictionary<string, bool>? Capabilities { get; init; }
        /// <summary>Token limits, when the provider states them (Gemini does).</summary>
        [JsonPropertyName("limit")] public Limits? Limit { get; init; }
    }

    public sealed class BaseModel
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("format")] public string? Format { get; init; }
    }

    public sealed class Limits
    {
        [JsonPropertyName("context")] public long? Context { get; init; }
        [JsonPropertyName("output")] public long? Output { get; init; }
    }

    public static string ToJson(List list) => JsonSerializer.Serialize(list, Json);

    /// <summary>One list out of several providers' lists, in the order given.</summary>
    public static List Merge(IEnumerable<List> lists)
    {
        var merged = new List();
        foreach (var l in lists) merged.Data.AddRange(l.Data);
        return merged;
    }

    // ---------- foundry ----------

    /// <summary>
    /// Reads the resource's deployments (data plane, so the Cognitive Services token is enough) and
    /// the base-model capabilities, and joins them with what is stored for the resource. A failure
    /// to read capabilities is not a failure of the catalog: the modality is then inferred from the
    /// name, which is what heypoul did before pks served this list.
    /// </summary>
    public static async Task<List> BuildFoundryAsync(
        HttpClient http, string endpoint, SecretValue token, FoundryStoredCredentials? creds, CancellationToken ct)
    {
        endpoint = endpoint.TrimEnd('/');
        var deployments = await GetJsonAsync(http, $"{endpoint}/openai/deployments?api-version=2023-03-15-preview", Bearer(token), ct)
            ?? throw new InvalidOperationException("could not list the resource's deployments");
        JsonNode? models = null;
        try
        {
            models = await GetJsonAsync(http, $"{endpoint}/openai/models?api-version=2024-10-21", Bearer(token), ct);
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
                OwnedBy = Foundry,
                Name = baseName == "" ? id : baseName,
                Description = status == "" ? null : $"{baseName} deployment, {status}",
                Architecture = Modality(id, baseName, caps),
                Pks = new PksFacts
                {
                    Provider = Foundry,
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

    // ---------- openai-compatible ----------

    /// <summary>
    /// <c>GET {base}/models</c> at the configured OpenAI-compatible endpoint, which every such
    /// server answers in the OpenAI list shape. OpenRouter's extras (<c>name</c>,
    /// <c>description</c>, <c>architecture</c>) are kept when present; otherwise the modality is
    /// read from the name. Nothing is enabled or defaulted by pks for this kind, so every model is
    /// enabled and no roles are assigned. The same reader serves OpenAI itself and Moonshot, which
    /// speak this shape natively; <paramref name="kind"/> is what the entries are tagged with.
    /// </summary>
    public static async Task<List> BuildOpenAiCompatibleAsync(HttpClient http, string baseUrl, SecretValue apiKey, CancellationToken ct, string kind = OpenAiCompatible)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var doc = await GetJsonAsync(http, $"{baseUrl}/models", Bearer(apiKey), ct)
            ?? throw new InvalidOperationException("could not list the endpoint's models");
        var list = new List();
        foreach (var m in doc["data"]?.AsArray() ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            long? created = m?["created"] is JsonValue cv && cv.TryGetValue<long>(out var epoch) ? epoch : null;
            var arch = m?["architecture"] is JsonObject a ? ReadArchitecture(a) : null;
            list.Data.Add(new Entry
            {
                Id = id,
                Created = created,
                OwnedBy = kind,
                Name = m?["name"]?.GetValue<string>() ?? id,
                Description = m?["description"]?.GetValue<string>(),
                Architecture = arch ?? Modality(id, id, null),
                Pks = new PksFacts
                {
                    Provider = kind,
                    Enabled = true,
                    Status = "available",
                    Model = new BaseModel { Name = id },
                    Limit = m?["context_length"] is JsonValue cl && cl.TryGetValue<long>(out var ctx) ? new Limits { Context = ctx } : null,
                },
            });
        }
        return list;
    }

    // ---------- anthropic ----------

    /// <summary>
    /// <c>GET /v1/models</c> at the Claude API, keyed with <c>x-api-key</c> and the required
    /// <c>anthropic-version</c>, following <c>has_more</c>/<c>last_id</c> pages. Claude models take
    /// text and images; the listing carries their token limits where it states them.
    /// </summary>
    public static async Task<List> BuildAnthropicAsync(HttpClient http, SecretValue apiKey, CancellationToken ct, string baseUrl = AnthropicBaseUrl)
    {
        var list = new List();
        string? after = null;
        do
        {
            var url = $"{baseUrl.TrimEnd('/')}/v1/models?limit=100" + (after is null ? "" : $"&after_id={Uri.EscapeDataString(after)}");
            var doc = await GetJsonAsync(http, url, r =>
            {
                r.Headers.TryAddWithoutValidation("x-api-key", apiKey.Reveal() ?? "");
                r.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            }, ct) ?? throw new InvalidOperationException("could not list the Claude models");
            foreach (var m in doc["data"]?.AsArray() ?? [])
            {
                var id = m?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                long? created = m?["created_at"]?.GetValue<string>() is { } iso
                    && DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                    ? at.ToUnixTimeSeconds() : null;
                long? ctx = m?["max_input_tokens"] is JsonValue mi && mi.TryGetValue<long>(out var mInput) ? mInput : null;
                long? output = m?["max_tokens"] is JsonValue mo && mo.TryGetValue<long>(out var mOut) ? mOut : null;
                list.Data.Add(new Entry
                {
                    Id = id,
                    Created = created,
                    OwnedBy = Anthropic,
                    Name = m?["display_name"]?.GetValue<string>() ?? id,
                    Architecture = Build(["text", "image"], ["text"]),
                    Pks = new PksFacts
                    {
                        Provider = Anthropic,
                        Enabled = true,
                        Status = "available",
                        Model = new BaseModel { Name = id },
                        Limit = ctx is null && output is null ? null : new Limits { Context = ctx, Output = output },
                    },
                });
            }
            after = doc["has_more"]?.GetValue<bool>() == true ? doc["last_id"]?.GetValue<string>() : null;
        } while (!string.IsNullOrEmpty(after));
        return list;
    }

    // ---------- typesafe ----------

    /// <summary>
    /// <c>GET /v1/models</c> at TypeSafe (jev). These models judge — <c>/v1/systemone</c> scores a
    /// request against choices — so they are listed as <c>text->choice</c> and are not routable
    /// through the chat route; a tool that wants a verdict still posts systemone to its bound provider.
    /// </summary>
    public static async Task<List> BuildTypeSafeAsync(HttpClient http, string baseUrl, SecretValue apiKey, CancellationToken ct)
    {
        var doc = await GetJsonAsync(http, $"{baseUrl.TrimEnd('/')}/v1/models", Bearer(apiKey), ct)
            ?? throw new InvalidOperationException("could not list the TypeSafe models");
        var items = doc is JsonArray arr ? arr : doc["models"]?.AsArray() ?? doc["data"]?.AsArray() ?? [];
        var list = new List();
        foreach (var m in items)
        {
            string? id = m is JsonValue v && v.TryGetValue<string>(out var s) ? s
                : m?["name"]?.GetValue<string>() ?? m?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            list.Data.Add(new Entry
            {
                Id = id,
                OwnedBy = TypeSafe,
                Name = id,
                Description = m is JsonObject ? m["description"]?.GetValue<string>() : null,
                Architecture = Build(["text"], ["choice"]),
                Pks = new PksFacts
                {
                    Provider = TypeSafe,
                    Enabled = true,
                    Status = "available",
                    Model = new BaseModel { Name = id, Version = m is JsonObject ? m["release_date"]?.GetValue<string>() : null },
                    Capabilities = new Dictionary<string, bool> { ["systemone"] = true, ["chat_completion"] = false },
                },
            });
        }
        return list;
    }

    // ---------- gemini ----------

    /// <summary>
    /// <c>GET /v1beta/models</c> at the Gemini API, keyed with <c>x-goog-api-key</c> so the key
    /// never travels in a URL. Gemini names what each model can do
    /// (<c>supportedGenerationMethods</c>) and its token limits, both carried through.
    /// </summary>
    public static async Task<List> BuildGeminiAsync(HttpClient http, SecretValue apiKey, CancellationToken ct, string baseUrl = GeminiBaseUrl)
    {
        var list = new List();
        string? pageToken = null;
        do
        {
            var url = $"{baseUrl.TrimEnd('/')}/models?pageSize=1000" + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var doc = await GetJsonAsync(http, url, r => r.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Reveal() ?? ""), ct)
                ?? throw new InvalidOperationException("could not list the Gemini models");
            foreach (var m in doc["models"]?.AsArray() ?? [])
            {
                var name = m?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) continue;
                var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                var methods = (m?["supportedGenerationMethods"]?.AsArray() ?? [])
                    .Select(x => x?.GetValue<string>() ?? "").Where(x => x != "").ToArray();
                var caps = methods.ToDictionary(x => x, _ => true);
                list.Data.Add(new Entry
                {
                    Id = id,
                    OwnedBy = Gemini,
                    Name = m?["displayName"]?.GetValue<string>() ?? id,
                    Description = m?["description"]?.GetValue<string>(),
                    Architecture = GeminiModality(id, methods),
                    Pks = new PksFacts
                    {
                        Provider = Gemini,
                        Enabled = true,
                        Status = "available",
                        Model = new BaseModel { Name = id, Version = m?["version"]?.GetValue<string>() },
                        Capabilities = caps.Count == 0 ? null : caps,
                        Limit = m?["inputTokenLimit"] is JsonValue il && il.TryGetValue<long>(out var input)
                            ? new Limits
                            {
                                Context = input,
                                Output = m?["outputTokenLimit"] is JsonValue ol && ol.TryGetValue<long>(out var output) ? output : null,
                            }
                            : null,
                    },
                });
            }
            pageToken = doc["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(pageToken));
        return list;
    }

    // ---------- shared ----------

    private static bool Is(string? a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What goes in and what comes out. A provider's capability flags settle chat and embeddings;
    /// the rest is read from the name, which is the only signal a deployments list carries.
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
        else if (n.Contains("image") || n.Contains("dall-e") || n.Contains("imagen"))
            (inputs, outputs) = (["text", "image"], ["image"]);
        else if (n.Contains("sora") || n.Contains("video") || n.Contains("veo"))
            (inputs, outputs) = (["text", "image"], ["video"]);
        else if (n.Contains("decision"))
            (inputs, outputs) = (["text"], ["choice"]);
        else if (n.Contains("rerank") || n.Contains("moderation"))
            (inputs, outputs) = (["text"], ["score"]);
        else
            (inputs, outputs) = (["text"], ["text"]);
        return Build(inputs, outputs);
    }

    private static Architecture GeminiModality(string id, string[] methods)
    {
        if (methods.Contains("embedContent") || methods.Contains("embedText"))
            return Build(["text"], ["embedding"]);
        if (methods.Contains("predict") || methods.Contains("predictLongRunning"))
            return Modality(id, id, null); // imagen / veo: the name says which
        if (methods.Contains("generateContent") || methods.Contains("generateText"))
        {
            var n = id.ToLowerInvariant();
            if (n.Contains("tts")) return Build(["text"], ["audio"]);
            if (n.Contains("image")) return Build(["text", "image"], ["image", "text"]);
            return Build(["text", "image"], ["text"]); // Gemini chat models take images
        }
        return Modality(id, id, null);
    }

    private static Architecture ReadArchitecture(JsonObject a)
    {
        string[] Read(string key) => (a[key]?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? "").Where(x => x != "").ToArray();
        var inputs = Read("input_modalities");
        var outputs = Read("output_modalities");
        if (inputs.Length == 0 && outputs.Length == 0 && a["modality"]?.GetValue<string>() is { } modality && modality.Contains("->"))
        {
            var parts = modality.Split("->", 2);
            inputs = parts[0].Split('+', StringSplitOptions.RemoveEmptyEntries);
            outputs = parts[1].Split('+', StringSplitOptions.RemoveEmptyEntries);
        }
        if (inputs.Length == 0) inputs = ["text"];
        if (outputs.Length == 0) outputs = ["text"];
        return Build(inputs, outputs);
    }

    private static Architecture Build(string[] inputs, string[] outputs) => new()
    {
        Modality = string.Join("+", inputs) + "->" + string.Join("+", outputs),
        InputModalities = inputs,
        OutputModalities = outputs,
    };

    private static Action<HttpRequestMessage> Bearer(SecretValue token) => request =>
    {
        if (!string.IsNullOrEmpty(token.Reveal()) && !SecretSink.SetBearerToken(request, token))
            throw new InvalidOperationException("no credential for the provider");
    };

    private static async Task<JsonNode?> GetJsonAsync(HttpClient http, string url, Action<HttpRequestMessage> authorize, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        authorize(request);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: HTTP {(int)response.StatusCode}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct);
    }
}
