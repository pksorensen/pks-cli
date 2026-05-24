using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Voice;

[Description("Open the heypoul settings window")]
public class VoiceSettingsCommand : AsyncCommand<VoiceSettings>
{
    private readonly IAnsiConsole _console;

    public VoiceSettingsCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, VoiceSettings settings)
    {
        var heypoulBin = await ResolveHeypoulBinaryAsync();
        if (heypoulBin == null)
        {
            _console.MarkupLine("[red]heypoul binary not found.[/]");
            _console.MarkupLine("[dim]Run [bold]pks voice start[/] first — it extracts the embedded Windows binary on first use.[/]");
            return 1;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _console.MarkupLine("[yellow]The settings window is currently Windows-only.[/]");
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "heypoul", "config.json");
            _console.MarkupLine($"[dim]Edit [bold]{Markup.Escape(configPath)}[/] manually for now.[/]");
            return 0;
        }

        // Fire-and-forget: heypoul --settings is its own GUI process. Don't wait —
        // the user keeps using the terminal while the dialog is open.
        var psi = new ProcessStartInfo(heypoulBin)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--settings");

        var proc = Process.Start(psi);
        if (proc == null)
        {
            _console.MarkupLine("[red]Failed to launch heypoul --settings.[/]");
            return 1;
        }

        _console.MarkupLine("[dim]Settings window opened. Save closes the window and writes to config.json.[/]");
        return 0;
    }

    // Mirrors VoiceStartCommand.ResolveHeypoulPathAsync but lives here so the
    // settings command doesn't depend on a running daemon or Foundry auth.
    private static async Task<string?> ResolveHeypoulBinaryAsync()
    {
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "heypoul.exe" : "heypoul";

        var pathSep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(pathSep))
        {
            var candidate = Path.Combine(dir.Trim(), binaryName);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var rel in new[] { $"./{binaryName}", $"./projects/heypoul/{binaryName}", $"../projects/heypoul/{binaryName}" })
        {
            var full = Path.GetFullPath(rel);
            if (File.Exists(full)) return full;
        }

        // Extract embedded Windows binary — same logic as VoiceStartCommand so the
        // first `pks voice settings` works even before the user has run start.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("heypoul-win-amd64");
            if (stream != null)
            {
                var dest = Path.Combine(Path.GetTempPath(), "heypoul.exe");
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                try
                {
                    await using var f = File.Create(dest);
                    await ms.CopyToAsync(f);
                }
                catch (IOException)
                {
                    // Binary in use — reuse whatever is on disk.
                }
                return dest;
            }
        }

        return null;
    }
}
