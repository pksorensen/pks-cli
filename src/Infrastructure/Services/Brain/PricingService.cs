using System.Net.Http;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

/// Port of ClaudeUsageCommand's pricing logic (LiteLLM cache + hard-coded fallback)
/// into a shared service so the brain can attribute cost without duplicating it.
/// Behavior is intentionally identical: 24-hour disk cache at
/// ~/.claude/pricing_cache.json, network fetch on miss, hard-coded backup table.
public sealed class PricingService : IPricingService
{
    private static readonly Dictionary<string, ModelPricing> HardcodedPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["haiku-4-5"]  = new(8e-7,  4e-6,   1e-6,    8e-8),
        ["sonnet-4-6"] = new(3e-6,  1.5e-5, 3.75e-6, 3e-7),
        ["opus-4-6"]   = new(1.5e-5, 7.5e-5, 1.875e-5, 1.5e-6),
        ["opus-4-7"]   = new(1.5e-5, 7.5e-5, 1.875e-5, 1.5e-6),
    };

    private readonly Dictionary<string, ModelPricing?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JsonElement? _liteLLM;
    private bool _liteLLMLoaded;

    public async Task<ModelPricing?> GetPricingAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(model, out var cached)) return cached;
            if (!_liteLLMLoaded)
            {
                _liteLLM = await LoadLiteLLMPricingAsync(ct);
                _liteLLMLoaded = true;
            }

            ModelPricing? result = null;
            if (_liteLLM is { } doc)
            {
                foreach (var prefix in new[] { "anthropic/", "" })
                {
                    if (doc.TryGetProperty(prefix + model, out var entry))
                    {
                        result = TryParseLiteLLMEntry(entry);
                        if (result != null) break;
                    }
                }
                if (result is null)
                {
                    foreach (var prop in doc.EnumerateObject())
                    {
                        if (prop.Name.Contains(model, StringComparison.OrdinalIgnoreCase))
                        {
                            result = TryParseLiteLLMEntry(prop.Value);
                            if (result != null) break;
                        }
                    }
                }
            }

            result ??= HardcodedPricing
                .Where(kv => model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value)
                .FirstOrDefault();

            _cache[model] = result;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public double EstimateCost(ModelPricing pricing, long input, long output, long cacheRead, long cacheCreate)
    {
        return input  * pricing.InputPerToken
             + output * pricing.OutputPerToken
             + cacheCreate * pricing.CacheCreatePerToken
             + cacheRead   * pricing.CacheReadPerToken;
    }

    // ── private ────────────────────────────────────────────────────────────────

    private static async Task<JsonElement?> LoadLiteLLMPricingAsync(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheFile = Path.Combine(home, ".claude", "pricing_cache.json");

        if (File.Exists(cacheFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)).TotalHours < 24)
        {
            try
            {
                var cached = await File.ReadAllTextAsync(cacheFile, ct);
                return JsonSerializer.Deserialize<JsonElement>(cached);
            }
            catch
            {
                // Fall through to network fetch.
            }
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(
                "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
                ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                await File.WriteAllTextAsync(cacheFile, json, ct);
            }
            catch
            {
                // Best-effort cache; not fatal if the write fails.
            }
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private static ModelPricing? TryParseLiteLLMEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!entry.TryGetProperty("input_cost_per_token", out var inp)) return null;
        if (!entry.TryGetProperty("output_cost_per_token", out var outp)) return null;
        if (!inp.TryGetDouble(out var inputCost)) return null;
        if (!outp.TryGetDouble(out var outputCost)) return null;

        double cacheCreate = 0, cacheRead = 0;
        if (entry.TryGetProperty("cache_creation_input_token_cost", out var cc) && cc.TryGetDouble(out var ccv))
            cacheCreate = ccv;
        if (entry.TryGetProperty("cache_read_input_token_cost", out var cr) && cr.TryGetDouble(out var crv))
            cacheRead = crv;

        return new ModelPricing(inputCost, outputCost, cacheCreate, cacheRead);
    }
}
