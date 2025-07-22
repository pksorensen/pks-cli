# Background Knowledge

The MCP protocol is explained at https://modelcontextprotocol.io/introduction and the C# SDK is here: https://github.com/modelcontextprotocol/csharp-sdk

Samples: https://github.com/modelcontextprotocol/csharp-sdk/tree/main/samples

# Transports

SSE Transport Sample: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/AspNetCoreSseServer/Program.cs

````
.WithStdioServerTransport() //stdio transport
.WithHttpTransport() //sse transport
.WithHttpTransport(options=>
{
  options.Stateless = true; // Enable stateless mode
}
) //http streamable support
```
For http/sse, we use /sse for server side events and /mcp for httpstreamable
```
var app = builder.Build();

app.MapMcp("/sse" or "/mcp");

```

# ComplexPrompt

Example of doing a ComplexPrompt with multiple arguments/parameters with descriptions

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Prompts/ComplexPromptType.cs

```
using EverythingServer.Tools;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Prompts;

[McpServerPromptType]
public class ComplexPromptType
{
    [McpServerPrompt(Name = "complex_prompt"), Description("A prompt with arguments")]
    public static IEnumerable<ChatMessage> ComplexPrompt(
        [Description("Temperature setting")] int temperature,
        [Description("Output style")] string? style = null)
    {
        return [
            new ChatMessage(ChatRole.User,$"This is a complex prompt with arguments: temperature={temperature}, style={style}"),
            new ChatMessage(ChatRole.Assistant, "I understand. You've provided a complex prompt with temperature and style arguments. How would you like me to proceed?"),
            new ChatMessage(ChatRole.User, [new DataContent(TinyImageTool.MCP_TINY_IMAGE)])
        ];
    }
}
```

# Simple Prompt

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Prompts/SimplePromptType.cs

```
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Prompts;

[McpServerPromptType]
public class SimplePromptType
{
    [McpServerPrompt(Name = "simple_prompt"), Description("A prompt without arguments")]
    public static string SimplePrompt() => "This is a simple prompt without arguments";
}
```

# Simple Resources

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Resources/SimpleResourceType.cs

```
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Resources;

[McpServerResourceType]
public class SimpleResourceType
{
    [McpServerResource(UriTemplate = "test://direct/text/resource", Name = "Direct Text Resource", MimeType = "text/plain")]
    [Description("A direct text resource")]
    public static string DirectTextResource() => "This is a direct resource";

    [McpServerResource(UriTemplate = "test://template/resource/{id}", Name = "Template Resource")]
    [Description("A template resource with a numeric ID")]
    public static ResourceContents TemplateResource(RequestContext<ReadResourceRequestParams> requestContext, int id)
    {
        int index = id - 1;
        if ((uint)index >= ResourceGenerator.Resources.Count)
        {
            throw new NotSupportedException($"Unknown resource: {requestContext.Params?.Uri}");
        }

        var resource = ResourceGenerator.Resources[index];
        return resource.MimeType == "text/plain" ?
            new TextResourceContents
            {
                Text = resource.Description!,
                MimeType = resource.MimeType,
                Uri = resource.Uri,
            } :
            new BlobResourceContents
            {
                Blob = resource.Description!,
                MimeType = resource.MimeType,
                Uri = resource.Uri,
            };
    }
}
```

# Long Running Tool

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Tools/LongRunningTool.cs

```
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Tools;

[McpServerToolType]
public class LongRunningTool
{
    [McpServerTool(Name = "longRunningOperation"), Description("Demonstrates a long running operation with progress updates")]
    public static async Task<string> LongRunningOperation(
        IMcpServer server,
        RequestContext<CallToolRequestParams> context,
        int duration = 10,
        int steps = 5)
    {
        var progressToken = context.Params?.ProgressToken;
        var stepDuration = duration / steps;

        for (int i = 1; i <= steps + 1; i++)
        {
            await Task.Delay(stepDuration * 1000);

            if (progressToken is not null)
            {
                await server.SendNotificationAsync("notifications/progress", new
                    {
                        Progress = i,
                        Total = steps,
                        progressToken
                    });
            }
        }

        return $"Long running operation completed. Duration: {duration} seconds. Steps: {steps}.";
    }
}
```

# Tiny Image Tool

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Tools/TinyImageTool.cs

```
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Tools;

