using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Actions;

/// <summary>
/// Interactive toggle for which ACTIONS require a two-factor code. Toggling is itself gated by
/// the <c>policy.write</c> action once enrolled, so an agent can't quietly disable the gate.
/// Enforcement is per-action at the shared choke-point, so enabling e.g. <c>vm.start</c> covers
/// every command that starts a VM (direct, status menu, and silent devcontainer auto-start).
/// </summary>
[Description("Toggle which actions require two-factor confirmation")]
public class ActionsCommand : Command<ActionsCommand.Settings>
{
    private readonly IActionPolicyStore _policy;
    private readonly IActionCatalog _catalog;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public ActionsCommand(IActionPolicyStore policy, IActionCatalog catalog, IActionGuard guard, IAnsiConsole console)
    {
        _policy = policy;
        _catalog = catalog;
        _guard = guard;
        _console = console;
    }

    public class Settings : CommandSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var current = await _policy.GetAllAsync();

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]Actions that require a two-factor code[/] [dim](space to toggle, enter to save)[/]")
            .NotRequired()
            .PageSize(20)
            .InstructionsText("[dim]Checked = a code is required before the action runs.[/]")
            .UseConverter(id =>
            {
                var def = _catalog.Find(id);
                return def == null ? id : $"[[{def.Category}]] {def.DisplayName} [dim]({def.Id})[/]";
            });

        foreach (var def in _catalog.All.OrderBy(d => d.Category).ThenBy(d => d.Id))
        {
            var item = prompt.AddChoice(def.Id);
            if (current.TryGetValue(def.Id, out var on) && on) item.Select();
        }

        var selected = _console.Prompt(prompt);
        var selectedSet = new HashSet<string>(selected);
        var newPolicy = _catalog.All.ToDictionary(d => d.Id, d => selectedSet.Contains(d.Id));

        if (newPolicy.All(kv => current.TryGetValue(kv.Key, out var c) && c == kv.Value))
        {
            _console.MarkupLine("[dim]No changes.[/]");
            return 0;
        }

        // Persisting the change is itself gated (fail-open until a factor is enrolled).
        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.PolicyWrite, "Change which actions require two-factor"));
        }
        catch (ActionGuardDeniedException ex)
        {
            _console.MarkupLine($"[red]Denied — changes discarded:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        await _policy.SetAsync(newPolicy);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1)
            .AddColumn("[bold]Action[/]").AddColumn("[bold]Two-factor[/]");
        foreach (var def in _catalog.All.OrderBy(d => d.Category).ThenBy(d => d.Id))
            table.AddRow(Markup.Escape($"{def.DisplayName} ({def.Id})"),
                newPolicy[def.Id] ? "[green]required[/]" : "[dim]off[/]");
        _console.Write(table);
        return 0;
    }
}
