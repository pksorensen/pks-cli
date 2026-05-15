namespace PKS.Infrastructure.Services.Brain;

/// Per-token pricing for a Claude model. Mirrors ModelPricing in ClaudeUsageCommand
/// but shared so the brain ingest can attribute cost per session/project/model
/// without duplicating the LiteLLM cache logic.
public sealed record ModelPricing(
    double InputPerToken,
    double OutputPerToken,
    double CacheCreatePerToken,
    double CacheReadPerToken);

public interface IPricingService
{
    /// Look up pricing for a model name. Result is cached for the lifetime of
    /// the service. Returns null if no pricing data is found.
    Task<ModelPricing?> GetPricingAsync(string model, CancellationToken ct = default);

    double EstimateCost(ModelPricing pricing, long inputTokens, long outputTokens, long cacheRead, long cacheCreate);
}
