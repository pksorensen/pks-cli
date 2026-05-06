using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Voice;

[Description("Start heypoul push-to-talk voice assistant")]
public class VoiceStartCommand : AsyncCommand<VoiceStartCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public VoiceStartCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config, IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public class Settings : VoiceSettings
    {
        [CommandOption("--key|-k")]
        [Description("Key code for push-to-talk. Linux: /dev/input code (run sudo find-key). Windows: VK hex e.g. 0xA5 for right-alt. Default: 100 (Linux right-alt) / 165 (Windows right-alt)")]
        public uint? KeyCode { get; set; }

        [CommandOption("--language|-l")]
        [Description("Speech recognition language (default: da-DK)")]
        public string Language { get; set; } = "da-DK";

        [CommandOption("--inject")]
        [Description("Injection mode: text (default) or command (presses Enter after injection)")]
        public string InjectMode { get; set; } = "text";

        [CommandOption("--heypoul")]
        [Description("Path to heypoul binary (default: auto-detect or extract embedded)")]
        public string? HeypoulPath { get; set; }

        [CommandOption("--inline")]
        [Description("Block the terminal and show inline waveform display (debug mode)")]
        public bool Inline { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceName))
        {
            _console.MarkupLine("[red]No Foundry resource configured. Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to acquire Azure access token. Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        // Platform-aware defaults: Windows right-alt = VK 0xA5 (165), Linux = 100
        var keyCode = settings.KeyCode ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 165u : 100u);

        var heypoulBin = await ResolveHeypoulPathAsync(settings.HeypoulPath);
        if (heypoulBin == null)
        {
            _console.MarkupLine("[red]heypoul binary not found.[/]");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _console.MarkupLine("[dim]Build with build-local.sh (embeds heypoul.exe automatically) or place heypoul.exe in PATH.[/]");
            else
                _console.MarkupLine("[dim]Run: cd sandbox/heypoul && go build -o heypoul . (or ./build.sh)[/]");
            return 1;
        }

        var commands = await PromptCommandMapAsync();
        var commandsJson = JsonSerializer.Serialize(commands);

        var endpoint = $"https://{creds.SelectedResourceName}.cognitiveservices.azure.com";

        _console.WriteLine();
        _console.MarkupLine("[cyan]Starting heypoul[/]");
        _console.MarkupLine($"[dim]  endpoint: {Markup.Escape(endpoint)}[/]");
        _console.MarkupLine($"[dim]  language: {settings.Language}  key: {keyCode}[/]");
        _console.MarkupLine($"[dim]  commands: {commands.Count} mapped[/]");
        _console.MarkupLine($"[dim]  binary:   {Markup.Escape(heypoulBin)}[/]");
        _console.WriteLine();

        var pidFile = Path.Combine(Path.GetTempPath(), "heypoul.pid");

        var args = new List<string>();
        if (settings.Inline) args.Add("--debug"); // heypoul's flag is still --debug internally

        var psi = new ProcessStartInfo(heypoulBin)
        {
            UseShellExecute = false,
            // In inline mode, keep stdio attached so the user sees the inline waveform.
            // In daemon mode, detach so heypoul runs silently in the background.
            RedirectStandardOutput = !settings.Inline,
            RedirectStandardError = !settings.Inline,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        psi.Environment["HEYPOUL_ENDPOINT"] = endpoint;
        psi.Environment["HEYPOUL_TOKEN"] = token;
        // Pass subscription key — the Speech REST API prefers Ocp-Apim-Subscription-Key over Azure AD Bearer tokens
        if (!string.IsNullOrEmpty(creds.ApiKey))
            psi.Environment["HEYPOUL_API_KEY"] = creds.ApiKey;
        psi.Environment["HEYPOUL_LANGUAGE"] = settings.Language;
        psi.Environment["HEYPOUL_KEY"] = keyCode.ToString();
        psi.Environment["HEYPOUL_INJECT"] = settings.InjectMode;
        psi.Environment["HEYPOUL_COMMANDS"] = commandsJson;
        // Use stored classifier model (set via pks foundry select)
        if (!string.IsNullOrEmpty(creds.VoiceClassifierModel))
            psi.Environment["HEYPOUL_CLASSIFIER"] = creds.VoiceClassifierModel;

        var proc = Process.Start(psi);
        if (proc == null)
        {
            _console.MarkupLine("[red]Failed to start heypoul.[/]");
            return 1;
        }

        if (settings.Inline)
        {
            // Block the terminal — user sees the inline waveform on stderr.
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }

        // Daemon mode: heypoul writes its own PID file to /tmp/heypoul.pid.
        // We just report the PID and return — pks voice off kills it later.
        _console.MarkupLine($"[dim]heypoul running (pid {proc.Id}) — [bold]pks voice off[/] to stop[/]");
        return 0;
    }

    private async Task<Dictionary<string, string>> PromptCommandMapAsync()
    {
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!_console.Confirm(
                "[cyan]Configure voice command shortcuts?[/] (say a phrase → runs a command)",
                defaultValue: false))
            return commands;

        var suggestions = new[]
        {
            ("launch claude", "pks claude"),
            ("launch vibecast", "pks vibecast"),
            ("git status", "git status"),
            ("run tests", "dotnet test"),
        };

        if (_console.Confirm("[cyan]Load suggested shortcuts?[/]", defaultValue: true))
        {
            foreach (var (phrase, cmd) in suggestions)
                commands[phrase] = cmd;
            _console.MarkupLine("[dim]Suggestions loaded. Add more below, or leave blank to finish.[/]");
        }

        while (true)
        {
            var phrase = _console.Ask<string>("[dim]Voice phrase (blank to finish):[/]").Trim();
            if (string.IsNullOrEmpty(phrase)) break;
            var cmd = _console.Ask<string>($"[dim]Command for \"{Markup.Escape(phrase)}\":[/]").Trim();
            if (!string.IsNullOrEmpty(cmd))
                commands[phrase] = cmd;
        }

        return commands;
    }

    /// <summary>
    /// Resolves the heypoul binary path. Priority:
    ///   1. Explicit --heypoul flag
    ///   2. heypoul / heypoul.exe anywhere in PATH
    ///   3. Common relative paths (sandbox/heypoul/heypoul)
    ///   4. Embedded heypoul-win-amd64 resource (extracted to %TEMP%\heypoul.exe on Windows)
    /// </summary>
    private async Task<string?> ResolveHeypoulPathAsync(string? userPath)
    {
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "heypoul.exe" : "heypoul";

        if (!string.IsNullOrEmpty(userPath) && File.Exists(userPath))
            return userPath;

        var pathSep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(pathSep))
        {
            var candidate = Path.Combine(dir.Trim(), binaryName);
            if (File.Exists(candidate)) return candidate;
        }

        var relatives = new[]
        {
            $"./{binaryName}",
            $"./sandbox/heypoul/{binaryName}",
            $"../sandbox/heypoul/{binaryName}",
        };
        foreach (var rel in relatives)
        {
            var full = Path.GetFullPath(rel);
            if (File.Exists(full)) return full;
        }

        // Extract embedded Windows binary
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("heypoul-win-amd64");
            if (stream != null)
            {
                var dest = Path.Combine(Path.GetTempPath(), "heypoul.exe");
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);

                // Only overwrite if newer/different size
                if (!File.Exists(dest) || new FileInfo(dest).Length != ms.Length)
                {
                    ms.Position = 0;
                    await using var f = File.Create(dest);
                    await ms.CopyToAsync(f);
                }

                return dest;
            }
        }

        return null;
    }
}
