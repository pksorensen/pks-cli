using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Nvidia;

/// <summary>
/// The NVIDIA NIM twin of <c>pks openrouter proxy</c>: a loopback OpenAI-compatible endpoint that
/// signs upstream calls with the stored NVIDIA key, so a local script never receives it.
///
///   export NVIDIA_PROXY_TOKEN=$(uuidgen) NVIDIA_PROXY_URL=http://localhost:8788
///   pks nvidia proxy --port 8788 --token "$NVIDIA_PROXY_TOKEN" &amp;
///
/// Do not try to capture the exports with <c>eval $(…)</c>; command substitution reads the child's
/// stdout to EOF and a running server has not closed it.
/// </summary>
[Description("Start a local OpenAI-compatible proxy that signs requests with the stored NVIDIA key")]
public sealed class NvidiaProxyCommand : AsyncCommand<NvidiaProxyCommand.Settings>
{
    private readonly INvidiaService _nvidia;
    private readonly IAnsiConsole _console;

    public NvidiaProxyCommand(INvidiaService nvidia, IAnsiConsole console)
    {
        _nvidia = nvidia;
        _console = console;
    }

    public sealed class Settings : NvidiaSettings
    {
        [CommandOption("-p|--port")]
        [Description("Port to listen on (default: random free port)")]
        public int? Port { get; set; }

        [CommandOption("-t|--token")]
        [Description("Proxy token clients must send in the Authorization header (default: random)")]
        public string? Token { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _nvidia.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No NVIDIA API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks nvidia init[/] first.[/]");
            return 1;
        }

        var port = settings.Port ?? LocalCredentialProxy.FindFreePort();
        var proxyToken = settings.Token ?? Guid.NewGuid().ToString("N");

        Console.WriteLine($"export NVIDIA_PROXY_URL=http://localhost:{port}");
        Console.WriteLine($"export NVIDIA_PROXY_TOKEN={proxyToken}");
        Console.Out.Flush();

        await LocalCredentialProxy.RunAsync(
            port,
            proxyToken,
            NvidiaService.BaseUrl,
            async () => (await _nvidia.GetStoredCredentialsAsync())?.ApiKey ?? default,
            "No NVIDIA API key registered — run pks nvidia init");
        return 0;
    }
}
