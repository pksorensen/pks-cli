using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Voice;

/// <summary>
/// Transcribes an audio or video file to text using heypoul + Azure AI Foundry Speech
/// (and optionally a local engine like parakeet-v3 in compare mode). Mirrors
/// <see cref="VoiceStartCommand"/>'s Foundry-creds handoff but runs heypoul in
/// file-transcribe mode instead of the push-to-talk daemon.
/// </summary>
[Description("Transcribe an audio/video file using heypoul + Azure AI Foundry Speech")]
public class VoiceTranscribeCommand : AsyncCommand<VoiceTranscribeCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public VoiceTranscribeCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config, IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<file>")]
        [Description("Path to audio or video file (mp4, m4a, wav, mp3, …) to transcribe")]
        public string File { get; set; } = "";

        [CommandOption("--out-dir|-o")]
        [Description("Output directory (default: <file-dir>/transcripts/<file-stem>)")]
        public string? OutDir { get; set; }

        [CommandOption("--engine")]
        [Description("Engine id: cloud (default) or parakeet-v3")]
        public string? Engine { get; set; }

        [CommandOption("--engines")]
        [Description("CSV of engines for compare mode, e.g. cloud,parakeet-v3 (overrides --engine)")]
        public string? Engines { get; set; }

        [CommandOption("--model-dir")]
        [Description("Model dir for non-cloud engines (default: ~/.pks-cli/models/<engine>)")]
        public string? ModelDir { get; set; }

        [CommandOption("--language|-l")]
        [Description("BCP-47 language tag (default: da-DK)")]
        public string? Language { get; set; }

        [CommandOption("--chunk-seconds")]
        [Description("Target chunk length in seconds (default: 60)")]
        public double? ChunkSeconds { get; set; }

        [CommandOption("--heypoul")]
        [Description("Path to heypoul binary (default: auto-detect or extract embedded)")]
        public string? HeypoulPath { get; set; }

        [CommandOption("--keep-wav")]
        [Description("Keep the extracted intermediate audio.wav (default: keep, since chunks reference it)")]
        public bool KeepWav { get; set; } = true;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!System.IO.File.Exists(settings.File))
        {
            _console.MarkupLine($"[red]File not found:[/] {Markup.Escape(settings.File)}");
            return 1;
        }
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

        // Speech REST endpoint. The Foundry resource's custom subdomain works for the
        // Speech transcribe API ({resource}.cognitiveservices.azure.com), and avoids
        // needing the region — which isn't always stored in the Foundry credentials.
        var endpoint = $"https://{creds.SelectedResourceName}.cognitiveservices.azure.com";

        var heypoulBin = await ResolveHeypoulPathAsync(settings.HeypoulPath);
        if (heypoulBin == null)
        {
            _console.MarkupLine("[red]heypoul binary not found.[/]");
            _console.MarkupLine("[dim]Build with projects/heypoul/build.sh, or build pks-cli with build-local.sh (embeds heypoul).[/]");
            return 1;
        }

        // Resolve output dir.
        var stem = Path.GetFileNameWithoutExtension(settings.File);
        var outDir = settings.OutDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.File))!, "transcripts", stem);
        Directory.CreateDirectory(outDir);

        // For .wav input, skip ffmpeg entirely — heypoul's WAV reader handles arbitrary
        // sample rate + mono/stereo on its own, so users on machines without ffmpeg
        // (typically Windows) can still transcribe by extracting the WAV elsewhere first.
        // For any other container (mp4/m4a/mp3/…), shell out to ffmpeg to decode.
        string wavPath;
        var ext = Path.GetExtension(settings.File).ToLowerInvariant();
        if (ext == ".wav")
        {
            wavPath = Path.GetFullPath(settings.File);
            _console.MarkupLine($"[cyan]Using WAV input directly[/] [dim]({Markup.Escape(wavPath)})[/]");
        }
        else
        {
            wavPath = Path.Combine(outDir, "audio.wav");
            _console.MarkupLine($"[cyan]Extracting audio with ffmpeg[/] [dim]→ {Markup.Escape(wavPath)}[/]");
            var ffmpegExit = await RunAsync("ffmpeg",
                new[] { "-y", "-i", settings.File, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath },
                quiet: true);
            if (ffmpegExit != 0)
            {
                _console.MarkupLine("[red]ffmpeg failed.[/] [dim]Install ffmpeg, or extract the audio to a WAV file yourself and pass that path instead.[/]");
                return ffmpegExit;
            }
        }

        // Build heypoul transcribe arg list.
        var engines = !string.IsNullOrWhiteSpace(settings.Engines)
            ? settings.Engines!
            : (settings.Engine ?? "cloud");
        var language = settings.Language ?? "da-DK";
        var chunk = (settings.ChunkSeconds ?? 60.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        var args = new List<string>
        {
            "transcribe",
            "--out-dir", outDir,
            "--language", language,
            "--chunk-seconds", chunk,
        };
        if (engines.Contains(','))
        {
            args.Add("--engines"); args.Add(engines);
        }
        else
        {
            args.Add("--engine"); args.Add(engines);
        }
        if (!string.IsNullOrEmpty(settings.ModelDir))
        {
            args.Add("--model-dir"); args.Add(settings.ModelDir);
        }
        args.Add(wavPath);

        _console.MarkupLine($"[cyan]Running heypoul transcribe[/] [dim]({engines}, {language}, {chunk}s chunks)[/]");

        var psi = new ProcessStartInfo(heypoulBin) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["HEYPOUL_ENDPOINT"] = endpoint;
        psi.Environment["HEYPOUL_TOKEN"] = token;
        if (!string.IsNullOrEmpty(creds.ApiKey))
            psi.Environment["HEYPOUL_API_KEY"] = creds.ApiKey;
        psi.Environment["HEYPOUL_LANGUAGE"] = language;

        // Per-engine model-dir env vars: heypoul picks these up when --engines is used.
        // Defaults to ~/.pks-cli/models/<engine> if not already set.
        foreach (var e in engines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (e == "cloud") continue;
            var envKey = "HEYPOUL_MODEL_DIR_" + e;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envKey)))
            {
                var defaultDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".pks-cli", "models", e);
                if (Directory.Exists(defaultDir))
                    psi.Environment[envKey] = defaultDir;
            }
        }

        // On Windows the embedded heypoul.exe is built with `-H windowsgui` (no console),
        // so its stderr/stdout writes have nowhere to land unless we explicitly capture them.
        // Capture both, forward to our own console live, and also tee to transcribe.log in
        // the output directory so the user can see what happened after the fact.
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var logPath = Path.Combine(outDir, "transcribe.log");
        await using var logFile = new StreamWriter(logPath, append: false);

        var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _console.WriteLine(e.Data);
            lock (logFile) logFile.WriteLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _console.WriteLine(e.Data);
            lock (logFile) logFile.WriteLine(e.Data);
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            _console.MarkupLine($"[red]heypoul exited with {proc.ExitCode}.[/] [dim]Full output saved to {Markup.Escape(logPath)}[/]");
            return proc.ExitCode;
        }

        if (!settings.KeepWav)
        {
            try { System.IO.File.Delete(wavPath); } catch { /* ignore */ }
        }

        _console.WriteLine();
        _console.MarkupLine($"[green]Done.[/] [dim]Output:[/] {Markup.Escape(outDir)}");
        foreach (var f in Directory.EnumerateFiles(outDir).OrderBy(f => f))
        {
            var rel = Path.GetFileName(f);
            if (rel.StartsWith("transcript-") || rel == "run.json")
                _console.MarkupLine($"  [dim]{Markup.Escape(rel)}[/]");
        }
        return 0;
    }

    private static async Task<int> RunAsync(string exe, IEnumerable<string> args, bool quiet)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = quiet, RedirectStandardError = quiet };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi);
        if (p == null) return 127;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    // Same heypoul resolution rules as VoiceStartCommand — see that class for the
    // priority order. Duplicated for now; both should move to a shared service
    // (IHeypoulResolver) in a follow-up.
    private async Task<string?> ResolveHeypoulPathAsync(string? userPath)
    {
        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "heypoul.exe" : "heypoul";
        if (!string.IsNullOrEmpty(userPath) && File.Exists(userPath)) return userPath;

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
                catch (IOException) { }
                foreach (var dllName in new[] { "sherpa-onnx-c-api.dll", "onnxruntime.dll" })
                {
                    using var dllStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(dllName);
                    if (dllStream == null) continue;
                    try
                    {
                        await using var df = File.Create(Path.Combine(Path.GetTempPath(), dllName));
                        await dllStream.CopyToAsync(df);
                    }
                    catch (IOException) { }
                }
                return dest;
            }
        }
        return null;
    }
}
