namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// A model that can back a local <c>pks claude</c> translating-proxy session. The alias the user
/// runs (<c>scaleway</c> / <c>mistral</c> / <c>qwen</c>) selects a subset of this catalog and the
/// proxy uses <see cref="Provider"/> to pick the right translator — so the catalog itself is the
/// model→provider registry (no name-prefix guessing required).
/// </summary>
public sealed record GenerativeModel(
    string Id,
    string Family,
    string Provider,
    string Label,
    bool IsDefault = false,
    // Upstream caps max_completion_tokens per model (Scaleway rejects requests above it). Claude Code
    // asks for a large max_tokens, so the proxy clamps to this. 16384 is the common Scaleway cap.
    int MaxOutputTokens = 16384);

/// <summary>
/// Curated catalog of Scaleway Generative APIs serverless models (OpenAI Chat Completions
/// compatible, base <c>https://api.scaleway.ai/v1</c>). Prices in the label are EUR per 1M tokens
/// (input/output) from Scaleway's supported-models page, shown in the picker to help the user
/// compare against Anthropic. Update this list as Scaleway's catalog changes.
/// </summary>
public static class GenerativeModelCatalog
{
    public const string ScalewayProvider = "scaleway";

    /// <summary>Scaleway base URL; the proxy appends <c>/chat/completions</c>.</summary>
    public const string ScalewayBaseUrl = "https://api.scaleway.ai/v1";

    private static readonly IReadOnlyList<GenerativeModel> _scaleway = new[]
    {
        // Mistral / Devstral family
        new GenerativeModel("devstral-2-123b-instruct-2512", "mistral", ScalewayProvider,
            "devstral-2-123b — coding specialist (€0.40/€2.00)", IsDefault: true),
        new GenerativeModel("mistral-large-3-675b-instruct-2512", "mistral", ScalewayProvider,
            "mistral-large-3-675b — frontier agentic (Opus-class)"),
        new GenerativeModel("mistral-medium-3.5-128b", "mistral", ScalewayProvider,
            "mistral-medium-3.5-128b — reasoning+coding (€1.50/€7.50)"),
        new GenerativeModel("mistral-small-3.2-24b-instruct-2506", "mistral", ScalewayProvider,
            "mistral-small-3.2-24b — cheap tool-calling (€0.15/€0.35)", MaxOutputTokens: 32768),

        // Qwen family
        new GenerativeModel("qwen3.5-397b-a17b", "qwen", ScalewayProvider,
            "qwen3.5-397b-a17b — frontier reasoning (€0.60/€3.60)"),
        new GenerativeModel("qwen3.5-122b-a10b", "qwen", ScalewayProvider,
            "qwen3.5-122b-a10b — agentic+coding MoE"),
        new GenerativeModel("qwen3-235b-a22b-instruct-2507", "qwen", ScalewayProvider,
            "qwen3-235b-a22b — text+reasoning (€0.75/€2.25)"),
        new GenerativeModel("qwen3-coder-30b-a3b-instruct", "qwen", ScalewayProvider,
            "qwen3-coder-30b — pure coding (€0.20/€0.80)", MaxOutputTokens: 32768),

        // Llama family
        new GenerativeModel("llama-3.3-70b-instruct", "llama", ScalewayProvider,
            "llama-3.3-70b — general (€0.90/€0.90)"),

        // OpenAI open-weight
        new GenerativeModel("gpt-oss-120b", "gpt-oss", ScalewayProvider,
            "gpt-oss-120b — reasoning, 128k", MaxOutputTokens: 32768),
    };

    /// <summary>All Scaleway models.</summary>
    public static IReadOnlyList<GenerativeModel> Scaleway => _scaleway;

    /// <summary>Every model served by the given provider (e.g. <c>scaleway</c>).</summary>
    public static IReadOnlyList<GenerativeModel> ByProvider(string provider) =>
        _scaleway.Where(m => string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Every model in the given family (e.g. <c>mistral</c>, <c>qwen</c>).</summary>
    public static IReadOnlyList<GenerativeModel> ByFamily(string family) =>
        _scaleway.Where(m => string.Equals(m.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The default model in a subset (the one flagged <see cref="GenerativeModel.IsDefault"/>, else the first).</summary>
    public static GenerativeModel? DefaultIn(IEnumerable<GenerativeModel> models)
    {
        var list = models.ToList();
        return list.FirstOrDefault(m => m.IsDefault) ?? list.FirstOrDefault();
    }
}
