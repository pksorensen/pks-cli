using System.ComponentModel;
using System.Net.Http.Headers;
using PKS.Infrastructure.Services.Agents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Share;

public class ShareSettings : CommandSettings { }

/// <summary>
/// Configure the <c>share</c> agent provider: log in to an Agent Share server
/// (share.agentics.dk) via the OIDC loopback flow and store the result in the pks
/// share store. This is the "provider" that <c>pks agent register</c> registers
/// against — run once per host. Loopback PKCE: pks prints a URL, you open it, and
/// the callback returns to a local listener (forwarded by the editor in a
/// devcontainer).
/// </summary>
[Description("Log in to an Agent Share server so agents can register against it")]
public class ShareInitCommand : Command<ShareInitCommand.Settings>
{
    private readonly IShareCredStore _store;
    private readonly OidcLoopback _oidc;
    private readonly IAnsiConsole _console;

    public ShareInitCommand(IShareCredStore store, OidcLoopback oidc, IAnsiConsole console)
    {
        _store = store;
        _oidc = oidc;
        _console = console;
    }

    public class Settings : ShareSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        if (Console.IsInputRedirected)
        {
            _console.MarkupLine("[red]`pks share init` must run in an interactive terminal.[/]");
            return 1;
        }

        var host = _console.Prompt(new TextPrompt<string>("[cyan]Agent Share server[/]:")
            .DefaultValue("https://share.agentics.dk").PromptStyle("cyan")).TrimEnd('/');

        var issuer = _console.Prompt(new TextPrompt<string>("[cyan]OIDC issuer[/]:")
            .DefaultValue("https://login.agentics.dk/realms/agentics").PromptStyle("cyan")).TrimEnd('/');

        var clientId = _console.Prompt(new TextPrompt<string>("[cyan]OIDC client id[/] [dim](public, loopback)[/]:")
            .DefaultValue("agentics-share-desktop").PromptStyle("cyan"));

        OidcTokens tok;
        try
        {
            _console.MarkupLine("[dim]Opening your browser to sign in… if it doesn't open, copy the URL below.[/]");
            tok = await _oidc.LoginAsync(issuer, clientId, "openid profile email offline_access",
                url => _console.MarkupLine($"\n[bold]Sign in:[/] [link]{Markup.Escape(url)}[/]\n"));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Login failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // Sanity-check the token reaches the server's owner-scoped API.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{host}/api/agents");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok.AccessToken);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                _console.MarkupLine($"[yellow]Warning:[/] {host}/api/agents returned {(int)resp.StatusCode} — saved anyway.");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Warning:[/] could not verify against {Markup.Escape(host)}: {Markup.Escape(ex.Message)}");
        }

        var cred = new ShareCred
        {
            Host = host,
            Issuer = issuer,
            ClientId = clientId,
            Sub = tok.Sub,
            DisplayName = string.IsNullOrEmpty(tok.Name) ? tok.Email : tok.Name,
        };
        await _store.SaveAsync(cred, tok.RefreshToken);

        var who = string.IsNullOrEmpty(cred.DisplayName) ? tok.Sub : cred.DisplayName;
        _console.MarkupLine($"[green]Logged in[/] to [bold]{Markup.Escape(host)}[/] as [bold]{Markup.Escape(who)}[/].");
        _console.MarkupLine("[dim]Register a session with:[/] [cyan]pks agent register[/]");
        return 0;
    }
}
