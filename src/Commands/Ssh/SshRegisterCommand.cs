using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

public class SshRegisterCommand : Command<SshRegisterCommand.Settings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public SshRegisterCommand(ISshTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [Description("SSH target in user@host format (e.g., root@myserver.com)")]
        public string? Target { get; set; }

        [CommandOption("-i|--identity <PATH>")]
        [Description("Path to SSH private key file")]
        public string? KeyPath { get; set; }

        [CommandOption("-p|--port <PORT>")]
        [Description("SSH port (default: 22)")]
        public int Port { get; set; } = 22;

        [CommandOption("--label <LABEL>")]
        [Description("Friendly label for this target")]
        public string? Label { get; set; }

        [CommandOption("--test")]
        [Description("Test SSH connectivity after registering")]
        public bool TestConnection { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Panel header
        var panel = new Panel("[bold cyan]SSH Target Registration[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        // Parse target (user@host)
        var target = settings.Target;
        if (string.IsNullOrEmpty(target))
        {
            target = _console.Ask<string>("[yellow]SSH target (user@host):[/]");
        }

        // Parse user@host
        var parts = target.Split('@', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            _console.MarkupLine("[red]Invalid target format. Use user@host (e.g., root@myserver.com)[/]");
            return 1;
        }

        var username = parts[0];
        var host = parts[1];

        // Get key path (optional — if not provided, uses SSH agent)
        var keyPath = settings.KeyPath;
        if (!string.IsNullOrEmpty(keyPath))
        {
            // Resolve to absolute path and validate
            keyPath = Path.GetFullPath(keyPath);
            if (!File.Exists(keyPath))
            {
                _console.MarkupLine($"[red]SSH key file not found: {keyPath.EscapeMarkup()}[/]");
                return 1;
            }
        }

        _console.MarkupLine($"[cyan]Host:[/] {host}");
        _console.MarkupLine($"[cyan]User:[/] {username}");
        _console.MarkupLine($"[cyan]Port:[/] {settings.Port}");
        _console.MarkupLine($"[cyan]Key:[/] {(string.IsNullOrEmpty(keyPath) ? "(SSH agent)" : keyPath.EscapeMarkup())}");

        // Optional: test connectivity
        if (settings.TestConnection)
        {
            _console.MarkupLine("[dim]Testing SSH connectivity...[/]");
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ssh",
                        Arguments = $"-i \"{keyPath}\" -p {settings.Port} -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new {username}@{host} echo ok",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _console.MarkupLine($"[red]SSH connection failed (exit code {process.ExitCode})[/]");
                    if (!string.IsNullOrWhiteSpace(error))
                        _console.MarkupLine($"[red]{error.EscapeMarkup()}[/]");
                    _console.MarkupLine("[yellow]Target will still be registered. Fix connectivity issues before using.[/]");
                }
                else
                {
                    _console.MarkupLine("[green]SSH connection successful![/]");
                }
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[yellow]Could not test SSH: {ex.Message.EscapeMarkup()}[/]");
                _console.MarkupLine("[yellow]Target will still be registered.[/]");
            }
        }

        // Save
        var sshTarget = await _configService.AddTargetAsync(host, username, settings.Port, keyPath, settings.Label);
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("ID", sshTarget.Id);
        table.AddRow("Host", sshTarget.Host);
        table.AddRow("Username", sshTarget.Username);
        table.AddRow("Port", sshTarget.Port.ToString());
        table.AddRow("Key", sshTarget.KeyPath.EscapeMarkup());
        if (!string.IsNullOrEmpty(sshTarget.Label))
            table.AddRow("Label", sshTarget.Label);
        table.AddRow("Registered", sshTarget.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[green]SSH target registered.[/]");
        _console.MarkupLine("[cyan]Use 'pks ssh list' to view all targets, or spawn a devcontainer on this host during 'pks init'.[/]");

        return 0;
    }
}
