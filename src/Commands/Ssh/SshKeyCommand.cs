using System.ComponentModel;
using System.Text;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

/// <summary>Import an existing SSH private key into the pks-held store and (optionally) register a target
/// that uses it. The key is never written to a path the agent uses directly — <c>pks ssh connect</c> is the
/// only consumer, and it is gated by the action guard.</summary>
[Description("Import an SSH private key into the pks-held store")]
public class SshKeyImportCommand : Command<SshKeyImportCommand.Settings>
{
    private readonly ISshKeyStore _keyStore;
    private readonly ISshTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public SshKeyImportCommand(ISshKeyStore keyStore, ISshTargetConfigurationService configService, IAnsiConsole console)
    {
        _keyStore = keyStore;
        _configService = configService;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandOption("--label <LABEL>")]
        [Description("Friendly label for this key (e.g. hetzner)")]
        public string? Label { get; set; }

        [CommandOption("--from-file <PATH>")]
        [Description("Read the private key from a file instead of pasting it")]
        public string? FromFile { get; set; }

        [CommandOption("--register <TARGET>")]
        [Description("Also register an SSH target (user@host) bound to this key")]
        public string? Register { get; set; }

        [CommandOption("-p|--port <PORT>")]
        [Description("SSH port for the registered target (default: 22)")]
        public int Port { get; set; } = 22;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        string pem;
        if (!string.IsNullOrWhiteSpace(settings.FromFile))
        {
            if (!File.Exists(settings.FromFile))
            {
                _console.MarkupLine($"[red]Key file not found: {settings.FromFile.EscapeMarkup()}[/]");
                return 1;
            }
            pem = await File.ReadAllTextAsync(settings.FromFile);
        }
        else
        {
            _console.MarkupLine("[cyan]Paste the private key, then press Enter on an empty line:[/]");
            _console.MarkupLine("[dim](must be an unencrypted OpenSSH/PEM key)[/]");
            var sb = new StringBuilder();
            string? line;
            var sawEnd = false;
            while ((line = Console.ReadLine()) != null)
            {
                sb.AppendLine(line);
                if (line.StartsWith("-----END", StringComparison.Ordinal)) { sawEnd = true; }
                else if (sawEnd && string.IsNullOrWhiteSpace(line)) break;
                else if (string.IsNullOrWhiteSpace(line) && sb.ToString().Contains("-----END")) break;
            }
            pem = sb.ToString();
        }

        SshKeyRecord record;
        try
        {
            record = await _keyStore.ImportAsync(pem, settings.Label);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Import failed:[/] {ex.Message.EscapeMarkup()}");
            return 1;
        }

        _console.MarkupLine($"[green]Key imported.[/] id [cyan]{record.Id}[/]" +
            (string.IsNullOrEmpty(record.Label) ? "" : $" ({record.Label.EscapeMarkup()})"));
        if (!string.IsNullOrWhiteSpace(record.Fingerprint))
            _console.MarkupLine($"[dim]{record.Fingerprint.EscapeMarkup()}[/]");
        _console.WriteLine();
        _console.MarkupLine("[cyan]Public key[/] (ensure this is in the host's ~/.ssh/authorized_keys):");
        _console.WriteLine(record.PublicKey);
        _console.WriteLine();

        if (!string.IsNullOrWhiteSpace(settings.Register))
        {
            var parts = settings.Register.Split('@', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                _console.MarkupLine("[yellow]--register must be user@host; skipping target registration.[/]");
            }
            else
            {
                var target = await _configService.AddTargetAsync(parts[1], parts[0], settings.Port, "", settings.Label, record.Id);
                _console.MarkupLine($"[green]Registered target[/] {target.Username}@{target.Host}:{target.Port} " +
                    $"→ key [cyan]{record.Id}[/]. Connect with [cyan]pks ssh connect {settings.Label ?? target.Host}[/].");
            }
        }
        else
        {
            _console.MarkupLine("[dim]Register a target with: pks ssh register user@host (then it will use this key), " +
                "or re-run with --register user@host.[/]");
        }

        return 0;
    }
}

[Description("List pks-held SSH keys")]
public class SshKeyListCommand : Command<SshSettings>
{
    private readonly ISshKeyStore _keyStore;
    private readonly IAnsiConsole _console;

    public SshKeyListCommand(ISshKeyStore keyStore, IAnsiConsole console)
    {
        _keyStore = keyStore;
        _console = console;
    }

    public override int Execute(CommandContext context, SshSettings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var keys = await _keyStore.ListAsync();
        if (keys.Count == 0)
        {
            _console.MarkupLine("[yellow]No pks-held SSH keys.[/]");
            _console.MarkupLine("[dim]Import one with: pks ssh key import --label hetzner[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Id[/]")
            .AddColumn("[cyan]Label[/]")
            .AddColumn("[cyan]Fingerprint[/]")
            .AddColumn("[dim]Imported[/]");

        foreach (var k in keys)
            table.AddRow(k.Id, k.Label ?? "[dim]-[/]", k.Fingerprint.EscapeMarkup(), k.ImportedAt.ToString("yyyy-MM-dd HH:mm"));

        _console.Write(table);
        return 0;
    }
}

[Description("Remove a pks-held SSH key")]
public class SshKeyRemoveCommand : Command<SshKeyRemoveCommand.Settings>
{
    private readonly ISshKeyStore _keyStore;
    private readonly IAnsiConsole _console;

    public SshKeyRemoveCommand(ISshKeyStore keyStore, IAnsiConsole console)
    {
        _keyStore = keyStore;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandArgument(0, "[KEY]")]
        [Description("Key id or label to remove")]
        public string? Key { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var keys = await _keyStore.ListAsync();
        if (keys.Count == 0)
        {
            _console.MarkupLine("[yellow]No pks-held SSH keys.[/]");
            return 0;
        }

        SshKeyRecord? record = null;
        if (!string.IsNullOrWhiteSpace(settings.Key))
            record = await _keyStore.FindAsync(settings.Key);
        else
        {
            var choice = _console.Prompt(new SelectionPrompt<string>()
                .Title("[yellow]Select key to remove:[/]")
                .AddChoices(keys.Select(k => $"{k.Id}" + (string.IsNullOrEmpty(k.Label) ? "" : $" ({k.Label})"))));
            var id = choice.Split(' ')[0];
            record = keys.FirstOrDefault(k => k.Id == id);
        }

        if (record == null)
        {
            _console.MarkupLine("[red]Key not found.[/]");
            return 1;
        }

        if (!_console.Confirm($"[red]Remove key {record.Id}{(string.IsNullOrEmpty(record.Label) ? "" : $" ({record.Label})")}?[/]", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _keyStore.RemoveAsync(record.Id);
        _console.MarkupLine($"[green]Removed key {record.Id}.[/]");
        return 0;
    }
}
