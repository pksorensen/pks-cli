using System.Text;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Chat;
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

    public PersonaScoreRunner(AgentChatProviderFactory providerFactory)
    {
        _providerFactory = providerFactory;
    }

    public sealed class Result
    {
        public bool Ok => Score is not null && Errors.Count == 0;
        public PersonaScore? Score { get; set; }
        public List<PersonaScoreSchema.ValidationError> Errors { get; set; } = new();
        public string RawReply { get; set; } = "";
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

        var text = new StringBuilder();
        await foreach (var ev in provider.StreamAsync(request, deployment, ct))
        {
            if (ev is TextDeltaEvent t) text.Append(t.Text);
            // We don't ask for tools, so ToolUse* / Thinking events shouldn't appear;
            // ignore them defensively if a provider emits them anyway.
        }

        var raw = text.ToString();
        var validation = PersonaScoreSchema.Validate(raw, rubric, persona.Id, modelId);
        return new Result
        {
            Score = validation.Parsed,
            Errors = validation.Errors,
            RawReply = raw,
        };
    }
}
