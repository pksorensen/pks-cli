using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Rsync;

public class RsyncInitCommand : AsyncCommand<RsyncSettings>
{
    private readonly IRsyncTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RsyncInitCommand(IRsyncTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RsyncSettings settings)
    {
        _console.Write(new Panel("[bold cyan]rsync Backup Target Setup[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0));
        _console.WriteLine();

        // user@host
        var userAtHost = _console.Ask<string>("[yellow]Remote host (user@host):[/]");
        var parts = userAtHost.Trim().Split('@', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            _console.MarkupLine("[red]Invalid format. Use user@host (e.g., backup@nas.local)[/]");
            return 1;
        }
        var username = parts[0].Trim();
        var host = parts[1].Trim();

        // port
        var portStr = _console.Prompt(
            new TextPrompt<string>("[yellow]SSH port:[/]").DefaultValue("22"));
        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
        {
            _console.MarkupLine("[red]Invalid port number.[/]");
            return 1;
        }

        // SSH key path (optional)
        var keyInput = _console.Prompt(
            new TextPrompt<string>("[yellow]SSH key path (leave blank for agent):[/]")
                .AllowEmpty().DefaultValue(""));
        string? keyPath = null;
        if (!string.IsNullOrWhiteSpace(keyInput))
        {
            keyPath = Path.GetFullPath(keyInput.Trim());
            if (!File.Exists(keyPath))
            {
                _console.MarkupLine($"[red]Key file not found: {keyPath.EscapeMarkup()}[/]");
                return 1;
            }
        }

        // remote backup path
        var remotePath = _console.Ask<string>("[yellow]Remote backup path (e.g. /mnt/nas/claude-backups/):[/]");
        remotePath = remotePath.Trim();
        if (!remotePath.EndsWith('/')) remotePath += '/';

        // label (optional)
        var labelInput = _console.Prompt(
            new TextPrompt<string>("[yellow]Label (leave blank to skip):[/]")
                .AllowEmpty().DefaultValue(""));
        string? label = string.IsNullOrWhiteSpace(labelInput) ? null : labelInput.Trim();

        // Connectivity test
        _console.WriteLine();
        _console.MarkupLine("[dim]Testing SSH connectivity...[/]");
        var connected = await TestSshAsync(host, username, port, keyPath);
        if (!connected)
        {
            _console.MarkupLine("[yellow]Could not verify connectivity. Target will still be saved.[/]");
            _console.MarkupLine("[dim]Make sure the host is reachable and the key is authorized.[/]");
        }
        else
        {
            _console.MarkupLine("[green]SSH connection successful.[/]");
        }

        // Save
        var target = await _configService.AddTargetAsync(host, username, port, keyPath, remotePath, label);

        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("ID", target.Id);
        table.AddRow("Host", target.Host);
        table.AddRow("Username", target.Username);
        table.AddRow("Port", target.Port.ToString());
        table.AddRow("Key", target.KeyPath?.EscapeMarkup() ?? "(SSH agent)");
        table.AddRow("Remote path", target.RemotePath.EscapeMarkup());
        if (!string.IsNullOrEmpty(target.Label))
            table.AddRow("Label", target.Label);
        table.AddRow("Registered", target.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[green]rsync target saved.[/] Run [cyan]pks claude backup[/] to sync [dim]~/.claude/[/].");

        return 0;
    }

    private static async Task<bool> TestSshAsync(string host, string username, int port, string? keyPath)
    {
        try
        {
            var keyArg = keyPath != null ? $"-i \"{keyPath}\" " : "";
            var psi = new System.Diagnostics.ProcessStartInfo("ssh",
                $"{keyArg}-p {port} -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new {username}@{host} echo ok")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
