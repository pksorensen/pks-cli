using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Agent.Foundry;

/// <summary>
/// Shared plumbing for talking to an Azure AI Foundry <b>Responses API</b> deployment: building the
/// canonical responses URL from a stored resource endpoint and applying the right upstream auth
/// (api-key for Codex deployments, bearer for plain GPT-5). Used by both the <c>pks claude codex</c>
/// translating proxy and the <c>pks codex</c> native passthrough so the auth/URL rules live in one place.
/// </summary>
public static class FoundryResponsesEndpoint
{
    /// <summary>Normalises a stored resource endpoint to the v1 Responses path regardless of how it was stored.</summary>
    public static string BuildResponsesUrl(string endpoint)
    {
        var baseUrl = endpoint.TrimEnd('/');
        if (baseUrl.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/responses";
        if (baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/v1/responses";
        return baseUrl + "/openai/v1/responses";
    }

    /// <summary>
    /// Applies upstream auth to a Foundry Responses request. Codex deployments authenticate with an
    /// api-key (Entra ID is not supported for Codex); plain GPT-5 deployments fall back to a bearer token.
    /// The bearer is fetched fresh from the auth service on every call, so long-lived sessions never expire.
    /// </summary>
    public static async Task ApplyUpstreamAuthAsync(
        HttpRequestMessage req,
        FoundryStoredCredentials creds,
        IAzureFoundryAuthService authService,
        string cognitiveScope,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(creds.ApiKey))
        {
            req.Headers.TryAddWithoutValidation("api-key", creds.ApiKey);
            return;
        }

        var token = await authService.GetAccessTokenAsync(cognitiveScope, ct);
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>
    /// Sends a tiny Responses call to validate deployment + auth + endpoint. Returns null on success,
    /// else a short error string suitable for display.
    /// </summary>
    public static async Task<string?> PreflightAsync(
        HttpClient client,
        FoundryStoredCredentials creds,
        IAzureFoundryAuthService authService,
        string cognitiveScope,
        string deployment,
        CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = deployment,
                ["input"] = "ping",
                ["max_output_tokens"] = 16,
                ["stream"] = false,
                ["store"] = false,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildResponsesUrl(creds.SelectedResourceEndpoint))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            await ApplyUpstreamAuthAsync(req, creds, authService, cognitiveScope, ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var resp = await client.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode) return null;

            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return $"HTTP {(int)resp.StatusCode}: {AnthropicProxyUtil.Truncate(text, 1200)}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