[McpServerToolType]
public class TinyImageTool
{
    [McpServerTool(Name = "getTinyImage"), Description("Get a tiny image from the server")]
    public static IEnumerable<AIContent> GetTinyImage() => [
            new TextContent("This is a tiny image:"),
            new DataContent(MCP_TINY_IMAGE),
            new TextContent("The image above is the MCP tiny image.")
        ];

    internal const string MCP_TINY_IMAGE =
      "data:image/png;base64,...base64 image...";
}
```

# Annotate Message

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Tools/AnnotatedMessageTool.cs

```
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace EverythingServer.Tools;

[McpServerToolType]
public class AnnotatedMessageTool
{
    public enum MessageType
    {
        Error,
        Success,
        Debug,
    }

    [McpServerTool(Name = "annotatedMessage"), Description("Generates an annotated message")]
    public static IEnumerable<ContentBlock> AnnotatedMessage(MessageType messageType, bool includeImage = true)
    {
        List<ContentBlock> contents = messageType switch
        {
            MessageType.Error => [new TextContentBlock
            {
                Text = "Error: Operation failed",
                Annotations = new() { Audience = [Role.User, Role.Assistant], Priority = 1.0f }
            }],
            MessageType.Success => [new TextContentBlock
            {
                Text = "Operation completed successfully",
                Annotations = new() { Audience = [Role.User], Priority = 0.7f }
            }],
            MessageType.Debug => [new TextContentBlock
            {
                Text = "Debug: Cache hit ratio 0.95, latency 150ms",
                Annotations = new() { Audience = [Role.Assistant], Priority = 0.3f }
            }],
            _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null)
        };

        if (includeImage)
        {
            contents.Add(new ImageContentBlock
            {
                Data = TinyImageTool.MCP_TINY_IMAGE.Split(",").Last(),
                MimeType = "image/png",
                Annotations = new() { Audience = [Role.User], Priority = 0.5f }
            });
        }

        return contents;
    }
}

```

# Send Log Message to Client

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/LoggingUpdateMessageSender.cs

```
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace EverythingServer;

public class LoggingUpdateMessageSender(IMcpServer server, Func<LoggingLevel> getMinLevel) : BackgroundService
{
    readonly Dictionary<LoggingLevel, string> _loggingLevelMap = new()
    {
        { LoggingLevel.Debug, "Debug-level message" },
        { LoggingLevel.Info, "Info-level message" },
        { LoggingLevel.Notice, "Notice-level message" },
        { LoggingLevel.Warning, "Warning-level message" },
        { LoggingLevel.Error, "Error-level message" },
        { LoggingLevel.Critical, "Critical-level message" },
        { LoggingLevel.Alert, "Alert-level message" },
        { LoggingLevel.Emergency, "Emergency-level message" }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var newLevel = (LoggingLevel)Random.Shared.Next(_loggingLevelMap.Count);

            var message = new
                {
                    Level = newLevel.ToString().ToLower(),
                    Data = _loggingLevelMap[newLevel],
                };

            if (newLevel > getMinLevel())
            {
                await server.SendNotificationAsync("notifications/message", message, cancellationToken: stoppingToken);
            }

            await Task.Delay(15000, stoppingToken);
        }
    }
}
```

# Sending resource updates

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/SubscriptionMessageSender.cs

```
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;

