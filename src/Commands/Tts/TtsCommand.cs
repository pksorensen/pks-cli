using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Tts;

/// <summary>
/// Generate speech audio from text using Azure AI Foundry TTS.
/// Mirrors the pks image command pattern: positional text arg, optional output path,
/// prints the output file path to stdout for agent piping.
/// </summary>
[Description("Generate speech audio from text using Azure AI Foundry TTS")]
public class TtsCommand : AsyncCommand<TtsCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;
    private readonly IHttpClientFactory _httpClientFactory;

    public TtsCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console,
        IHttpClientFactory httpClientFactory)
    {
        _authService = authService;
        _config = config;
        _console = console;
        _httpClientFactory = httpClientFactory;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[text]")]
        [Description("The text to synthesize into speech")]
        public string? Text { get; set; }

        [CommandOption("--text-file")]
        [Description("Path to a text file containing the input (alternative to positional argument)")]
        public string? TextFile { get; set; }

        [CommandOption("--ssml-file")]
        [Description("Path to an SSML file. Switches to Azure Speech neural-voice synthesis (supports da-DK-ChristelNeural, da-DK-JeppeNeural, multi-voice dialog, prosody, breaks). Voice/model options are ignored — voices come from the SSML.")]
        public string? SsmlFile { get; set; }

        [CommandOption("--format")]
        [Description("SSML mode only: X-Microsoft-OutputFormat header value (default: audio-24khz-160kbitrate-mono-mp3)")]
        public string SsmlFormat { get; set; } = "audio-24khz-160kbitrate-mono-mp3";

        [CommandOption("--video")]
        [Description("Also render an MP4 with an audio-reactive visualization (LinkedIn-ready). Requires ffmpeg on PATH or PKS_FFMPEG_BIN env var pointing at the binary.")]
        public bool Video { get; set; }

        [CommandOption("--video-size")]
        [Description("Video resolution (default: 1080x1080). Use 1080x1920 for vertical.")]
        public string VideoSize { get; set; } = "1080x1080";

        [CommandOption("--video-style")]
        [Description("Visualization style: waves | volume | cqt | pulse | scope (default: waves). 'pulse' draws a centered circle with a sharp waveform behind it; 'scope' is an organic moving line.")]
        public string VideoStyle { get; set; } = "waves";

        [CommandOption("--voice|-v")]
        [Description("Voice name: alloy, echo, fable, onyx, nova, shimmer (default: alloy)")]
        public string Voice { get; set; } = "alloy";

        [CommandOption("--model|-m")]
        [Description("TTS model name (default: tts-hd)")]
        public string Model { get; set; } = "tts-hd";

        [CommandOption("--deployment|-d")]
        [Description("Foundry deployment name (default: from stored credentials)")]
        public string? Deployment { get; set; }

        [CommandOption("--output|-o")]
        [Description("Output file path (default: speech-<timestamp>.mp3)")]
        public string? Output { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        // SSML path branches to Azure Speech neural-voice synthesis. Different endpoint,
        // different body (raw XML, not JSON), different output-format header. Voices and
        // prosody come from the SSML itself, so --voice/--model are ignored here.
        if (!string.IsNullOrEmpty(settings.SsmlFile))
        {
            return await ExecuteSsmlAsync(settings);
        }

        // Resolve input text
        string? text = null;
        if (!string.IsNullOrEmpty(settings.Text))
        {
            text = settings.Text;
        }
        else if (!string.IsNullOrEmpty(settings.TextFile))
        {
            if (!File.Exists(settings.TextFile))
            {
                _console.MarkupLine($"[red]Text file not found:[/] {Markup.Escape(settings.TextFile)}");
                return 1;
            }
            text = await File.ReadAllTextAsync(settings.TextFile);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _console.MarkupLine("[red]Provide text via argument, --text-file, or --ssml-file.[/]");
            _console.MarkupLine("[dim]Example: pks tts \"Hello world\"[/]");
            _console.MarkupLine("[dim]Example: pks tts --text-file script.txt --voice nova --output speech.mp3[/]");
            _console.MarkupLine("[dim]Example: pks tts --ssml-file dialog-da.xml --output spike-dialog-da.mp3[/]");
            return 1;
        }

        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceName))
        {
            _console.MarkupLine("[red]No Foundry resource configured.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] to select a resource.[/]");
            return 1;
        }

        // TTS uses the cognitiveservices.azure.com endpoint, not the newer services.ai.azure.com one.
        // Default deployment is tts-hd (not the chat DefaultModel stored in credentials).
        var deployment = settings.Deployment ?? "tts-hd";
        var cognitiveEndpoint = $"https://{creds.SelectedResourceName}.cognitiveservices.azure.com";
        var endpoint = cognitiveEndpoint +
                       $"/openai/deployments/{deployment}/audio/speech?api-version=2025-03-01-preview";

        // Acquire real Azure token
        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to acquire Azure access token. Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        // Resolve output path
        var outputPath = settings.Output
            ?? $"speech-{DateTime.Now:yyyyMMdd-HHmmss}.mp3";
        outputPath = Path.GetFullPath(outputPath);

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Call TTS API
        byte[]? audioBytes = null;
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model = settings.Model,
                input = text,
                voice = settings.Voice,
            });

            audioBytes = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Generating speech with [bold]{Markup.Escape(deployment)}[/] ({Markup.Escape(settings.Voice)})...", async _ =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                    var client = _httpClientFactory.CreateClient("foundry-tts");
                    var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync();
                });
        }
        catch (HttpRequestException ex)
        {
            _console.MarkupLine($"[red]API error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        await File.WriteAllBytesAsync(outputPath, audioBytes);

        // Print absolute path to stdout for agent piping (same as pks image)
        Console.WriteLine(outputPath);

        _console.MarkupLine($"[green]Speech saved:[/] {Markup.Escape(outputPath)} [dim]({audioBytes.Length / 1024} KB, voice: {Markup.Escape(settings.Voice)})[/]");

        if (settings.Video)
        {
            var rc = await RenderVideoAsync(outputPath, settings);
            if (rc != 0) return rc;
        }

        return 0;
    }

    private async Task<int> ExecuteSsmlAsync(Settings settings)
    {
        if (!File.Exists(settings.SsmlFile!))
        {
            _console.MarkupLine($"[red]SSML file not found:[/] {Markup.Escape(settings.SsmlFile!)}");
            return 1;
        }
        var ssml = await File.ReadAllTextAsync(settings.SsmlFile!);

        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceName))
        {
            _console.MarkupLine("[red]No Foundry resource configured.[/] Run [bold]pks foundry init[/].");
            return 1;
        }

        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to acquire Azure access token.[/] Try [bold]pks foundry init --force[/].");
            return 1;
        }

        // Custom-subdomain Speech endpoint accepts AAD bearer auth on the /tts/ prefix.
        // Equivalent to the brief's https://{region}.tts.speech.microsoft.com/cognitiveservices/v1
        // but without needing SPEECH_KEY/REGION.
        var endpoint = $"https://{creds.SelectedResourceName}.cognitiveservices.azure.com/tts/cognitiveservices/v1";

        var outputPath = Path.GetFullPath(settings.Output
            ?? $"speech-ssml-{DateTime.Now:yyyyMMdd-HHmmss}.mp3");
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        byte[] audioBytes;
        var started = DateTime.UtcNow;
        try
        {
            audioBytes = await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Synthesizing SSML via Azure Speech [dim]({Markup.Escape(settings.SsmlFormat)})[/]...", async _ =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                    {
                        Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", settings.SsmlFormat);
                    request.Headers.UserAgent.ParseAdd("pks-cli-tts-ssml");

                    var client = _httpClientFactory.CreateClient("foundry-tts");
                    var response = await client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        // Azure returns plain-text SSML validation errors in the body.
                        var errBody = await response.Content.ReadAsStringAsync();
                        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {errBody}");
                    }
                    return await response.Content.ReadAsByteArrayAsync();
                });
        }
        catch (HttpRequestException ex)
        {
            _console.MarkupLine($"[red]Speech synthesis failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var elapsed = DateTime.UtcNow - started;
        await File.WriteAllBytesAsync(outputPath, audioBytes);

        Console.WriteLine(outputPath);
        _console.MarkupLine($"[green]SSML synth saved:[/] {Markup.Escape(outputPath)} [dim]({audioBytes.Length / 1024} KB, {elapsed.TotalSeconds:0.0}s)[/]");

        if (settings.Video)
        {
            var rc = await RenderVideoAsync(outputPath, settings);
            if (rc != 0) return rc;
        }
        return 0;
    }

    private async Task<int> RenderVideoAsync(string audioPath, Settings settings)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("PKS_FFMPEG_BIN");
        if (string.IsNullOrWhiteSpace(ffmpeg)) ffmpeg = "ffmpeg";

        var videoPath = Path.ChangeExtension(audioPath, ".mp4");

        // Size parsing for centered overlay offsets.
        var size = settings.VideoSize;
        var parts = size.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
        {
            _console.MarkupLine($"[red]Invalid --video-size:[/] {Markup.Escape(size)} (expected WxH like 1080x1080)");
            return 1;
        }

        // Centered circle is drawn with geq — radius is ~22% of the shorter side.
        // Used by the 'pulse' style to put a solid white disc behind the waveform.
        int r = (int)(Math.Min(w, h) * 0.22);
        string circleGeq =
            $"color=s={size}:c=0x0a0a0a,format=rgba," +
            $"geq=r='if(lt(hypot(X-{w/2},Y-{h/2}),{r}),255,r(X,Y))':" +
            $"g='if(lt(hypot(X-{w/2},Y-{h/2}),{r}),255,g(X,Y))':" +
            $"b='if(lt(hypot(X-{w/2},Y-{h/2}),{r}),255,b(X,Y))':" +
            $"a='if(lt(hypot(X-{w/2},Y-{h/2}),{r}),255,a(X,Y))'";

        string filter = settings.VideoStyle.ToLowerInvariant() switch
        {
            "volume" =>
                $"color=s={size}:c=0x0a0a0a[bg];" +
                $"[0:a]showvolume=w={w}:h=60:b=2:f=0.5:c=0xffffffff,format=yuva420p[v0];" +
                $"[bg][v0]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]",
            "cqt" =>
                $"color=s={size}:c=0x0a0a0a[bg];" +
                $"[0:a]showcqt=s={w}x{Math.Max(240, h / 3)}:count=2,format=yuva420p[v0];" +
                $"[bg][v0]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]",
            "scope" =>
                // Fake stereo from mono so avectorscope has something to draw.
                $"color=s={size}:c=0x0a0a0a[bg];" +
                $"[0:a]aformat=channel_layouts=stereo|mono,pan=stereo|c0=c0|c1=c0," +
                $"avectorscope=s={Math.Min(w,h)}x{Math.Min(w,h)}:zoom=1.5:draw=line:scale=lin:rc=255:gc=255:bc=255:rf=15:gf=15:bf=15,format=yuva420p[v0];" +
                $"[bg][v0]overlay=(W-w)/2:(H-h)/2:shortest=1,format=yuv420p[v]",
            "pulse" =>
                // Sharper p2p waveform + static centered white disc.
                $"{circleGeq}[bg];" +
                $"[0:a]showwaves=s={w}x{Math.Max(300, h/3)}:mode=p2p:colors=0xffffff:rate=30:draw=full,format=yuva420p[v0];" +
                $"[bg][v0]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]",
            _ =>  // waves (default) — sharper than the old cline
                $"color=s={size}:c=0x0a0a0a[bg];" +
                $"[0:a]showwaves=s={w}x{Math.Max(300, h/3)}:mode=p2p:colors=0xffffff:rate=30:draw=full,format=yuva420p[v0];" +
                $"[bg][v0]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]",
        };

        var args = new List<string>
        {
            "-y",
            "-i", audioPath,
            "-filter_complex", filter,
            "-map", "[v]",
            "-map", "0:a",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", "20",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "192k",
            "-movflags", "+faststart",
            videoPath,
        };

        _console.MarkupLine($"[cyan]Rendering video[/] [dim]({Markup.Escape(settings.VideoStyle)}, {Markup.Escape(size)}) via {Markup.Escape(ffmpeg)}[/]");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0)
            {
                var stderr = await stderrTask;
                _console.MarkupLine($"[red]ffmpeg exited {proc.ExitCode}[/]");
                _console.WriteLine(stderr);
                return proc.ExitCode;
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _console.MarkupLine($"[red]Could not launch ffmpeg ({Markup.Escape(ffmpeg)}):[/] {Markup.Escape(ex.Message)}");
            _console.MarkupLine("[dim]Set PKS_FFMPEG_BIN to the full path of ffmpeg.exe, or add ffmpeg to PATH.[/]");
            return 1;
        }

        Console.WriteLine(videoPath);
        _console.MarkupLine($"[green]Video saved:[/] {Markup.Escape(videoPath)}");
        return 0;
    }
}
