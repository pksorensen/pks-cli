using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Providers;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Providers;

public class ProvidersSettings : CommandSettings;

/// <summary>
/// The providers a host can open, in the order the wizard offers them. Built per invocation from the
/// services the commands already use, so a provider added here is the same provider its own
/// `pks &lt;provider&gt; init` sets up.
/// </summary>
public sealed class ProviderStepCatalog
{
    private readonly IReadOnlyList<IProviderStep> _steps;

    public ProviderStepCatalog(
        IProviderApiKeyService keys,
        IAzureFoundryAuthService foundry,
        AzureFoundryAuthConfig foundryConfig,
        IManagedIdentityClient managedIdentity,
        IGoogleAiService google,
        IOpenRouterService openRouter,
        INvidiaService nvidia,
        IMoonshotService moonshot,
        ITypeSafeCredentialService typeSafe,
        ISecretStore secrets,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _steps = new IProviderStep[]
        {
            new DirectApiKeyStep(ApiKeyProvider.OpenAI, keys, guard, console),
            new DirectApiKeyStep(ApiKeyProvider.Anthropic, keys, guard, console),
            new FoundryStep(foundry, foundryConfig, managedIdentity, console),
            new GoogleStep(google, guard, console),
            new TypeSafeStep(typeSafe, secrets, guard, console),
            new OpenRouterStep(openRouter, guard, console),
            new NvidiaStep(nvidia, guard, console),
            new MoonshotStep(moonshot, guard, console),
        };
    }

    internal ProviderStepCatalog(IReadOnlyList<IProviderStep> steps) => _steps = steps;

    public IReadOnlyList<IProviderStep> All => _steps;