internal class SubscriptionMessageSender(IMcpServer server, HashSet<string> subscriptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var uri in subscriptions)
            {
                await server.SendNotificationAsync("notifications/resource/updated",
                    new
                    {
                        Uri = uri,
                    }, cancellationToken: stoppingToken);
            }

            await Task.Delay(5000, stoppingToken);
        }
    }
}
```

# Everything MCP Server Example

Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/EverythingServer/Program.cs

```
using EverythingServer;
using EverythingServer.Prompts;
using EverythingServer.Resources;
using EverythingServer.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(consoleLogOptions =>
{
    // Configure all logs to go to stderr
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

HashSet<string> subscriptions = [];
var _minimumLoggingLevel = LoggingLevel.Debug;

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<AddTool>()
    .WithTools<AnnotatedMessageTool>()
    .WithTools<EchoTool>()
    .WithTools<LongRunningTool>()
    .WithTools<PrintEnvTool>()
    .WithTools<SampleLlmTool>()
    .WithTools<TinyImageTool>()
    .WithPrompts<ComplexPromptType>()
    .WithPrompts<SimplePromptType>()
    .WithResources<SimpleResourceType>()
    .WithSubscribeToResourcesHandler(async (ctx, ct) =>
    {
        var uri = ctx.Params?.Uri;

        if (uri is not null)
        {
            subscriptions.Add(uri);

            await ctx.Server.SampleAsync([
                new ChatMessage(ChatRole.System, "You are a helpful test server"),
                new ChatMessage(ChatRole.User, $"Resource {uri}, context: A new subscription was started"),
            ],
            options: new ChatOptions
            {
                MaxOutputTokens = 100,
                Temperature = 0.7f,
            },
            cancellationToken: ct);
        }

        return new EmptyResult();
    })
    .WithUnsubscribeFromResourcesHandler(async (ctx, ct) =>
    {
        var uri = ctx.Params?.Uri;
        if (uri is not null)
        {
            subscriptions.Remove(uri);
        }
        return new EmptyResult();
    })
    .WithCompleteHandler(async (ctx, ct) =>
    {
        var exampleCompletions = new Dictionary<string, IEnumerable<string>>
        {
            { "style", ["casual", "formal", "technical", "friendly"] },
            { "temperature", ["0", "0.5", "0.7", "1.0"] },
            { "resourceId", ["1", "2", "3", "4", "5"] }
        };

        if (ctx.Params is not { } @params)
        {
            throw new NotSupportedException($"Params are required.");
        }

        var @ref = @params.Ref;
        var argument = @params.Argument;

        if (@ref is ResourceTemplateReference rtr)
        {
            var resourceId = rtr.Uri?.Split("/").Last();

            if (resourceId is null)
            {
                return new CompleteResult();
            }

            var values = exampleCompletions["resourceId"].Where(id => id.StartsWith(argument.Value));

            return new CompleteResult
            {
                Completion = new Completion { Values = [.. values], HasMore = false, Total = values.Count() }
            };
        }

        if (@ref is PromptReference pr)
        {
            if (!exampleCompletions.TryGetValue(argument.Name, out IEnumerable<string>? value))
            {
                throw new NotSupportedException($"Unknown argument name: {argument.Name}");
            }

            var values = value.Where(value => value.StartsWith(argument.Value));
            return new CompleteResult
            {
                Completion = new Completion { Values = [.. values], HasMore = false, Total = values.Count() }
            };
        }

        throw new NotSupportedException($"Unknown reference type: {@ref.Type}");
    })
    .WithSetLoggingLevelHandler(async (ctx, ct) =>
    {
        if (ctx.Params?.Level is null)
        {
            throw new McpException("Missing required argument 'level'", McpErrorCode.InvalidParams);
        }

        _minimumLoggingLevel = ctx.Params.Level;

        await ctx.Server.SendNotificationAsync("notifications/message", new
        {
            Level = "debug",
            Logger = "test-server",
            Data = $"Logging level set to {_minimumLoggingLevel}",
        }, cancellationToken: ct);

        return new EmptyResult();
    });

ResourceBuilder resource = ResourceBuilder.CreateDefault().AddService("everything-server");
builder.Services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource("*").AddHttpClientInstrumentation().SetResourceBuilder(resource))
    .WithMetrics(b => b.AddMeter("*").AddHttpClientInstrumentation().SetResourceBuilder(resource))
    .WithLogging(b => b.SetResourceBuilder(resource))
    .UseOtlpExporter();

builder.Services.AddSingleton(subscriptions);
builder.Services.AddHostedService<SubscriptionMessageSender>();
builder.Services.AddHostedService<LoggingUpdateMessageSender>();

builder.Services.AddSingleton<Func<LoggingLevel>>(_ => () => _minimumLoggingLevel);

await builder.Build().RunAsync();
```
````
