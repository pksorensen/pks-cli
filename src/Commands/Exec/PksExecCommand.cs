using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Exec;

/// <summary>
/// Generic exec wrapper. Discovers a child tool's capability manifest by
/// running it with PKS_DISCOVERY=1, prompts the user for provider/model
/// choices, starts a local managed-identity proxy if needed, resolves
/// placeholders in the manifest's env bindings, then exec's the tool
/// again with the composed env.
///
/// Manifest schema (v1): see internal/pksmanifest/manifest.go in
/// pks-agent-photographer for the canonical reference.
///
/// Placeholder vocabulary supported here:
///   {endpoint}        — registered provider's endpoint URL
///   {apikey}          — registered provider's API key (or prompted)
///   {imds:endpoint}   — local managed-identity proxy URL (this command starts it)
///   {imds:header}     — IMDS X-IDENTITY-HEADER secret
///   {model:&lt;role&gt;}    — user-selected model id for the named role
/// </summary>
[Description("Run a tool that supports the pks-cli discovery contract, with providers/models wired up automatically")]
public class PksExecCommand : AsyncCommand<PksExecCommand.Settings>
{
    private readonly IAzureFoundryAuthService _foundryAuth;
    private readonly AzureFoundryAuthConfig _foundryConfig;
    private readonly IAnsiConsole _console;

    public PksExecCommand(
        IAzureFoundryAuthService foundryAuth,
        AzureFoundryAuthConfig foundryConfig,
        IAnsiConsole console)
    {
        _foundryAuth = foundryAuth;
        _foundryConfig = foundryConfig;
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
            _console.MarkupLine("[dim]Usage: pks exec <tool> [args...][/]");
            return 2;
        }

        // 1. Discovery
        Manifest manifest;
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

        // 2. Resolve each capability into env-var overlays
        var envOverlay = new Dictionary<string, string>();
        ImdsProxy? imds = null;

        foreach (var cap in manifest.Capabilities)
        {
            if (!cap.Required)
            {
                if (!_console.Confirm($"Configure optional capability [yellow]{cap.Id}[/]?", false))
                {
                    continue;
                }
            }
            _console.MarkupLine($"\n[cyan]── {cap.Id}: {cap.Description.EscapeMarkup()} ──[/]");

            var provider = await ChooseProvider(cap, settings.Provider);
            if (provider == null)
            {
                _console.MarkupLine($"[red]no usable provider for {cap.Id.EscapeMarkup()}[/]");
                return 1;
            }

            var modelChoices = PromptModels(provider);
            var resolvedEnv = await ResolveEnv(provider, modelChoices, settings.Port, () =>
            {
                imds ??= ImdsProxy.Start(_foundryAuth, _foundryConfig, settings.Port);
                return imds;
            });

            foreach (var (k, v) in resolvedEnv)
            {
                envOverlay[k] = v;
            }
        }

