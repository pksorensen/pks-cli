using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>OpenRouter API credentials persisted for local proxy and tool launches.</summary>
public sealed class OpenRouterStoredCredentials
{
    public SecretValue ApiKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What <c>GET /api/v1/key</c> reports about the key that signed the request.
///
/// This is the only endpoint that can tell a key apart from a typo — see
/// <see cref="OpenRouterService.ValidateApiKeyAsync"/> for why <c>/models</c> cannot.
/// <see cref="IsFreeTier"/> is the field worth surfacing: the <c>:free</c> model routes
/// only resolve for an account that qualifies for them.
/// </summary>
public sealed class OpenRouterKeyInfo
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("usage")]
    public double Usage { get; set; }

    /// <summary>Credit ceiling for this key, or null when the key is uncapped.</summary>
    [JsonPropertyName("limit")]
    public double? Limit { get; set; }

    [JsonPropertyName("limit_remaining")]
    public double? LimitRemaining { get; set; }

    [JsonPropertyName("is_free_tier")]
    public bool IsFreeTier { get; set; }
}

internal sealed class OpenRouterKeyResponse
{
    [JsonPropertyName("data")]
    public OpenRouterKeyInfo? Data { get; set; }
}
