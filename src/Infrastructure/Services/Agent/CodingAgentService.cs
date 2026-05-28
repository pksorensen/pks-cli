using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Agent.Tools;
using Spectre.Console;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Façade that wires together everything needed to run one <c>pks agent "&lt;prompt&gt;"</c> invocation.
/// </summary>
public sealed class CodingAgentService
{
    private readonly AgentChatProviderFactory _providerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CodingAgentService> _logger;

    public CodingAgentService(
        AgentChatProviderFactory providerFactory,
        ILoggerFactory loggerFactory)
    {
        _providerFactory = providerFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CodingAgentService>();
    }

    public async Task<int> RunAsync(CodingAgentRunOptions options, CancellationToken cancellationToken)
    {
        var cwd = Path.GetFullPath(options.Cwd ?? Environment.CurrentDirectory);

        // 1. Resolve model → (provider, deployment).
        var (provider, deployment) = await _providerFactory.ResolveAsync(options.ModelId, cancellationToken);
        _logger.LogDebug("Using provider {Provider} deployment {Deployment}", provider.ProviderId, deployment);

        // 2. Build tool registry.
        var allTools = new IAgentTool[]
        {
            new ReadTool(cwd),
            new WriteTool(cwd),
            new EditTool(cwd),
            new BashTool(cwd),
            new GrepTool(cwd),
            new FindTool(cwd),
            new LsTool(cwd),
        };
        var registry = new AgentToolRegistry(allTools);
        if (options.AllowedTools is { Count: > 0 })
        {
            registry = registry.FilterTo(options.AllowedTools);
        }
        else if (options.ReadOnly)
        {
            registry = registry.FilterTo(new[] { "read", "grep", "find", "ls" });
        }

        // 3. Build system prompt (skill file replaces default body if given).
        string? skillBody = null;
        if (!string.IsNullOrWhiteSpace(options.SkillName))
        {
            var skillPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pks-cli", "agent-skills", options.SkillName + ".md");
            if (!File.Exists(skillPath))
            {
                AnsiConsole.MarkupLine($"[red]skill not found: {skillPath}[/]");
                return 1;
            }
            skillBody = await File.ReadAllTextAsync(skillPath, cancellationToken);
        }

        var systemPrompt = SystemPromptBuilder.Build(
            cwd: cwd,
            toolNames: registry.All.Select(t => t.Definition.Name).ToList(),
            customPrompt: skillBody,
            contextFiles: SystemPromptBuilder.LoadContextFiles(cwd));

        // 4. Build the loop and run.
        var loop = new AgentLoop(
            provider: provider,
            modelId: deployment,
            tools: registry,
            renderer: new SpectreAgentLoopRenderer(),
            logger: _loggerFactory.CreateLogger<AgentLoop>(),
            maxTurns: options.MaxTurns);

        return await loop.RunAsync(systemPrompt, options.Prompt, cancellationToken);
    }
}

public sealed record CodingAgentRunOptions(
    string Prompt,
    string ModelId,
    string? Cwd = null,
    string? SkillName = null,
    int MaxTurns = 50,
    bool ReadOnly = false,
    IReadOnlyList<string>? AllowedTools = null);

/// <summary>
/// Renderer that writes the agent's output to <see cref="AnsiConsole"/>.
/// </summary>
internal sealed class SpectreAgentLoopRenderer : IAgentLoopRenderer
{
    public void RenderTextDelta(string text)
    {
        // Spectre's static `AnsiConsole.Write(string)` forwards to the
        // format-string overload internally, which crashes on bare `{` and `}`
        // (e.g. when the model streams JSON tokens or schema fences). Render
        // via the IAnsiConsole instance instead — that path treats the input
        // as literal text, no format parsing.
        AnsiConsole.Console.Write(text);
    }

    public void RenderToolCall(string toolName, JsonElement arguments, bool isError)
    {
        var argSummary = arguments.GetRawText();
        if (argSummary.Length > 200) argSummary = argSummary[..200] + "…";
        AnsiConsole.MarkupLine($"\n[dim cyan]› {Markup.Escape(toolName)}({Markup.Escape(argSummary)})[/]");
    }

    public void RenderToolResult(string toolName, string output, bool isError)
    {
        var snippet = output.Length > 400 ? output[..400] + "…" : output;
        var color = isError ? "red" : "dim";
        AnsiConsole.MarkupLine($"[{color}]{Markup.Escape(snippet)}[/]");
    }

    public void RenderError(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    }
}
