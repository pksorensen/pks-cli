using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Cert;

/// <summary>
/// Create a pks-held self-signed code-signing certificate, once, and store it encrypted under
/// <c>~/.pks-cli/certs/</c>. This is the trust event: it runs interactively from a trusted host
/// context (refuses sudo/redirected I/O, like <c>pks authenticator init</c>), and afterwards
/// <c>pks sign</c> uses the stored cert unattended across CI runs. The exported public <c>.cer</c>
/// is what consumers trust once — it stays stable instead of changing every release.
/// </summary>
[Description("Create a pks-held self-signed code-signing certificate (interactive, once)")]
public class CertInitCommand : Command<CertInitCommand.Settings>
{
    private readonly ICertStore _store;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public CertInitCommand(ICertStore store, IActionGuard guard, IAnsiConsole console)
    {
        _store = store;
        _guard = guard;
        _console = console;
    }

    public class Settings : CertSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        // Creating signing material must come from a context the in-container agent cannot reach
        // (same reasoning as authenticator enrollment) — refuse the sudo path.
        if (SecurityContext.IsSudoInvoked)
        {
            _console.MarkupLine("[red]Cert creation can't run via sudo inside the container[/] (an agent could create its own signing cert).");
            _console.MarkupLine("[dim]" + Markup.Escape(SecurityContext.HostEnrollmentHint) + "[/]");
            return 1;
        }

        // Interactive only — the prompts need a real terminal.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            _console.MarkupLine("[red]Cert creation must run in an interactive terminal[/] (use [cyan]docker exec -it -u pks …[/] or run it on the runner host).");
            return 1;
        }

        // Trust-once: if a self-signed cert already exists, replacing it requires the second factor.
        if (await _store.AnyAsync())
        {
            _console.MarkupLine("[yellow]A pks-held certificate already exists.[/] Creating another will add a new one.");
            try
            {
                await _guard.RequireAsync(new ActionRequest(
                    ActionIds.CertWrite,
                    "Create an additional pks-held code-signing certificate"));
            }
            catch (ActionGuardDeniedException ex)
            {
                _console.MarkupLine($"[red]Cert creation denied:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }
        }

        var subject = _console.Prompt(
            new TextPrompt<string>("[cyan]Certificate subject[/] [dim](MUST match the MSIX appxmanifest Publisher)[/]:")
                .DefaultValue("CN=Agentic Live (Self-Signed)")
                .PromptStyle("cyan"));

        var label = _console.Prompt(
            new TextPrompt<string>("[cyan]Label[/] [dim](friendly name, optional)[/]:")
                .AllowEmpty()
                .DefaultValue("agentics"));

        var years = _console.Prompt(
            new SelectionPrompt<int>()
                .Title("[cyan]Validity[/]:")
                .AddChoices(1, 2, 3, 5)
                .UseConverter(y => $"{y} year{(y == 1 ? "" : "s")}"));

        CertRecord record;
        try
        {
            record = await _store.CreateSelfSignedAsync(subject, string.IsNullOrWhiteSpace(label) ? null : label, TimeSpan.FromDays(365 * years));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Cert creation failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        // Export the public trust cert next to the store by default.
        var defaultCer = SecurityFiles.PathFor(Path.Combine("certs", record.Id + ".cer"));
        var cerPath = _console.Prompt(
            new TextPrompt<string>("[cyan]Export public .cer to[/]:")
                .DefaultValue(defaultCer)
                .PromptStyle("cyan"));

        string? exported = null;
        try { exported = await _store.ExportPublicCerAsync(record.Id, cerPath); }
        catch (Exception ex) { _console.MarkupLine($"[yellow]Could not export .cer:[/] {Markup.Escape(ex.Message)}"); }

        var body = string.Join("\n", new[]
        {
            $"  [cyan]Id:[/]         [bold]{record.Id}[/]" + (string.IsNullOrEmpty(record.Label) ? "" : $"   [dim]({Markup.Escape(record.Label)})[/]"),
            $"  [cyan]Subject:[/]    {Markup.Escape(record.Subject)}",
            $"  [cyan]Thumbprint:[/] {record.Thumbprint}",
            $"  [cyan]Valid:[/]      {record.NotBefore:yyyy-MM-dd} → {record.NotAfter:yyyy-MM-dd}",
            exported is null ? "" : $"  [cyan]Public .cer:[/] {Markup.Escape(exported)}",
        }.Where(s => s.Length > 0));

        _console.Write(new Panel(body)
            .Border(BoxBorder.Rounded)
            .BorderStyle("green")
            .Header(" [bold green]Code-signing certificate created[/] "));

        _console.MarkupLine("[dim]Sign with: [bold]pks sign <file.msix>[/]. Distribute the .cer so consumers can trust the package.[/]");
        return 0;
    }
}
