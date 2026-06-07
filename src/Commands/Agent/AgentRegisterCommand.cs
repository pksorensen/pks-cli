using System.ComponentModel;
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

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Agent name as it appears when sharing (prompted if omitted in an interactive terminal)")]
        public string? Name { get; set; }

        [CommandOption("-r|--role")]
        [Description("One-line role/description")]
        public string? Role { get; set; }

        [CommandOption("-p|--provider")]
        [Description("Provider to register against (default: the sole configured one)")]
        public string? Provider { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // Agents invoke this non-interactively, so accept name/role/provider as
        // args and only prompt when they're missing AND we have a real terminal.
        var interactive = !Console.IsInputRedirected;
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

        IAgentProvider provider;
        if (!string.IsNullOrWhiteSpace(settings.Provider))
        {
            var match = configured.FirstOrDefault(p => p.Name.Equals(settings.Provider, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"[red]Provider '{Markup.Escape(settings.Provider)}' is not configured.[/]");
                return 1;
            }
            provider = match;
        }
        else if (configured.Count == 1 || !interactive)
        {
            provider = configured[0];
        }
        else
        {
            var pick = _console.Prompt(new SelectionPrompt<string>()
                .Title("[cyan]Register against which provider?[/]")
                .AddChoices(configured.Select(c => c.Name)));
            provider = configured.First(p => p.Name == pick);
        }

        var name = settings.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = interactive
                ? _console.Prompt(new TextPrompt<string>("[cyan]Agent name[/] [dim](how it appears when sharing)[/]:")
                    .DefaultValue(SuggestName()).PromptStyle("cyan"))
                : SuggestName();

        var role = settings.Role;
        if (string.IsNullOrWhiteSpace(role))
            role = interactive
                ? _console.Prompt(new TextPrompt<string>("[cyan]Role[/] [dim](one line, optional)[/]:")
                    .AllowEmpty().DefaultValue("coding session"))
                : "coding session";

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

        // Intentionally does NOT run `claude mcp add` — that scatters a new MCP
        // server per registration. The agent-share MCP server is added ONCE
        // (agent-share install → one .mcp.json entry); sessions register through
        // its `register` tool. This CLI path just enrolls the inbox.
        _console.MarkupLine("[dim]Appears in the Share panel within ~30s. Wire the inbox once with[/] [cyan]agent-share install[/][dim]; sessions self-register via the share `register` tool — no per-session MCP servers.[/]");
        return 0;
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
