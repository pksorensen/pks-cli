using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Rough token estimator for the Anthropic <c>/v1/messages/count_tokens</c> endpoint and for
/// the <c>input_tokens</c> seed on <c>message_start</c>. The Responses API has no token-count
/// endpoint, and Claude Code only uses the value for context-window display, so a ~4-chars/token
/// approximation over all serialised text is sufficient.
/// </summary>
public static class TokenEstimator
{
    public static int EstimateInputTokens(JsonElement anthropic)
    {
        var sb = new StringBuilder();

        if (anthropic.TryGetProperty("system", out var system))
        {
            CollectText(system, sb);
        }
        if (anthropic.TryGetProperty("messages", out var messages))
        {
            CollectText(messages, sb);
        }
        if (anthropic.TryGetProperty("tools", out var tools))
        {
            CollectText(tools, sb);
        }

        return Math.Max(1, sb.Length / 4);
    }

    private static void CollectText(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(el.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectText(item, sb);
                break;
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject()) CollectText(prop.Value, sb);
                break;
        }
    }
}
