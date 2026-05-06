using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Voice;

[Description("Stop the running heypoul voice assistant")]
public class VoiceStopCommand : AsyncCommand<VoiceSettings>
{
    private readonly IAnsiConsole _console;

    public VoiceStopCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override Task<int> ExecuteAsync(CommandContext context, VoiceSettings settings)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), "heypoul.pid");
        if (!File.Exists(pidFile))
        {
            _console.MarkupLine("[yellow]heypoul is not running (no pid file found).[/]");
            return Task.FromResult(0);
        }

        var pidText = File.ReadAllText(pidFile).Trim();
        if (!int.TryParse(pidText, out var pid))
        {
            _console.MarkupLine("[red]Invalid pid file.[/]");
            File.Delete(pidFile);
            return Task.FromResult(1);
        }

        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Kill();
            proc.WaitForExit(3000);
            File.Delete(pidFile);
            _console.MarkupLine($"[green]heypoul stopped (pid {pid}).[/]");
        }
        catch (ArgumentException)
        {
            File.Delete(pidFile);
            _console.MarkupLine("[dim]heypoul was not running (process already gone).[/]");
        }

        return Task.FromResult(0);
    }
}