        // 3. Exec
        return await ExecChild(settings, envOverlay, imds);
    }

    // ---------- discovery ----------

    private static async Task<Manifest> DiscoverManifest(string executable)
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
        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException("tool produced no manifest on stdout");
        }
        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
        {
            throw new InvalidOperationException("tool stdout has no JSON: " + Truncate(stdout, 200));
        }
        var json = stdout[jsonStart..];
        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOpts)
            ?? throw new InvalidOperationException("manifest decoded to null");
        if (manifest.ManifestVersion != "v1")
        {
            throw new InvalidOperationException($"unsupported manifestVersion={manifest.ManifestVersion} (this pks-cli speaks v1)");
        }
        return manifest;
    }

    // ---------- provider selection ----------

    private async Task<Provider?> ChooseProvider(Capability cap, string? preferred)
    {
        var available = new List<Provider>();
        foreach (var p in cap.Providers)
        {
            if (await IsAvailable(p.Kind))
            {
                available.Add(p);
            }
        }
        if (available.Count == 0)
        {
            _console.MarkupLine("[red]no registered provider matches this capability.[/]");
            _console.MarkupLine("[dim]register one via [bold]pks foundry init[/] (or set GEMINI_API_KEY / OPENAI_BASE_URL).[/]");
            return null;
        }
        if (!string.IsNullOrEmpty(preferred))
        {
            var match = available.FirstOrDefault(p => string.Equals(p.Kind, preferred, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                _console.MarkupLine($"[red]--provider {preferred.EscapeMarkup()} not registered or not offered by this tool.[/]");
                return null;
            }
            return match;
        }
        if (available.Count == 1)
        {
            _console.MarkupLine($"  using sole available provider: [green]{available[0].Kind.EscapeMarkup()}[/]");
            return available[0];
        }
        var choice = _console.Prompt(new SelectionPrompt<Provider>()
            .Title("  select provider:")
            .UseConverter(p => $"{p.Kind} — {p.Description}")
            .AddChoices(available));
        return choice;
    }

    private async Task<bool> IsAvailable(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "foundry" => await _foundryAuth.IsAuthenticatedAsync(),
            "gemini" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
            "openai-compatible" => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"))
                                 || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")),
            _ => false,
        };
    }

    // ---------- model selection ----------

    private Dictionary<string, string> PromptModels(Provider provider)
    {
        var choices = new Dictionary<string, string>();
        foreach (var model in provider.Models)
        {
            var defaultModel = SuggestDefaultModel(provider.Kind, model.Role);
            // Spectre treats [foo] as markup; use [[ ]] to render literal brackets.
            var role = model.Role.EscapeMarkup();
            var label = string.IsNullOrEmpty(model.Description)
                ? $"  model[[{role}]]:"
                : $"  model[[{role}]] ({model.Description.EscapeMarkup()}):";
            var v = _console.Prompt(new TextPrompt<string>(label)
                .DefaultValue(defaultModel)
                .ShowDefaultValue());
            choices[model.Role] = v;
        }
        return choices;
    }

    private string SuggestDefaultModel(string kind, string role)
    {
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                var stored = _foundryAuth.GetStoredCredentialsAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(stored?.DefaultModel))
                {
                    return stored.DefaultModel;
                }
                return role == "fast" ? "claude-sonnet-4-6" : "claude-opus-4-7";
            case "gemini":
                return role == "fast" ? "gemini-2.5-flash" : "gemini-2.5-pro";
            case "openai-compatible":
                return role == "fast" ? "gpt-4o-mini" : "gpt-4o";
            default:
                return "";
        }
    }

    // ---------- env resolution ----------

    private async Task<Dictionary<string, string>> ResolveEnv(
        Provider provider,
        IReadOnlyDictionary<string, string> modelChoices,
        int? imdsPort,
        Func<ImdsProxy> getImds)
    {
        var resolved = new Dictionary<string, string>();
        foreach (var (key, template) in provider.Env)
        {
            resolved[key] = await ExpandPlaceholders(template, provider.Kind, modelChoices, getImds);
        }
        return resolved;
    }

    private static readonly Regex PlaceholderRx = new(@"\{([a-z0-9:_\-]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async Task<string> ExpandPlaceholders(string template, string providerKind, IReadOnlyDictionary<string, string> modelChoices, Func<ImdsProxy> getImds)
    {
        if (!template.Contains('{'))
        {
            return template;
        }
        var matches = PlaceholderRx.Matches(template);
        var sb = new StringBuilder(template);
        // Replace from the end so indices stay valid.
        foreach (var m in matches.Reverse())
        {
            var name = m.Groups[1].Value;
            var value = await Resolve(name, providerKind, modelChoices, getImds);
            sb.Remove(m.Index, m.Length);
            sb.Insert(m.Index, value);
        }
        return sb.ToString();
    }

    private async Task<string> Resolve(string name, string providerKind, IReadOnlyDictionary<string, string> modelChoices, Func<ImdsProxy> getImds)
    {
        if (name.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
        {
            var role = name[6..];
            return modelChoices.TryGetValue(role, out var v) ? v : "";
        }
        switch (name.ToLowerInvariant())
        {
            case "imds:endpoint":
                return getImds().Endpoint;
            case "imds:header":
                return getImds().Header;
            case "endpoint":
                return await ResolveEndpoint(providerKind);
            case "apikey":
                return ResolveApiKey(providerKind);
        }
        return "";
    }

    private async Task<string> ResolveEndpoint(string kind)
    {
        switch (kind.ToLowerInvariant())
        {
            case "foundry":
                var stored = await _foundryAuth.GetStoredCredentialsAsync();
                return stored?.SelectedResourceEndpoint ?? "";
            case "openai-compatible":
                return Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "";
            default:
                return "";
        }
    }

    private static string ResolveApiKey(string kind)
    {
        return kind.ToLowerInvariant() switch
        {
            "gemini" => Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "",
            "openai-compatible" => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "",
            _ => "",
        };
    }

    // ---------- exec ----------

    private async Task<int> ExecChild(Settings settings, Dictionary<string, string> envOverlay, ImdsProxy? imds)
    {
        if (settings.DryRun)
        {
            _console.MarkupLine("\n[yellow]--dry-run, would exec:[/]");
            _console.MarkupLine($"  {settings.Executable.EscapeMarkup()} {string.Join(" ", settings.Args).EscapeMarkup()}");
            _console.MarkupLine("[yellow]with env overlay:[/]");
            foreach (var (k, v) in envOverlay.OrderBy(kv => kv.Key))
            {
                var shown = IsSecret(k) ? "(set, hidden)" : v;
                _console.MarkupLine($"  {k.EscapeMarkup()} = {shown.EscapeMarkup()}");
            }
            imds?.Stop();
            return 0;
        }

        var psi = new ProcessStartInfo
        {
            FileName = settings.Executable,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var a in settings.Args)
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (k, v) in envOverlay)
        {
            psi.Environment[k] = v;
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]failed to start: {ex.Message.EscapeMarkup()}[/]");
            imds?.Stop();
            return 127;
        }
        if (proc == null)
        {
            imds?.Stop();
            return 127;
        }
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { proc.Kill(entireProcessTree: true); } catch { }
        };
        await proc.WaitForExitAsync();
        var exit = proc.ExitCode;
        imds?.Stop();
        return exit;
    }

    private static bool IsSecret(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("token") || lower.Contains("key") || lower.Contains("header") || lower.Contains("password") || lower.Contains("secret");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------- JSON ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public class Manifest
    {
        public string ManifestVersion { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public List<Capability> Capabilities { get; set; } = new();
    }

    public class Capability
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Required { get; set; }
        public List<Provider> Providers { get; set; } = new();
    }

    public class Provider
    {
        public string Kind { get; set; } = "";
        public string Description { get; set; } = "";
        public List<Model> Models { get; set; } = new();
        public Dictionary<string, string> Env { get; set; } = new();
    }

    public class Model
    {
        public string Role { get; set; } = "";
        public string Description { get; set; } = "";
        public string Hint { get; set; } = "";
    }
}