    /// <summary>The steps named by <paramref name="only"/> (comma separated ids), or all of them.
    /// Unknown ids come back in <paramref name="unknown"/> rather than being ignored.</summary>
    public IReadOnlyList<IProviderStep> Select(string? only, out IReadOnlyList<string> unknown)
    {
        if (string.IsNullOrWhiteSpace(only))
        {
            unknown = Array.Empty<string>();
            return _steps;
        }
        var ids = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        unknown = ids.Where(id => _steps.All(s => !s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList();
        return _steps.Where(s => ids.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>
/// `pks providers init` — walks every model provider pks supports and sets up the ones the operator
/// wants, on this host, for every project it runs. Safe to run again and again: a configured provider
/// is left alone unless named with <c>--force</c>, so the second run is how you open one more.
///
/// Unattended (<c>--from-env</c>, or whenever stdin is not a terminal — e.g. inside
/// <c>curl … | bash</c>, where stdin is the script itself) it never prompts: it reads each
/// provider's key from its environment variable and stores what is new.
/// </summary>
[Description("Set up model providers on this host (run again any time to add one)")]
public sealed class ProvidersInitCommand : AsyncCommand<ProvidersInitCommand.Settings>
{
    private readonly ProviderStepCatalog _catalog;
    private readonly IAnsiConsole _console;

    public ProvidersInitCommand(ProviderStepCatalog catalog, IAnsiConsole console)
    {
        _catalog = catalog;
        _console = console;
    }

    /// <summary>Whether stdin is something other than a terminal. A seam for tests, whose runner
    /// redirects stdin; in production this is what catches <c>curl … | bash</c>.</summary>
    internal Func<bool> InputRedirected { get; init; } = () => System.Console.IsInputRedirected;

    public sealed class Settings : ProvidersSettings
    {
        [CommandOption("--only <IDS>")]
        [Description("Comma-separated provider ids to consider (see `pks providers status`)")]
        public string? Only { get; set; }

        [CommandOption("-f|--force")]
        [Description("Unattended: store keys from the environment even when a provider is already configured")]
        public bool Force { get; set; }

        [CommandOption("--from-env")]
        [Description("Never prompt: read keys from ANTHROPIC_API_KEY, OPENAI_API_KEY, … (Foundry: AZURE_FOUNDRY_RESOURCE + managed identity)")]
        public bool FromEnv { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var steps = _catalog.Select(settings.Only, out var unknown);
        if (unknown.Count > 0)
        {
            _console.MarkupLine($"[red]Unknown provider(s): {Markup.Escape(string.Join(", ", unknown))}.[/] Known: {Markup.Escape(string.Join(", ", _catalog.All.Select(s => s.Id)))}");
            return 1;
        }

        var unattended = settings.FromEnv || !_console.Profile.Capabilities.Interactive || InputRedirected();
        return unattended
            ? await RunUnattendedAsync(steps, settings.Force)
            : await RunInteractiveAsync(steps);
    }

    private async Task<int> RunInteractiveAsync(IReadOnlyList<IProviderStep> steps)
    {
        // One checklist, not a question per provider: the status is on each line, so the operator
        // sees the whole host at once and ticks what to set up. A configured provider is listed too —
        // ticking it is how you replace its key — but never pre-ticked, so Enter on its own changes
        // nothing.
        var labels = new Dictionary<IProviderStep, string>();
        foreach (var step in steps)
        {
            var configured = await step.IsConfiguredAsync();
            var hint = await step.HintAsync();
            var label = $"{Markup.Escape(step.DisplayName),-22} "
                + (configured ? "[green]configured[/]" : "[dim]not set[/]   ")
                + $"  [dim]{Markup.Escape(step.Reach)}[/]";
            if (hint is not null) label += $" [yellow]· {Markup.Escape(hint)}[/]";
            labels[step] = label;
        }

        var selected = _console.Prompt(
            new MultiSelectionPrompt<IProviderStep>()
                .Title("[bold]Which model providers should this host set up?[/]")
                .InstructionsText("[dim](↑/↓ to move, [blue]space[/] to select, [green]enter[/] to continue — configured ones are set up again if selected)[/]")
                .NotRequired()
                .PageSize(Math.Max(3, steps.Count))
                .UseConverter(step => labels[step])
                .AddChoices(steps));

        if (selected.Count == 0)
        {
            _console.MarkupLine("[dim]Nothing selected — no changes.[/]");
            return 0;
        }

        var failed = 0;
        foreach (var step in steps.Where(selected.Contains))
        {
            _console.Write(new Rule($"[cyan]{Markup.Escape(step.DisplayName)}[/]").LeftJustified());
            bool ok;
            try
            {
                ok = await step.RunInteractiveAsync(force: await step.IsConfiguredAsync());
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]{Markup.Escape(step.DisplayName)} failed: {Markup.Escape(ex.Message)}[/]");
                ok = false;
            }
            if (!ok) failed++;
            _console.WriteLine();
        }

        await ProvidersStatusCommand.WriteStatusAsync(_console, steps);
        _console.MarkupLine("[dim]Run [bold]pks providers init[/] again any time to add another provider.[/]");
        return failed == 0 ? 0 : 1;
    }

    private async Task<int> RunUnattendedAsync(IReadOnlyList<IProviderStep> steps, bool force)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Provider");
        table.AddColumn("Source");
        table.AddColumn("Result");

        var failed = 0;
        foreach (var step in steps)
        {
            var value = step.EnvVar is null ? null : Environment.GetEnvironmentVariable(step.EnvVar);
            StepOutcome outcome;
            try
            {
                outcome = await step.ApplyUnattendedAsync(value, force);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]{Markup.Escape(step.DisplayName)} failed: {Markup.Escape(ex.Message)}[/]");
                outcome = StepOutcome.Failed;
            }
            if (outcome == StepOutcome.Failed) failed++;

            var source = step.EnvVar ?? $"{FoundryStep.ResourceEnv} + managed identity";
            var configured = outcome is StepOutcome.Skipped && await step.IsConfiguredAsync();
            table.AddRow(
                Markup.Escape(step.DisplayName),
                $"[dim]{Markup.Escape(source)}[/]",
                outcome switch
                {
                    StepOutcome.Stored => "[green]stored[/]",
                    StepOutcome.StoredUnverified => "[yellow]stored (not verified)[/]",
                    StepOutcome.Unchanged => "[green]unchanged[/]",
                    StepOutcome.Failed => "[red]failed[/]",
                    _ => configured ? "[green]configured[/] [dim](not in env)[/]" : "[dim]not set[/]",
                });
        }

        _console.Write(table);
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>`pks providers status` — what this host can call, and who on it can use each provider.</summary>
[Description("Show which model providers are set up on this host")]
public sealed class ProvidersStatusCommand : AsyncCommand<ProvidersSettings>
{
    private readonly ProviderStepCatalog _catalog;
    private readonly IAnsiConsole _console;

    public ProvidersStatusCommand(ProviderStepCatalog catalog, IAnsiConsole console)
    {
        _catalog = catalog;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ProvidersSettings settings)
    {
        await WriteStatusAsync(_console, _catalog.All);
        return 0;
    }

    internal static async Task WriteStatusAsync(IAnsiConsole console, IReadOnlyList<IProviderStep> steps)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Model providers on this host[/]");
        table.AddColumn("Id");
        table.AddColumn("Provider");
        table.AddColumn("Status");
        table.AddColumn("Used by");
        foreach (var step in steps)
        {
            table.AddRow(
                Markup.Escape(step.Id),
                Markup.Escape(step.DisplayName),
                await step.IsConfiguredAsync() ? "[green]configured[/]" : "[dim]not set[/]",
                $"[dim]{Markup.Escape(step.Reach)}[/]");
        }
        console.Write(table);
    }
}
