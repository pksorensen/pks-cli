using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PKS.Infrastructure.Services.Agent;
using PKS.Infrastructure.Services.Agent.Chat;
using PKS.Infrastructure.Services.Agent.Tools;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent;

public class AgentLoopTests
{
    private static AgentToolRegistry RegistryWith(params IAgentTool[] tools) => new(tools);

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RunAsync_ModelStopsImmediately_ReturnsZero()
    {
        var provider = new ScriptedChatProvider(turns: new[]
        {
            new ScriptedTurn(
                Events: new ChatStreamEvent[]
                {
                    new TextDeltaEvent("hello"),
                    new MessageStopEvent(ChatFinishReason.Stop, Usage: null),
                }),
        });

        var loop = new AgentLoop(
            provider,
            modelId: "test-model",
            tools: RegistryWith(),
            renderer: new NullAgentLoopRenderer(),
            logger: NullLogger<AgentLoop>.Instance,
            maxTurns: 3);

        var exit = await loop.RunAsync(systemPrompt: "be nice", userPrompt: "hi", CancellationToken.None);

        exit.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RunAsync_ToolCallThenStop_ExecutesToolAndAppendsResult()
    {
        var fakeTool = new FakeTool(name: "ping", reply: "pong");

        var provider = new ScriptedChatProvider(turns: new[]
        {
            // Turn 1: model asks to call ping
            new ScriptedTurn(
                Events: new ChatStreamEvent[]
                {
                    new ToolUseStartEvent("call_1", "ping"),
                    new ToolUseDeltaEvent("call_1", "{}"),
                    new MessageStopEvent(ChatFinishReason.ToolCalls, Usage: null),
                }),
            // Turn 2: model says final answer + stops
            new ScriptedTurn(
                Events: new ChatStreamEvent[]
                {
                    new TextDeltaEvent("done"),
                    new MessageStopEvent(ChatFinishReason.Stop, Usage: null),
                }),
        });

        var loop = new AgentLoop(
            provider,
            modelId: "test-model",
            tools: RegistryWith(fakeTool),
            renderer: new NullAgentLoopRenderer(),
            logger: NullLogger<AgentLoop>.Instance,
            maxTurns: 5);

        var exit = await loop.RunAsync("sys", "go", CancellationToken.None);

        exit.Should().Be(0);
        fakeTool.InvocationCount.Should().Be(1, "the model requested exactly one tool call");

        // After the second turn the model's stream is replayed against the request
        // that prompted it. That request contains: user prompt + first-turn assistant
        // (tool_use) + tool result. LastRequest is captured on the SECOND turn's call,
        // so we expect 3 messages there.
        provider.LastRequest.Should().NotBeNull();
        provider.LastRequest!.Messages.Should().HaveCount(3);
        provider.LastRequest.Messages[2].Role.Should().Be(ChatRole.Tool);
        var resultBlock = provider.LastRequest.Messages[2].Content
            .OfType<ToolResultBlock>().Single();
        resultBlock.ToolUseId.Should().Be("call_1");
        resultBlock.Content.Should().Be("pong");
        resultBlock.IsError.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RunAsync_MaxTurnsExceeded_ReturnsTwo()
    {
        // Always asks for a tool call → never stops.
        var fakeTool = new FakeTool("nop", "ok");
        var provider = new ScriptedChatProvider(turns: Enumerable.Repeat(
            new ScriptedTurn(new ChatStreamEvent[]
            {
                new ToolUseStartEvent("call_X", "nop"),
                new MessageStopEvent(ChatFinishReason.ToolCalls, null),
            }),
            count: 10).ToArray());

        var loop = new AgentLoop(
            provider,
            modelId: "test-model",
            tools: RegistryWith(fakeTool),
            renderer: new NullAgentLoopRenderer(),
            logger: NullLogger<AgentLoop>.Instance,
            maxTurns: 2);

        var exit = await loop.RunAsync("sys", "go", CancellationToken.None);
        exit.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RunAsync_UnknownTool_ReturnsErrorResultButContinues()
    {
        var provider = new ScriptedChatProvider(turns: new[]
        {
            new ScriptedTurn(new ChatStreamEvent[]
            {
                new ToolUseStartEvent("call_1", "does_not_exist"),
                new MessageStopEvent(ChatFinishReason.ToolCalls, null),
            }),
            new ScriptedTurn(new ChatStreamEvent[]
            {
                new TextDeltaEvent("recovered"),
                new MessageStopEvent(ChatFinishReason.Stop, null),
            }),
        });

        var loop = new AgentLoop(
            provider,
            modelId: "test-model",
            tools: RegistryWith(),
            renderer: new NullAgentLoopRenderer(),
            logger: NullLogger<AgentLoop>.Instance,
            maxTurns: 5);

        var exit = await loop.RunAsync("sys", "go", CancellationToken.None);
        exit.Should().Be(0);

        var errResult = provider.LastRequest!.Messages[2].Content
            .OfType<ToolResultBlock>().Single();
        errResult.IsError.Should().BeTrue();
        errResult.Content.Should().Contain("unknown tool");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task RunAsync_FinishReasonMaxTokens_ReturnsThree()
    {
        var provider = new ScriptedChatProvider(turns: new[]
        {
            new ScriptedTurn(new ChatStreamEvent[]
            {
                new TextDeltaEvent("truncated"),
                new MessageStopEvent(ChatFinishReason.MaxTokens, null),
            }),
        });

        var loop = new AgentLoop(
            provider,
            modelId: "test-model",
            tools: RegistryWith(),
            renderer: new NullAgentLoopRenderer(),
            logger: NullLogger<AgentLoop>.Instance);

        var exit = await loop.RunAsync("sys", "go", CancellationToken.None);
        exit.Should().Be(3);
    }

    // ----- Test doubles -----

    private sealed record ScriptedTurn(IReadOnlyList<ChatStreamEvent> Events);

    private sealed class ScriptedChatProvider : IChatProvider
    {
        private readonly Queue<ScriptedTurn> _turns;

        public ScriptedChatProvider(IReadOnlyList<ScriptedTurn> turns)
        {
            _turns = new Queue<ScriptedTurn>(turns);
        }

        public string ProviderId => "scripted";

        public ChatRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            string modelId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (!_turns.TryDequeue(out var turn))
            {
                // Default to a clean stop if the test under-supplies turns.
                yield return new MessageStopEvent(ChatFinishReason.Stop, null);
                yield break;
            }
            foreach (var ev in turn.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return ev;
            }
        }
    }

    private sealed class FakeTool : IAgentTool
    {
        private static readonly JsonElement EmptySchema =
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

        private readonly string _reply;

        public FakeTool(string name, string reply)
        {
            Definition = new ChatToolDefinition(name, $"fake {name}", EmptySchema);
            _reply = reply;
        }

        public ChatToolDefinition Definition { get; }
        public int InvocationCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(ToolResult.Success(_reply));
        }
    }
}
