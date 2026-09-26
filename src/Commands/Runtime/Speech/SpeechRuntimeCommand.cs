using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Spectre.Console.Cli;

namespace PKS.Commands.Runtime.Speech;

[Description("Run the provider adapter for realtime speech capabilities")]
public sealed class SpeechRuntimeCommand : AsyncCommand<SpeechRuntimeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--port")]
        [Description("HTTP port (default: 8080)")]
        public int Port { get; init; } = 8080;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        => await RunAsync(settings.Port);

    public static async Task<int> RunAsync(int port, CancellationToken cancellationToken = default)
    {
        SpeechRuntimeOptions options;
        try { options = SpeechRuntimeOptions.FromEnvironment(); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"speech runtime configuration error: {error.Message}");
            return 2;
        }

        var serviceToken = Environment.GetEnvironmentVariable("PKS_SPEECH_RUNTIME_TOKEN") ?? "";
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/healthz", () => Results.Json(new { status = "ok", provider = options.Provider }));
        app.MapGet("/v1/capabilities", () => Results.Json(new
        {
            capabilities = new[]
            {
                new
                {
                    id = SpeechProtocol.Capability,
                    transport = "websocket",
                    audio = new { sampleRate = 24000, channels = 1, encoding = "pcm16" },
                },
            },
        }));

        app.Map("/v1/speech/realtime", async http =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                return;
            }
            if (serviceToken.Length > 0 && http.Request.Headers.Authorization != $"Bearer {serviceToken}")
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var client = await http.WebSockets.AcceptWebSocketAsync();
            try
            {
                var startJson = await ReceiveTextAsync(client, http.RequestAborted)
                    ?? throw new InvalidOperationException("Client closed before session.start");
                var session = SpeechProtocol.ParseSessionStart(startJson);
                await using var azure = new AzureVoiceLiveSession(options);
                await azure.ConnectAsync(session, http.RequestAborted);
                long sequence = 0;
                await SendTextAsync(client, SpeechProtocol.ServerEvent("session.started", ++sequence, new
                {
                    capability = SpeechProtocol.Capability,
                    provider = options.Provider,
                }), http.RequestAborted);

                var upstream = PumpAzureAsync(azure, client, () => Interlocked.Increment(ref sequence), http.RequestAborted);
                var downstream = PumpClientAsync(client, azure, http.RequestAborted);
                await Task.WhenAny(upstream, downstream);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (client.State == WebSocketState.Open)
                {
                    var message = SpeechProtocol.ServerEvent("provider.error", 0, new { message = error.Message });
                    await SendTextAsync(client, message, CancellationToken.None);
                }
            }
            finally
            {
                if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None); }
                    catch { }
                }
            }
        });

        Console.WriteLine($"pks speech runtime listening on :{port} ({options.Provider})");
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    private static async Task PumpClientAsync(WebSocket client, AzureVoiceLiveSession azure, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (client.State == WebSocketState.Open && azure.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType == WebSocketMessageType.Binary)
                await azure.SendAudioAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
    }

    private static async Task PumpAzureAsync(
        AzureVoiceLiveSession azure,
        WebSocket client,
        Func<long> nextSequence,
        CancellationToken cancellationToken)
    {
        while (azure.State == WebSocketState.Open && client.State == WebSocketState.Open)
        {
            var providerEvent = await azure.ReceiveTextAsync(cancellationToken);
            if (providerEvent is null) return;
            var translated = AzureVoiceLiveEvents.Translate(providerEvent);
            if (translated is null) continue;
            await SendTextAsync(
                client,
                SpeechProtocol.ServerEvent(translated.Type, nextSequence(), translated.Payload),
                cancellationToken);
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidOperationException("Expected session.start text event");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);
}
