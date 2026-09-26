using System.ComponentModel;
using Spectre.Console.Cli;
using PKS.Commands.Runtime;

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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings) =>
        await RunAsync(settings.Port);

    public static Task<int> RunAsync(int port, CancellationToken cancellationToken = default) =>
        PksRuntimeHost.RunAsync(port, [SpeechProtocol.Capability], cancellationToken);
}
