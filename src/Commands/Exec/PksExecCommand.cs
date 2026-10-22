using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services.Exec;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Exec;

/// <summary>
/// Generic exec wrapper. Discovers a child tool's capability manifest by running it with
/// PKS_DISCOVERY=1, resolves provider and model choices against whatever this machine is signed in to,
/// then exec's the tool again with the composed environment. The tool ships a manifest and never a
/// token — see <c>.pks/brain/feature-specs/FT-010-exec-protocol-tool-ai-access.md</c>.
///
/// Manifest schema (v1): see internal/pksmanifest/manifest.go in pks-agent-photographer for the
/// canonical reference, and <see cref="PksManifest"/> for this side of it.
///
/// Placeholder vocabulary:
///   {endpoint}        — registered provider's endpoint URL
///   {endpoint:openai} — the same, in the shape an OpenAI client can be pointed at
///   {apikey}          — registered provider's API key
///   {imds:endpoint}   — local managed-identity proxy URL (the resolver starts it)
///   {imds:header}     — IMDS X-IDENTITY-HEADER secret
///   {proxy:endpoint}  — http://127.0.0.1:&lt;port&gt; of a local credential proxy in front of the provider's
///                       endpoint (foundry: an Entra token per request; openai-compatible: OPENAI_API_KEY).
///                       Proxies HTTP and WebSocket upgrades; the resolver starts it and Release() stops it
///   {proxy:token}     — the throwaway bearer token that proxy accepts, per run; the child never sees
///                       the real credential
///   {model:&lt;role&gt;}    — user-selected model id for the named role
/// </summary>
[Description("Run a tool that supports the pks-cli discovery contract, with providers/models wired up automatically")]
public class PksExecCommand : AsyncCommand<PksExecCommand.Settings>
{
    private readonly IManifestResolver _resolver;
    private readonly IAnsiConsole _console;

    public PksExecCommand(IManifestResolver resolver, IAnsiConsole console)
    {
        _resolver = resolver;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<EXECUTABLE>")]
        [Description("Path to the tool to run")]
        public string Executable { get; set; } = string.Empty;

        [CommandArgument(1, "[ARGS]")]
        [Description("Arguments passed verbatim to the tool")]
        public string[] Args { get; set; } = Array.Empty<string>();

        [CommandOption("--provider <KIND>")]
        [Description("Skip the provider prompt and pick this kind directly (e.g. foundry, gemini, openai-compatible)")]
        public string? Provider { get; set; }

        [CommandOption("--port <N>")]
        [Description("Bind the managed-identity proxy to this port (default: random)")]
        public int? Port { get; set; }

        [CommandOption("--dry-run")]
        [Description("Print the resolved env and command but do not exec")]
        public bool DryRun { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Executable))
        {
            _console.MarkupLine("[red]Missing EXECUTABLE.[/]");
            _console.MarkupLine("[dim]Usage: pks exec <tool> [[args...]][/]");
            return 2;
        }

        // Options meant for the tool have to come after `--`: Spectre would otherwise treat an
        // option it does not know as a remaining parsed option and drop it, and the tool would
        // silently run with its defaults. Positional ARGS before `--` still pass through.
        // Spectre also files options after `--` under Parsed, so only refuse when there is no `--`.
        if (context.Remaining.Parsed.Count > 0 && context.Remaining.Raw.Count == 0)
        {
            var unknown = string.Join(" ", context.Remaining.Parsed.Select(g => g.Key));
            _console.MarkupLine($"[red]unknown option(s) for pks exec: {unknown.EscapeMarkup()}[/]");
            _console.MarkupLine("[dim]Pass the tool's own options after --, e.g. pks exec heypoul -- meeting --no-me[/]");
            return 2;
        }
        var childArgs = settings.Args.Concat(context.Remaining.Raw).ToArray();

        PksManifest manifest;
        try
        {
            manifest = await DiscoverManifest(settings.Executable);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]discovery failed for {settings.Executable.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
            _console.MarkupLine("[dim]The tool must emit a JSON manifest to stdout when invoked with PKS_DISCOVERY=1.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]discovered:[/] {manifest.Name.EscapeMarkup()} v{manifest.Version.EscapeMarkup()}");

        var resolved = await _resolver.ResolveAsync(manifest, new ManifestResolveOptions
        {
            PreferredProvider = settings.Provider,
            ImdsPort = settings.Port,
        });

        if (resolved is null)
        {
            _resolver.Release();
            return 1;
        }

        try
        {
            return await ExecChild(settings, childArgs, resolved);
        }
        finally
        {
            _resolver.Release();
        }
    }

    // ---------- discovery ----------

    private static async Task<PksManifest> DiscoverManifest(string executable)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["PKS_DISCOVERY"] = "1";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("manifest discovery timed out after 10s");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"tool exited {proc.ExitCode}: {stderr.Trim()}");
        }
        return PksManifest.Parse(stdout);
    }

    // ---------- exec ----------

    private async Task<int> ExecChild(Settings settings, IReadOnlyList<string> childArgs, ResolvedEnvironment environment)
    {
        if (settings.DryRun)
        {
            _console.MarkupLine("\n[yellow]--dry-run, would exec:[/]");
            _console.MarkupLine($"  {settings.Executable.EscapeMarkup()} {string.Join(" ", childArgs).EscapeMarkup()}");
            _console.MarkupLine("[yellow]with env overlay:[/]");
            foreach (var (name, display) in environment.Describe())
            {
                _console.MarkupLine($"  {name.EscapeMarkup()} = {display.EscapeMarkup()}");
            }
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = settings.Executable,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var a in childArgs)
        {
            psi.ArgumentList.Add(a);
        }
        environment.ApplyTo(psi);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]failed to start: {ex.Message.EscapeMarkup()}[/]");
            return 127;
        }
        if (proc == null)
        {
            return 127;
        }
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
        };
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
