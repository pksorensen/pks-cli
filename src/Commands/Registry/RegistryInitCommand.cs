using System.Diagnostics;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Registry;

/// <summary>
/// Register a container registry on this runner.
/// Usage: pks registry init [hostname]
/// </summary>
public class RegistryInitCommand : Command<RegistrySettings>
{
    private readonly IRegistryConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RegistryInitCommand(IRegistryConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override int Execute(CommandContext context, RegistrySettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, RegistrySettings settings)
    {
        var panel = new Panel("[bold cyan]Registry Init[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        // Get hostname
        var hostname = settings.Hostname;
        if (string.IsNullOrWhiteSpace(hostname))
            hostname = _console.Ask<string>("[yellow]Registry hostname:[/]");

        // Normalize: strip scheme and trailing slash
        hostname = hostname
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        _console.MarkupLine($"[cyan]Registry:[/] {hostname}");

        // Get credentials
        var username = _console.Ask<string>("[yellow]Username:[/]");
        var password = _console.Prompt(
            new TextPrompt<string>("[yellow]Password:[/]").Secret());

        // Verify via docker login
        _console.MarkupLine("[dim]Verifying via docker login...[/]");
        try
        {
            var psi = new ProcessStartInfo("docker", $"login --username {username} --password-stdin {hostname}")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi)!;
            await proc.StandardInput.WriteAsync(password);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                var err = await proc.StandardError.ReadToEndAsync();
                _console.MarkupLine($"[red]docker login failed: {err.Trim().EscapeMarkup()}[/]");
                _console.MarkupLine("[yellow]Check that the hostname, username, and password are correct.[/]");
                return 1;
            }

            _console.MarkupLine("[green]docker login succeeded[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to run docker login: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        // Save
        var entry = await _configService.AddAsync(hostname, username, password);
        _console.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Green)
            .AddColumn("[yellow]Property[/]")
            .AddColumn("[cyan]Value[/]");

        table.AddRow("Hostname", entry.Hostname);
        table.AddRow("Username", entry.Username);
        table.AddRow("Registered", entry.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[green]Registry registered successfully.[/]");
        _console.MarkupLine("[cyan]Run 'pks registry init' on each self-hosted runner.[/]");

        return 0;
    }
}
