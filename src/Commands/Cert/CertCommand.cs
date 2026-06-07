using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Cert;

[Description("List pks-held code-signing certificates")]
public class CertListCommand : Command<CertSettings>
{
    private readonly ICertStore _store;
    private readonly IAnsiConsole _console;

    public CertListCommand(ICertStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public override int Execute(CommandContext context, CertSettings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var certs = await _store.ListAsync();
        if (certs.Count == 0)
        {
            _console.MarkupLine("[yellow]No pks-held certificates.[/]");
            _console.MarkupLine("[dim]Create one with: pks cert init[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Id[/]")
            .AddColumn("[cyan]Label[/]")
            .AddColumn("[cyan]Provider[/]")
            .AddColumn("[cyan]Subject[/]")
            .AddColumn("[dim]Thumbprint[/]")
            .AddColumn("[dim]Expires[/]");

        foreach (var c in certs)
            table.AddRow(
                c.Id,
                c.Label ?? "[dim]-[/]",
                c.Provider.ToString(),
                c.Subject.EscapeMarkup(),
                c.Thumbprint,
                c.NotAfter.ToString("yyyy-MM-dd"));

        _console.Write(table);
        return 0;
    }
}

[Description("Show a pks-held certificate (details + public PEM)")]
public class CertShowCommand : Command<CertShowCommand.Settings>
{
    private readonly ICertStore _store;
    private readonly IAnsiConsole _console;

    public CertShowCommand(ICertStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : CertSettings
    {
        [CommandArgument(0, "[CERT]")]
        [Description("Cert id or label")]
        public string? Cert { get; set; }

        [CommandOption("--export-cer <PATH>")]
        [Description("Also export the public .cer to this path")]
        public string? ExportCer { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var record = await ResolveAsync(settings.Cert);
        if (record == null) { _console.MarkupLine("[red]Cert not found.[/]"); return 1; }

        _console.MarkupLine($"[cyan]Id:[/]         [bold]{record.Id}[/]" + (string.IsNullOrEmpty(record.Label) ? "" : $"   [dim]({record.Label.EscapeMarkup()})[/]"));
        _console.MarkupLine($"[cyan]Provider:[/]   {record.Provider}");
        _console.MarkupLine($"[cyan]Subject:[/]    {record.Subject.EscapeMarkup()}");
        _console.MarkupLine($"[cyan]Thumbprint:[/] {record.Thumbprint}");
        _console.MarkupLine($"[cyan]Valid:[/]      {record.NotBefore:yyyy-MM-dd} → {record.NotAfter:yyyy-MM-dd}");
        _console.WriteLine();
        _console.WriteLine(record.PublicCertPem.Trim());

        if (!string.IsNullOrWhiteSpace(settings.ExportCer))
        {
            var path = await _store.ExportPublicCerAsync(record.Id, settings.ExportCer);
            _console.WriteLine();
            _console.MarkupLine($"[green]Exported public .cer:[/] {path.EscapeMarkup()}");
        }
        return 0;
    }

    private async Task<CertRecord?> ResolveAsync(string? idOrLabel)
    {
        if (!string.IsNullOrWhiteSpace(idOrLabel)) return await _store.FindAsync(idOrLabel);
        var all = await _store.ListAsync();
        return all.Count == 1 ? all[0] : null;
    }
}

[Description("Export the public .cer (trust certificate) of a pks-held cert")]
public class CertExportCommand : Command<CertExportCommand.Settings>
{
    private readonly ICertStore _store;
    private readonly IAnsiConsole _console;

    public CertExportCommand(ICertStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : CertSettings
    {
        [CommandArgument(0, "[CERT]")]
        [Description("Cert id or label (defaults to the sole cert)")]
        public string? Cert { get; set; }

        [CommandOption("-o|--output <PATH>")]
        [Description("Destination .cer path")]
        public string? Output { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        CertRecord? record;
        if (!string.IsNullOrWhiteSpace(settings.Cert)) record = await _store.FindAsync(settings.Cert);
        else { var all = await _store.ListAsync(); record = all.Count == 1 ? all[0] : null; }

        if (record == null) { _console.MarkupLine("[red]Cert not found[/] (specify an id/label when more than one exists)."); return 1; }

        var dest = settings.Output ?? Path.Combine(Directory.GetCurrentDirectory(), record.Id + ".cer");
        var path = await _store.ExportPublicCerAsync(record.Id, dest);
        _console.MarkupLine($"[green]Exported:[/] {path.EscapeMarkup()}");
        return 0;
    }
}

[Description("Remove a pks-held certificate")]
public class CertRemoveCommand : Command<CertRemoveCommand.Settings>
{
    private readonly ICertStore _store;
    private readonly IAnsiConsole _console;

    public CertRemoveCommand(ICertStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : CertSettings
    {
        [CommandArgument(0, "[CERT]")]
        [Description("Cert id or label to remove")]
        public string? Cert { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var certs = await _store.ListAsync();
        if (certs.Count == 0) { _console.MarkupLine("[yellow]No pks-held certificates.[/]"); return 0; }

        CertRecord? record = null;
        if (!string.IsNullOrWhiteSpace(settings.Cert))
            record = await _store.FindAsync(settings.Cert);
        else
        {
            var choice = _console.Prompt(new SelectionPrompt<string>()
                .Title("[yellow]Select cert to remove:[/]")
                .AddChoices(certs.Select(c => $"{c.Id}" + (string.IsNullOrEmpty(c.Label) ? "" : $" ({c.Label})"))));
            var id = choice.Split(' ')[0];
            record = certs.FirstOrDefault(c => c.Id == id);
        }

        if (record == null) { _console.MarkupLine("[red]Cert not found.[/]"); return 1; }

        if (!_console.Confirm($"[red]Remove cert {record.Id}{(string.IsNullOrEmpty(record.Label) ? "" : $" ({record.Label})")}?[/]", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _store.RemoveAsync(record.Id);
        _console.MarkupLine($"[green]Removed cert {record.Id}.[/]");
        return 0;
    }
}
