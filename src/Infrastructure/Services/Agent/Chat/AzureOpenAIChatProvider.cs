using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using NeutralChat = PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent.Chat;

public sealed class AzureOpenAIChatProvider : IChatProvider
{
    private readonly AzureOpenAIClient _client;

    public AzureOpenAIChatProvider(Uri endpoint, AzureKeyCredential credential)
    {
        _client = new AzureOpenAIClient(endpoint, credential);
    }

    public AzureOpenAIChatProvider(Uri endpoint, global::Azure.Core.TokenCredential credential)
    {
        _client = new AzureOpenAIClient(endpoint, credential);
    }

    public string ProviderId => "azure-openai";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatClient = _client.GetChatClient(modelId);

        var messages = ConvertMessages(request.Messages, request.SystemPrompt);
        var tools = ConvertTools(request.Tools);

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = request.MaxOutputTokens,
        };
        foreach (var tool in tools)
        {
            options.Tools.Add(tool);
        }

#pragma warning disable AOAI001
        options.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

        var pending = new Dictionary<int, (string Id, string Name, StringBuilder ArgsJson)>();

        await foreach (var update in chatClient.CompleteChatStreamingAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            if (update.ContentUpdate != null)
            {
                foreach (var part in update.ContentUpdate)
                {
                    var text = part.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new TextDeltaEvent(text);
                    }
                }
            }

            if (update.ToolCallUpdates != null)
            {
                foreach (var toolUpdate in update.ToolCallUpdates)
                {
                    var idx = toolUpdate.Index;
                    if (!pending.TryGetValue(idx, out var entry))
                    {
                        entry = (toolUpdate.ToolCallId ?? string.Empty, toolUpdate.FunctionName ?? string.Empty, new StringBuilder());
                        pending[idx] = entry;
                        if (!string.IsNullOrEmpty(toolUpdate.ToolCallId))
                        {
                            yield return new ToolUseStartEvent(toolUpdate.ToolCallId!, toolUpdate.FunctionName ?? string.Empty);
                        }
                    }
                    else if (string.IsNullOrEmpty(entry.Id) && !string.IsNullOrEmpty(toolUpdate.ToolCallId))
                    {
                        entry = (toolUpdate.ToolCallId!, toolUpdate.FunctionName ?? entry.Name, entry.ArgsJson);
                        pending[idx] = entry;
                        yield return new ToolUseStartEvent(entry.Id, entry.Name);
                    }

                    if (toolUpdate.FunctionArgumentsUpdate != null
                        && toolUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
                    {
                        var delta = toolUpdate.FunctionArgumentsUpdate.ToString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            entry.ArgsJson.Append(delta);
                            pending[idx] = entry;
                            if (!string.IsNullOrEmpty(entry.Id))
                            {
                                yield return new ToolUseDeltaEvent(entry.Id, delta);
                            }
                        }
                    }
                }
            }

            if (update.FinishReason.HasValue)
            {
                var reason = MapFinishReason(update.FinishReason.Value);
                ChatUsage? usage = null;
                if (update.Usage != null)
                {
                    usage = new ChatUsage(update.Usage.InputTokenCount, update.Usage.OutputTokenCount);
                }
                yield return new MessageStopEvent(reason, usage);
            }
        }
    }

    private static NeutralChat.ChatFinishReason MapFinishReason(OpenAI.Chat.ChatFinishReason reason) => reason switch
    {
        OpenAI.Chat.ChatFinishReason.Stop => NeutralChat.ChatFinishReason.Stop,
        OpenAI.Chat.ChatFinishReason.ToolCalls => NeutralChat.ChatFinishReason.ToolCalls,
        OpenAI.Chat.ChatFinishReason.Length => NeutralChat.ChatFinishReason.MaxTokens,
        OpenAI.Chat.ChatFinishReason.ContentFilter => NeutralChat.ChatFinishReason.ContentFilter,
        _ => NeutralChat.ChatFinishReason.Stop,
    };

    internal static List<OpenAI.Chat.ChatMessage> ConvertMessages(
        IReadOnlyList<NeutralChat.ChatMessage> messages,
        string systemPrompt)
    {
        var result = new List<OpenAI.Chat.ChatMessage>();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            result.Add(new SystemChatMessage(systemPrompt));
        }

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case ChatRole.System:
                {
                    var text = string.Concat(msg.Content.OfType<TextBlock>().Select(t => t.Text));
                    result.Add(new SystemChatMessage(text));
                    break;
                }
                case ChatRole.User:
                {
                    var text = string.Concat(msg.Content.OfType<TextBlock>().Select(t => t.Text));
                    result.Add(new UserChatMessage(text));
                    break;
                }
                case ChatRole.Assistant:
                {
                    var text = string.Concat(msg.Content.OfType<TextBlock>().Select(t => t.Text));
                    var toolUses = msg.Content.OfType<ToolUseBlock>().ToList();
                    var toolCalls = toolUses.Select(tu =>
                        ChatToolCall.CreateFunctionToolCall(
                            tu.Id,
                            tu.Name,
                            BinaryData.FromString(tu.Arguments.GetRawText()))).ToList();

                    AssistantChatMessage assistant;
                    if (toolCalls.Count > 0)
                    {
                        assistant = new AssistantChatMessage(toolCalls);
                        if (!string.IsNullOrEmpty(text))
                        {
                            assistant.Content.Add(ChatMessageContentPart.CreateTextPart(text));
                        }
                    }
                    else
                    {
                        assistant = new AssistantChatMessage(text);
                    }
                    result.Add(assistant);
                    break;
                }
                case ChatRole.Tool:
                {
                    foreach (var trb in msg.Content.OfType<ToolResultBlock>())
                    {
                        result.Add(new ToolChatMessage(trb.ToolUseId, trb.Content));
                    }
                    break;
                }
            }
        }

        return result;
    }

    internal static List<ChatTool> ConvertTools(IReadOnlyList<ChatToolDefinition> tools)
    {
        var result = new List<ChatTool>(tools.Count);
        foreach (var t in tools)
        {
            result.Add(ChatTool.CreateFunctionTool(
                t.Name,
                t.Description,
                BinaryData.FromString(t.InputSchema.GetRawText())));
        }
        return result;
    }
}
