using PKS.Infrastructure.Services;
using Spectre.Console;

namespace PKS.Commands.Ssh;

/// <summary>
/// Shared SSH target picker: resolve by name argument, auto-select when only one target is
/// registered, else prompt. Modeled directly on <see cref="PKS.Commands.Vm.VmSelection.Pick"/> --
/// every prior SSH-target picker in this codebase (<c>SshConnectCommand</c>,
/// <c>ClaudeSpawnCommand</c> et al.) hand-rolled its own <c>SelectionPrompt&lt;string&gt;</c> over
/// <c>Label ?? Host</c>; this is the shared version so Phase 4 doesn't add a ninth bespoke variant.
/// See docs/remote-runner-targets-plan.md Phase 4, obstacle (b).
/// </summary>
public static class SshTargetSelection
{
    /// <summary>
    /// Resolve a target. Precedence: explicit <paramref name="nameArg"/> match (by label or host) &gt;
    /// auto-select when exactly one target is registered &gt; interactive prompt. When zero targets
    /// are registered, prints an actionable message (never throws) and returns <c>null</c> --
    /// callers should treat a <c>null</c> result as "nothing to do here", not as an error to log
    /// again.
    /// </summary>
    public static Task<SshTarget?> PickAsync(
        IAnsiConsole console,
        IReadOnlyList<SshTarget> targets,
        string? nameArg,
        string title)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(targets);

        if (!string.IsNullOrWhiteSpace(nameArg))
        {
            var found = targets.FirstOrDefault(t =>
                string.Equals(t.Label, nameArg, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Host, nameArg, StringComparison.OrdinalIgnoreCase));

            if (found == null)
            {
                console.MarkupLine($"[red]SSH target '{Markup.Escape(nameArg)}' not found.[/]");
            }

            return Task.FromResult(found);
        }

        if (targets.Count == 0)
        {
            console.MarkupLine("[yellow]No SSH targets registered.[/] Run [bold]pks ssh register[/] or [bold]pks vm init[/] first.");
            return Task.FromResult<SshTarget?>(null);
        }

        if (targets.Count == 1)
        {
            return Task.FromResult<SshTarget?>(targets[0]);
        }

        var choice = console.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .HighlightStyle(Style.Parse("cyan"))
                .PageSize(15)
                .AddChoices(targets.Select(t => t.Label ?? t.Host)));

        return Task.FromResult<SshTarget?>(targets.First(t => (t.Label ?? t.Host) == choice));
    }
}
