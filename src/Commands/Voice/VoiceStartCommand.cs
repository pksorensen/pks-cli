using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly IModelRegistryService _models;

    public VoiceStartCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config, IAnsiConsole console, IModelRegistryService models)
    {
        _authService = authService;
        _config = config;
        _console = console;
        _models = models;
    }

    public class Settings : VoiceSettings
    {
        [CommandOption("--key|-k")]
        [Description("Key code for push-to-talk. Linux: /dev/input code (run sudo find-key). Windows: VK hex e.g. 0xA5 for right-alt. Default: 100 (Linux right-alt) / 165 (Windows right-alt)")]
        public uint? KeyCode { get; set; }

        [CommandOption("--language|-l")]
        [Description("Speech recognition language (default: from heypoul config.json, then da-DK)")]
        public string? Language { get; set; }

        [CommandOption("--inject")]
        [Description("Injection mode: text (default) or command (presses Enter after injection)")]
        public string? InjectMode { get; set; }

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

        // Load persisted heypoul config — used as defaults below if CLI flags aren't set.
        // The settings UI (heypoul --settings or `pks voice settings`) writes this file,
        // so once a user has picked a device/language/key it sticks across runs.
        var heypoulCfg = LoadHeypoulConfig();

        // Resolve transcription engine(s).
        //   - Inline (debug) mode: MultiSelectionPrompt so the user can pick 1+ engines for
        //     A/B comparison. Each PTT release runs every selected engine in parallel and
        //     prints labeled transcripts. No injection in multi-engine mode.
        //   - Daemon mode: single SelectionPrompt, saved to config.json so subsequent runs skip
        //     the prompt.
        var sttModels = await _models.ByCapabilityAsync("voice-stt");
        const string cloudLabel = "cloud (Azure Speech)";
        var choices = new List<string> { cloudLabel };
        choices.AddRange(sttModels.Select(m => m.Name));

        List<string> selectedEngines;
        if (settings.Inline && choices.Count > 1)
        {
            selectedEngines = _console.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title("[cyan]Pick engine(s) for inline comparison (space to toggle, enter to confirm):[/]")
                    .PageSize(10)
                    .NotRequired()
                    .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]")
                    .AddChoices(choices));
            if (selectedEngines.Count == 0)
            {
                _console.MarkupLine("[yellow]No engines selected — defaulting to cloud.[/]");
                selectedEngines = new List<string> { cloudLabel };
            }
            // Don't persist multi-select inline picks — they're a transient comparison choice.
        }
        else
        {
            string single;
            if (!string.IsNullOrEmpty(heypoulCfg.Engine))
            {
                single = heypoulCfg.Engine;
            }
            else if (sttModels.Count == 0)
            {
                single = "cloud";
            }
            else
            {
                var picked = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Pick a transcription engine:[/]")
                        .PageSize(10)
                        .AddChoices(choices));
                single = picked == cloudLabel ? "cloud" : picked;
                heypoulCfg.Engine = single;
                SaveHeypoulConfig(heypoulCfg);
            }
            selectedEngines = new List<string> { single == "cloud" ? cloudLabel : single };
        }

        // Normalise labels back to engine ids and resolve a model dir per non-cloud engine.
        var engineIds = selectedEngines.Select(s => s == cloudLabel ? "cloud" : s).ToList();
        var engineModelDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in engineIds.Where(e => e != "cloud"))
        {
            var model = sttModels.FirstOrDefault(m => string.Equals(m.Name, id, StringComparison.OrdinalIgnoreCase));
            if (model == null)
            {
                _console.MarkupLine($"[red]Engine [bold]{Markup.Escape(id)}[/] is selected but the model is no longer installed.[/]");
                _console.MarkupLine("[dim]Run [bold]pks model " + Markup.Escape(id) + " init[/] to install it, or [bold]pks voice settings[/] to switch back to cloud.[/]");
                return 1;
            }
            engineModelDirs[id] = model.InstallPath;
        }

        // For the single-engine path we still set the legacy env vars below.
        var engine = engineIds[0];
        var engineModelDir = engineModelDirs.TryGetValue(engine, out var dir) ? dir : null;

        // Platform-aware defaults: Windows right-alt = VK 0xA5 (165), Linux = 100.
        // Precedence: --key flag > config.json > platform default.
        var keyCode = settings.KeyCode
            ?? (heypoulCfg.KeyCode != 0 ? (uint)heypoulCfg.KeyCode : (uint?)null)
            ?? (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 165u : 100u);

        var language = settings.Language
            ?? (!string.IsNullOrEmpty(heypoulCfg.Language) ? heypoulCfg.Language : null)
            ?? "da-DK";

        var injectMode = settings.InjectMode
            ?? (!string.IsNullOrEmpty(heypoulCfg.InjectMode) ? heypoulCfg.InjectMode : null)
            ?? "text";

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

        // Pick a microphone. Skip the prompt if the user already saved a choice via the
        // settings window (config.json.deviceName) — re-prompting every voice start is
        // friction. They can change it from the tray icon or `pks voice settings`.
        string deviceName;
        if (!string.IsNullOrEmpty(heypoulCfg.DeviceName))
        {
            deviceName = heypoulCfg.DeviceName;
            _console.MarkupLine($"[dim]Using saved microphone:[/] [bold]{Markup.Escape(deviceName)}[/]");
            _console.MarkupLine("[dim]Change via tray → Settings… or [bold]pks voice settings[/].[/]");
        }
        else
        {
            deviceName = await PromptMicrophoneAsync(heypoulBin);
            if (!string.IsNullOrEmpty(deviceName))
            {
                heypoulCfg.DeviceName = deviceName;
                SaveHeypoulConfig(heypoulCfg);
            }
        }

        var commands = await PromptCommandMapAsync();
        var commandsJson = JsonSerializer.Serialize(commands);

        // Foundry LLM Speech REST API: https://<region>.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2025-10-15
        // This is the new MAI-Transcribe-1 / enhancedMode API that supports Danish.
        // Auth: Ocp-Apim-Subscription-Key (the resource subscription key from ARM listKeys).
        if (string.IsNullOrEmpty(creds.SelectedResourceLocation))
        {
            _console.MarkupLine("[red]Resource region not stored. Run [bold]pks foundry select[/] to refresh.[/]");
            return 1;
        }
        var region = creds.SelectedResourceLocation.ToLowerInvariant().Replace(" ", "");
        var endpoint = $"https://{region}.api.cognitive.microsoft.com";

        _console.WriteLine();
        _console.MarkupLine("[cyan]Starting heypoul[/]");
        _console.MarkupLine($"[dim]  endpoint: {Markup.Escape(endpoint)}[/]");
        var engineLabel = engineIds.Count > 1 ? string.Join(" + ", engineIds) : engine;
        _console.MarkupLine($"[dim]  language: {language}  key: {keyCode}  engine: {Markup.Escape(engineLabel)}[/]");
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
        if (!string.IsNullOrEmpty(deviceName))
            psi.Environment["HEYPOUL_DEVICE_NAME"] = deviceName;
        psi.Environment["HEYPOUL_TOKEN"] = token;
        // Pass subscription key — the Speech REST API prefers Ocp-Apim-Subscription-Key over Azure AD Bearer tokens
        if (!string.IsNullOrEmpty(creds.ApiKey))
            psi.Environment["HEYPOUL_API_KEY"] = creds.ApiKey;
        psi.Environment["HEYPOUL_LANGUAGE"] = language;
        psi.Environment["HEYPOUL_KEY"] = keyCode.ToString();
        psi.Environment["HEYPOUL_INJECT"] = injectMode;
        psi.Environment["HEYPOUL_ENGINE"] = engine;
        if (!string.IsNullOrEmpty(engineModelDir))
            psi.Environment["HEYPOUL_MODEL_DIR"] = engineModelDir;

        if (engineIds.Count > 1)
        {
            // Inline comparison: CSV list of engines + per-engine model-dir keys.
            // heypoul splits HEYPOUL_ENGINES on ',' and looks up HEYPOUL_MODEL_DIR_<id>
            // (e.g. HEYPOUL_MODEL_DIR_parakeet-v3) for each non-cloud engine.
            psi.Environment["HEYPOUL_ENGINES"] = string.Join(",", engineIds);
            foreach (var (id, modelDir) in engineModelDirs)
            {
                psi.Environment["HEYPOUL_MODEL_DIR_" + id] = modelDir;
            }
        }
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

        // Daemon mode: heypoul writes its own PID file to %TEMP%\heypoul.pid.
        // We just report the PID and return — pks voice off kills it later.
        _console.MarkupLine($"[dim]heypoul running (pid {proc.Id}) — [bold]pks voice off[/] to stop[/]");

        // Print log/pid/state paths so the user can `tail -f` the log to see transcripts as they arrive.
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var logPath = Path.Combine(tempDir, "heypoul.log");
        var pidPath = Path.Combine(tempDir, "heypoul.pid");
        var statePath = Path.Combine(tempDir, $"heypoul-{proc.Id}.state");
        _console.WriteLine();
        _console.MarkupLine($"[dim]log:   {Markup.Escape(logPath)}[/]");
        _console.MarkupLine($"[dim]pid:   {Markup.Escape(pidPath)}[/]");
        _console.MarkupLine($"[dim]state: {Markup.Escape(statePath)}[/]");
        _console.WriteLine();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _console.MarkupLine($"[dim]Watch transcripts:  [bold]Get-Content -Wait \"{Markup.Escape(logPath)}\"[/][/]");
        }
        else
        {
            _console.MarkupLine($"[dim]Watch transcripts:  [bold]tail -f \"{Markup.Escape(logPath)}\"[/][/]");
        }
        return 0;
    }

    private sealed class MicInfo
    {
        public string Name { get; set; } = "";
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// Calls `heypoul --list-mics` to enumerate input devices and lets the user pick one.
    /// Returns the chosen device name (passed to heypoul via HEYPOUL_DEVICE_NAME), or
    /// empty string if the user picks the system default.
    /// </summary>
    private async Task<string> PromptMicrophoneAsync(string heypoulBin)
    {
        try
        {
            var psi = new ProcessStartInfo(heypoulBin)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--list-mics");

            using var p = Process.Start(psi);
            if (p == null) return "";
            var json = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(json)) return "";

            var mics = JsonSerializer.Deserialize<List<MicInfo>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (mics == null || mics.Count == 0) return "";

            if (mics.Count == 1)
            {
                _console.MarkupLine($"[dim]Using only available microphone: [bold]{Markup.Escape(mics[0].Name)}[/][/]");
                return ""; // single device — let heypoul pick the system default
            }

            // Choice strings go through Spectre's markup parser, so square brackets in
            // them are treated as tags. Use a parenthesised marker instead of "[default]"
            // — otherwise SelectionPrompt throws "malformed markup tag" at runtime.
            const string defaultLabel = "(system default)";
            const string defaultSuffix = "  (default)";
            var choices = new List<string> { defaultLabel };
            choices.AddRange(mics.Select(m => m.IsDefault ? $"{m.Name}{defaultSuffix}" : m.Name));

            var picked = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Pick a microphone:[/]")
                    .PageSize(10)
                    .AddChoices(choices));

            if (picked == defaultLabel) return "";
            var idx = picked.IndexOf(defaultSuffix);
            return idx >= 0 ? picked[..idx] : picked;
        }
        catch (Exception ex)
        {
            // Surface the failure instead of silently skipping the prompt — otherwise
            // the user has no idea why the device picker dropped out and starts the
            // daemon with whatever the OS default mic is.
            _console.MarkupLine($"[yellow]Warning: could not enumerate microphones: {Markup.Escape(ex.Message)}[/]");
            _console.MarkupLine("[dim]Falling back to system default. Try [bold]pks voice settings[/] to pick a device manually.[/]");
            return "";
        }
    }

    internal sealed class HeypoulConfig
    {
        [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
        [JsonPropertyName("keyCode")] public ushort KeyCode { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("injectMode")] public string? InjectMode { get; set; }
        [JsonPropertyName("engine")] public string? Engine { get; set; }
    }

    // Path layout matches the Go side (config.go): on Windows that's
    // %APPDATA%\heypoul\config.json. On Linux the .NET SpecialFolder.ApplicationData
    // maps to ~/.config which matches Go's os.UserConfigDir() — so both processes
    // read/write the same file.
    private static string GetHeypoulConfigPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "heypoul", "config.json");
    }

    internal static HeypoulConfig LoadHeypoulConfig()
    {
        try
        {
            var path = GetHeypoulConfigPath();
            if (!File.Exists(path)) return new HeypoulConfig();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HeypoulConfig>(json) ?? new HeypoulConfig();
        }
        catch
        {
            return new HeypoulConfig();
        }
    }

    internal static void SaveHeypoulConfig(HeypoulConfig c)
    {
        try
        {
            var path = GetHeypoulConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Best-effort persistence — not having the device cached just means
            // the user gets prompted again next time, which is annoying but not fatal.
        }
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

                // Always overwrite — size-based caching missed code-only changes (same .exe size,
                // different bytes), so users got stuck running stale heypoul.exe binaries.
                ms.Position = 0;
                try
                {
                    await using var f = File.Create(dest);
                    await ms.CopyToAsync(f);
                }
                catch (IOException)
                {
                    // File is in use (a previous heypoul process is still running).
                    // Fall through and use whatever is on disk.
                }

                // sherpa-onnx DLLs need to sit alongside heypoul.exe — they're load-time
                // dependencies of the executable, so the OS loader looks for them in the
                // exe's directory at process start. If the resources aren't embedded
                // (built without parakeet support), this silently no-ops and the cloud
                // engine still works.
                foreach (var dllName in new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll" })
                {
                    await ExtractResourceIfPresent(dllName, Path.Combine(Path.GetTempPath(), dllName));
                }

                return dest;
            }
        }

        return null;
    }

    private static async Task ExtractResourceIfPresent(string resourceName, string destPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return;
        try
        {
            await using var f = File.Create(destPath);
            await stream.CopyToAsync(f);
        }
        catch (IOException)
        {
            // DLL already in use by a running heypoul.exe — reuse whatever is on disk.
        }
    }
}
