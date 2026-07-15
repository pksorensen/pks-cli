using System.Diagnostics;
using System.Text;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Chat;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Persona.Models;

namespace PKS.Infrastructure.Services.Persona;

/// <summary>
/// Composes prompt + agent-streamed completion + schema validation for a
/// single (content, persona, rubric, model) tuple. Kept separate from the
/// commands so <c>score</c> and <c>score-all</c> share one path.
/// </summary>
public sealed class PersonaScoreRunner
{
    private readonly AgentChatProviderFactory _providerFactory;
    private readonly IPricingService _pricing;

    public PersonaScoreRunner(AgentChatProviderFactory providerFactory, IPricingService pricing)
    {
        _providerFactory = providerFactory;
        _pricing = pricing;
    }

    public sealed class Result
    {
        public bool Ok => Score is not null && Errors.Count == 0;
        public PersonaScore? Score { get; set; }
        public List<PersonaScoreSchema.ValidationError> Errors { get; set; } = new();
        public string RawReply { get; set; } = "";

        /// <summary>Token usage the provider reported for this call, if any.</summary>
        public ChatUsage? Usage { get; set; }

        /// <summary>
        /// Estimated cost from <see cref="Usage"/> × LiteLLM pricing for the
        /// resolved deployment. Zero when pricing data isn't found (e.g. an
        /// unmapped Foundry deployment name) — never a hard failure.
        /// </summary>
        public double CostUsd { get; set; }

        public TimeSpan Duration { get; set; }
    }

    public async Task<Result> RunAsync(
        Models.Persona persona,
        Rubric rubric,
        string contentPath,
        string content,
        string modelId,
        int maxOutputTokens = 1500,
        CancellationToken ct = default)
    {
        var bundle = PersonaScorePrompt.Build(new PersonaScorePrompt.Request
        {
            ContentPath = contentPath,
            Content = content,
            Persona = persona,
            Rubric = rubric,
            ModelHint = modelId,
        });

        var (provider, deployment) = await _providerFactory.ResolveAsync(modelId, ct);

        var request = new ChatRequest(
            Messages: new[] { ChatMessage.User(bundle.User) },
            SystemPrompt: bundle.System,
            Tools: Array.Empty<ChatToolDefinition>(),
            MaxOutputTokens: maxOutputTokens);

        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();
        ChatUsage? usage = null;
        await foreach (var ev in provider.StreamAsync(request, deployment, ct))
        {
            switch (ev)
            {
                case TextDeltaEvent t: text.Append(t.Text); break;
                case MessageStopEvent stop: usage = stop.Usage; break;
                // We don't ask for tools, so ToolUse* / Thinking events shouldn't appear;
                // ignore them defensively if a provider emits them anyway.
            }
        }
        sw.Stop();

        // Foundry usage is metered by Azure — no per-call $ in the response —
        // so estimate from tokens × LiteLLM pricing, same as PksAgentRunner.
        double cost = 0;
        var pricing = await _pricing.GetPricingAsync(deployment, ct);
        if (pricing is not null)
            cost = _pricing.EstimateCost(pricing, usage?.InputTokens ?? 0, usage?.OutputTokens ?? 0, cacheRead: 0, cacheCreate: 0);

        var raw = text.ToString();
        var validation = PersonaScoreSchema.Validate(raw, rubric, persona.Id, modelId);
        return new Result
        {
            Usage = usage,
            CostUsd = cost,
            Duration = sw.Elapsed,
            Score = validation.Parsed,
            Errors = validation.Errors,
            RawReply = raw,
        };
    }
}