// ---------- IMDS proxy ----------

internal sealed class ImdsProxy
{
    public string Endpoint { get; }
    public string Header { get; }

    private readonly WebApplication _app;
    private readonly CancellationTokenSource _cts;
    private readonly Task _runTask;

    private ImdsProxy(WebApplication app, CancellationTokenSource cts, string endpoint, string header, Task runTask)
    {
        _app = app;
        _cts = cts;
        Endpoint = endpoint;
        Header = header;
        _runTask = runTask;
    }

    public static ImdsProxy Start(IAzureFoundryAuthService auth, AzureFoundryAuthConfig cfg, int? portHint)
    {
        var port = portHint ?? FindFreePort();
        var headerSecret = Guid.NewGuid().ToString("N");

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapGet("/", async (HttpContext ctx) =>
        {
            var headerVal = ctx.Request.Headers["X-IDENTITY-HEADER"].ToString();
            if (!string.IsNullOrEmpty(headerVal) && headerVal != headerSecret)
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("forbidden");
                return;
            }
            var resource = ctx.Request.Query["resource"].ToString();
            var scope = string.IsNullOrEmpty(resource) ? cfg.CognitiveScope : NormaliseScope(resource);
            string? token;
            try
            {
                token = await auth.GetAccessTokenAsync(scope);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("token error: " + ex.Message);
                return;
            }
            if (string.IsNullOrEmpty(token))
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("token unavailable");
                return;
            }
            var resp = new
            {
                access_token = token,
                expires_on = DateTimeOffset.UtcNow.AddMinutes(50).ToUnixTimeSeconds(),
                resource = string.IsNullOrEmpty(resource) ? "https://cognitiveservices.azure.com" : resource,
                token_type = "Bearer",
            };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(resp));
        });

        var cts = new CancellationTokenSource();
        var task = app.RunAsync(cts.Token);

        WaitForListen(port, TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();

        return new ImdsProxy(app, cts, $"http://localhost:{port}/", headerSecret, task);
    }

    private static string NormaliseScope(string resourceQuery)
    {
        var s = resourceQuery.TrimEnd('/');
        return s.EndsWith("/.default", StringComparison.OrdinalIgnoreCase) ? s : s + "/.default";
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _runTask.GetAwaiter().GetResult(); } catch { }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForListen(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(25);
            }
        }
    }
}
