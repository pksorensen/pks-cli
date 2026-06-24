using System.Diagnostics;
using System.Net.Http;
using System.Text;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// In-process extract runner (the `--agent pks` backend). Instead of shelling out to the
/// `claude` binary, it drives pks-cli's own chat stack (<see cref="AgentChatProviderFactory"/>
/// + <see cref="IChatProvider"/>) with a single system-prompt + user-message turn and no tools.
/// With <c>--foundry</c> it talks to Azure AI Foundry's Claude route via the stored
/// Foundry refresh-token (Entra bearer), so a full extract run bills against Azure quota
/// rather than the per-token Anthropic plan.
/// </summary>
public sealed class PksAgentRunner : IClaudeRunner
{
    private const int MaxOutputTokens = 8192;

    private readonly AgentChatProviderFactory _factory;
    private readonly IPricingService _pricing;
    private readonly HttpClient _http;
    private readonly IAzureFoundryAuthService? _foundryAuth;

    public PksAgentRunner(
        AgentChatProviderFactory factory,
        IPricingService pricing,
        HttpClient http,
        IAzureFoundryAuthService? foundryAuth = null)
    {
        _factory = factory;
        _pricing = pricing;
        _http = http;
        _foundryAuth = foundryAuth;
    }

    public async Task<ClaudeRunResult> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        IChatProvider provider;
        string deployment;
        try
        {
            (provider, deployment) = request.UseFoundry
                ? await BuildFoundryAsync(request.Model, ct)
                : await _factory.ResolveAsync(MapAlias(request.Model) ?? "claude-sonnet-4-6", ct);
        }
        catch (Exception ex)
        {
            return Fail("resolve", ex.Message, sw.Elapsed);
        }

        var chat = new ChatRequest(
            Messages: new[] { ChatMessage.User(request.UserPrompt) },
            SystemPrompt: request.SystemPrompt,
            Tools: Array.Empty<ChatToolDefinition>(),
            MaxOutputTokens: MaxOutputTokens);

        var text = new StringBuilder();
        ChatUsage? usage = null;
        ChatFinishReason finish = ChatFinishReason.Stop;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(request.Timeout);
            await foreach (var ev in provider.StreamAsync(chat, deployment, timeoutCts.Token))
            {
                switch (ev)
                {
                    case TextDeltaEvent t: text.Append(t.Text); break;
                    case MessageStopEvent stop: usage = stop.Usage; finish = stop.FinishReason; break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("timeout", $"timed out after {request.Timeout.TotalSeconds:0.#}s", sw.Elapsed);
        }
        catch (Exception ex)
        {
            return Fail("exit", ex.Message, sw.Elapsed);
        }

        var body = text.ToString().Trim();
        long inTok = usage?.InputTokens ?? 0;
        long outTok = usage?.OutputTokens ?? 0;

        // Foundry usage is metered by Azure (no per-call cost in the response); estimate
        // from tokens × LiteLLM pricing so the meta/UI $ figures stay meaningful.
        double cost = 0;
        var pricing = await _pricing.GetPricingAsync(deployment, ct);
        if (pricing is not null)
            cost = _pricing.EstimateCost(pricing, inTok, outTok, cacheRead: 0, cacheCreate: 0);

        // Soft budget guard (the binary's --max-budget-usd has no in-process equivalent).
        if (request.MaxBudgetUsd is { } cap && cap > 0 && cost > cap)
        {
            return new ClaudeRunResult
            {
                Success = false, ResponseText = string.Empty, RawStdout = body, Stderr = "",
                ExitCode = 0, Duration = sw.Elapsed, Model = deployment,
                InputTokens = inTok, OutputTokens = outTok, CostUsd = cost, ErrorKind = "budget",
            };
        }

        var success = body.Length > 0 && finish != ChatFinishReason.Error;
        return new ClaudeRunResult
        {
            Success = success,
            ResponseText = body,
            RawStdout = body,
            Stderr = "",
            ExitCode = success ? 0 : 1,
            Duration = sw.Elapsed,
            Model = deployment,
            InputTokens = inTok,
            OutputTokens = outTok,
            CacheReadInputTokens = 0,
            CacheCreationInputTokens = 0,
            CostUsd = cost,
            ErrorKind = success ? null : (finish == ChatFinishReason.Error ? "is_error" : "empty"),
        };
    }

    /// Build a Foundry-served Anthropic provider directly from stored Foundry credentials,
    /// resolving a tier alias (haiku/sonnet/opus) or explicit deployment name.
    private async Task<(IChatProvider, string)> BuildFoundryAsync(string? model, CancellationToken ct)
    {
        if (_foundryAuth is null || !await _foundryAuth.IsAuthenticatedAsync())
            throw new InvalidOperationException("--foundry requires a logged-in Foundry session (run `pks foundry`).");
        var creds = await _foundryAuth.GetStoredCredentialsAsync()
            ?? throw new InvalidOperationException("--foundry: no stored Foundry credentials.");

        if (string.IsNullOrWhiteSpace(creds.SelectedResourceEndpoint))
            throw new InvalidOperationException("--foundry: stored credentials have no resource endpoint.");

        // Foundry's Claude route lives at {scheme}://{host}/anthropic (the provider appends /v1/messages).
        var src = new Uri(creds.SelectedResourceEndpoint);
        var endpoint = new Uri($"{src.Scheme}://{src.Host}/anthropic");
        var provider = new AnthropicChatProvider(endpoint, new FoundryTokenCredential(_foundryAuth), _http);

        var deployment = ResolveTierDeployment(creds.EnabledModels, creds.DefaultModel, model);
        return (provider, deployment);
    }

    private static string ResolveTierDeployment(IReadOnlyList<string> enabled, string defaultModel, string? model)
    {
        var m = (model ?? "haiku").Trim();
        if (m is "haiku" or "sonnet" or "opus")
        {
            var match = enabled.FirstOrDefault(d => d.ToLowerInvariant().Contains(m));
            return match ?? (string.IsNullOrWhiteSpace(defaultModel) ? m : defaultModel);
        }
        return m; // explicit deployment id
    }

    /// Map tier aliases to built-in Anthropic-direct model ids for the non-Foundry path.
    private static string? MapAlias(string? model) => model switch
    {
        "sonnet" => "claude-sonnet-4-6",
        "opus" => "claude-opus-4-7",
        _ => model, // "haiku" (no Anthropic-direct built-in) and explicit ids pass through
    };

    private static ClaudeRunResult Fail(string kind, string message, TimeSpan elapsed) => new()
    {
        Success = false,
        ResponseText = string.Empty,
        RawStdout = string.Empty,
        Stderr = message,
        ExitCode = -1,
        Duration = elapsed,
        ErrorKind = kind,
    };
}
