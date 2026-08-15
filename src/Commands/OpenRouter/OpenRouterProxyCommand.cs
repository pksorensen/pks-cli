using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenRouter;

/// <summary>
/// Starts a local OpenAI-compatible endpoint that accepts a throwaway proxy token and forwards to
/// OpenRouter signed with the registered API key.
///
/// This exists because the stored credential has no read path out of pks — deliberately, and there
/// will not be an export command. A local script that needs OpenRouter therefore does not get the
/// key; it gets a loopback URL and a token that dies with this process:
///
///   export OPENROUTER_PROXY_TOKEN=$(uuidgen) OPENROUTER_PROXY_URL=http://localhost:8787
///   pks openrouter proxy --port 8787 --token "$OPENROUTER_PROXY_TOKEN" &amp;
///   NEMO_BASE_URL=$OPENROUTER_PROXY_URL NEMO_API_KEY=$OPENROUTER_PROXY_TOKEN python3 run_cleanup.py
///
/// The caller supplies port and token because the obvious alternative does not work:
/// <c>eval $(pks openrouter proxy)</c> — the shape <c>pks foundry proxy</c> documents — hangs. The
/// export lines are printed and flushed, but command substitution reads the child's stdout to EOF,
/// and a server that is still serving has not closed it. The exports are still printed for the
/// interactive case; they are just not capturable that way.
/// </summary>
[Description("Start a local OpenAI-compatible proxy that signs requests with the stored OpenRouter key")]
public sealed class OpenRouterProxyCommand : AsyncCommand<OpenRouterProxyCommand.Settings>
{
    private readonly IOpenRouterService _openRouter;
    private readonly IAnsiConsole _console;

    public OpenRouterProxyCommand(IOpenRouterService openRouter, IAnsiConsole console)
    {
        _openRouter = openRouter;
        _console = console;
    }

    public sealed class Settings : OpenRouterSettings
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
        if (!await _openRouter.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No OpenRouter API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks openrouter init[/] first.[/]");
            return 1;
        }

        var port = settings.Port ?? LocalCredentialProxy.FindFreePort();
        var proxyToken = settings.Token ?? Guid.NewGuid().ToString("N");

        // Printed for a human reading the terminal. Not capturable with command substitution —
        // see the class comment for why, and for the pattern that works.
        Console.WriteLine($"export OPENROUTER_PROXY_URL=http://localhost:{port}");
        Console.WriteLine($"export OPENROUTER_PROXY_TOKEN={proxyToken}");
        Console.Out.Flush();

        await LocalCredentialProxy.RunAsync(
            port,
            proxyToken,
            OpenRouterService.BaseUrl,
            async () => (await _openRouter.GetStoredCredentialsAsync())?.ApiKey ?? default,
            "No OpenRouter API key registered — run pks openrouter init");
        return 0;
    }
}
