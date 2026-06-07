using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services.Agents;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agent;

/// <summary>
/// Register the current session as an agent against a configured provider so it
/// shows up as a shareable "person" (e.g. in the Windows Share panel). Generic
/// over <see cref="IAgentProvider"/> — the first provider is <c>share</c>
/// (configured via <c>pks share init</c>); others can register against the same
/// verb later. Enrollment makes the agent appear immediately; it also wires the
/// <c>share-agent</c> MCP server so this session can receive what's shared to it.
/// </summary>
[Description("Register this session as a shareable agent")]
public class AgentRegisterCommand : Command<AgentRegisterCommand.Settings>
{
    private readonly IEnumerable<IAgentProvider> _providers;
    private readonly IAnsiConsole _console;

    public AgentRegisterCommand(IEnumerable<IAgentProvider> providers, IAnsiConsole console)
    {
        _providers = providers;
        _console = console;
    }

    public class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        // Only providers that are actually configured (e.g. `pks share init` was run).
        var configured = new List<IAgentProvider>();
        foreach (var p in _providers)
            if (await p.IsConfiguredAsync()) configured.Add(p);

        if (configured.Count == 0)
        {
            _console.MarkupLine("[red]No agent provider is configured.[/]");
            _console.MarkupLine("[dim]Set one up first, e.g.:[/] [cyan]pks share init[/]");
            return 1;
        }

        var provider = configured.Count == 1
            ? configured[0]
            : configured.First(p => p.Name == _console.Prompt(
                new SelectionPrompt<string>().Title("[cyan]Register against which provider?[/]")
                    .AddChoices(configured.Select(c => c.Name))));

        var defaultName = SuggestName();
        var name = _console.Prompt(new TextPrompt<string>("[cyan]Agent name[/] [dim](how it appears when sharing)[/]:")
            .DefaultValue(defaultName).PromptStyle("cyan"));
        var role = _console.Prompt(new TextPrompt<string>("[cyan]Role[/] [dim](one line, optional)[/]:")
            .AllowEmpty().DefaultValue("coding session"));

        AgentRegistration reg;
        try
        {
            reg = await _console.Status().StartAsync("Enrolling agent…",
                async _ => await provider.RegisterAsync(new AgentIdentity(name, role)));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Registration failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        _console.MarkupLine($"[green]Registered[/] [bold]{Markup.Escape(name)}[/] against [cyan]{provider.Name}[/] " +
                            $"[dim](inbox {Markup.Escape(reg.InboxId)})[/].");

        // Wire the MCP server for THIS session so it can receive shares. Best
        // effort: if `claude` isn't on PATH, print the command for the user.
        WireMcp(reg);

        _console.MarkupLine("[dim]It appears as a contact in the Share panel within ~30s. " +
                            "Shares arrive in this inbox — read them with the agent-share `share.list` loop.[/]");
        return 0;
    }

    private void WireMcp(AgentRegistration reg)
    {
        var args = new[]
        {
            "mcp", "add", "share-agent", "--transport", "http",
            "--header", $"Authorization: Bearer {reg.Token}", reg.McpUrl,
        };
        try
        {
            var psi = new ProcessStartInfo("claude") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("could not start claude");
            proc.WaitForExit(15000);
            if (proc.ExitCode == 0)
            {
                _console.MarkupLine("[green]Wired[/] the [cyan]share-agent[/] MCP server for this session.");
                return;
            }
        }
        catch { /* fall through to manual hint */ }

        _console.MarkupLine("[yellow]Wire MCP manually[/] (claude not found or add failed):");
        _console.MarkupLine($"[dim]claude mcp add share-agent --transport http --header \"Authorization: Bearer {Markup.Escape(reg.Token)}\" {Markup.Escape(reg.McpUrl)}[/]");
    }

    /// <summary>A sensible default name: the git repo / working-dir basename.</summary>
    private static string SuggestName()
    {
        try
        {
            var dir = Directory.GetCurrentDirectory();
            var name = new DirectoryInfo(dir).Name;
            return string.IsNullOrWhiteSpace(name) ? "coding session" : name;
        }
        catch { return "coding session"; }
    }
}
