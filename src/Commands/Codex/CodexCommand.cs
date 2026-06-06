using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

/// <summary>
/// Runs the genuine <c>codex</c> CLI against an Azure AI Foundry Responses deployment — natively, with
/// no Anthropic translation. pks boots a thin loopback passthrough (<see cref="FoundryResponsesPassthrough"/>)
/// that injects fresh Foundry auth per request, then launches <c>codex</c> selecting the Foundry provider
/// for this run only (<c>-c model_provider=pks-foundry</c>), leaving the user's global Codex defaults intact.
///
///   pks codex                         # launch codex on the default Foundry deployment
///   pks codex -m gpt-5-codex          # pick a deployment
///   pks codex --print-env             # run the passthrough + print the launch command instead
/// </summary>
[Description("Run the real Codex CLI against a Codex / GPT-5.x model on Azure AI Foundry (native, no translation)")]
public sealed class CodexCommand : AsyncCommand<CodexSettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public CodexCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, CodexSettings settings)
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        var creds = await _authService.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceEndpoint))
        {
            _console.MarkupLine("[red]No Foundry endpoint configured — run [bold]pks foundry init[/].[/]");
            return 1;
        }

        var launchCfg = CodexCliConfig.LoadLaunchConfig();
        var deployment = settings.Model
            ?? (CodexCliConfig.LooksLikeCodex(creds.DefaultModel) ? creds.DefaultModel : null)
            ?? launchCfg.Deployment;
        var port = settings.Port ?? launchCfg.Port;
        var effort = settings.ReasoningEffort ?? launchCfg.ReasoningEffort;

        var proxyToken = Guid.NewGuid().ToString("N");
        var passthrough = await StartPassthroughAsync(creds, proxyToken, port);
        if (passthrough is null) return 1;

        try
        {
            // Make the managed provider block match the actually-bound port (auto-init / fallback / change).
            var existingToml = File.Exists(CodexCliConfig.ConfigTomlPath())
                ? File.ReadAllText(CodexCliConfig.ConfigTomlPath())
                : null;
            if (!CodexCliConfig.HasManagedBlockForPort(existingToml, passthrough.Port))
            {
                CodexCliConfig.WriteProviderBlock(passthrough.Port);
                _console.MarkupLine($"[dim]Wrote Foundry provider block to [bold]{CodexCliConfig.ConfigTomlPath()}[/] (port {passthrough.Port}).[/]");
            }

            if (settings.PrintEnv)
            {
                Console.WriteLine($"export PKS_CODEX_TOKEN={proxyToken}");
                Console.WriteLine(BuildCodexCommandLine(deployment, effort, bypass: !settings.Safe));
                _console.MarkupLine($"[green]Codex passthrough live[/] [dim]→ {deployment} @ {passthrough.Port}[/]");
                _console.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                await done.Task;
                return 0;
            }

            var bypass = !settings.Safe;
            _console.MarkupLine($"[green]Launching Codex on[/] [bold]{deployment}[/] [dim](Foundry passthrough, effort={effort}{(bypass ? ", bypass" : ", sandboxed")})[/]");
            return await LaunchCodexAsync(deployment, effort, proxyToken, bypass);
        }
        finally
        {
            await passthrough.StopAsync();
        }
    }

    /// <summary>
    /// Starts the passthrough on <paramref name="desiredPort"/>, falling back to a free port if it's
    /// already in use (e.g. a stale instance). Returns null if even the fallback fails to bind.
    /// </summary>
    private async Task<FoundryResponsesPassthrough?> StartPassthroughAsync(
        FoundryStoredCredentials creds, string proxyToken, int desiredPort)
    {
        var passthrough = new FoundryResponsesPassthrough(creds, _authService, _config.CognitiveScope, proxyToken, desiredPort);
        try
        {
            await passthrough.StartAsync();
            return passthrough;
        }
        catch (IOException)
        {
            await passthrough.StopAsync();
            var freePort = AnthropicProxyUtil.FindFreePort();
            _console.MarkupLine($"[yellow]Port {desiredPort} is already in use — using {freePort} for this run.[/]");

            var fallback = new FoundryResponsesPassthrough(creds, _authService, _config.CognitiveScope, proxyToken, freePort);
            try
            {
                await fallback.StartAsync();
                return fallback;
            }
            catch (Exception ex)
            {
                await fallback.StopAsync();
                _console.MarkupLine($"[red]Could not start the Foundry passthrough: {ex.Message}[/]");
                return null;
            }
        }
    }

    private const string BypassFlag = "--dangerously-bypass-approvals-and-sandbox";

    private static string BuildCodexCommandLine(string deployment, string effort, bool bypass)
    {
        var parts = new List<string> { "codex", "-m", deployment, "-c", $"model_provider=\"{CodexCliConfig.ProviderName}\"" };
        if (!string.IsNullOrEmpty(effort) && !effort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("-c");
            parts.Add($"model_reasoning_effort=\"{effort}\"");
        }
        if (bypass) parts.Add(BypassFlag);
        return string.Join(' ', parts);
    }

    private async Task<int> LaunchCodexAsync(string deployment, string effort, string proxyToken, bool bypass)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "codex",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(deployment);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"model_provider=\"{CodexCliConfig.ProviderName}\"");
        if (!string.IsNullOrEmpty(effort) && !effort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"model_reasoning_effort=\"{effort}\"");
        }
        if (bypass) psi.ArgumentList.Add(BypassFlag);

        psi.Environment["PKS_CODEX_TOKEN"] = proxyToken;
        // Don't let codex pick up an ambient OpenAI key and bypass the passthrough.
        psi.Environment.Remove("OPENAI_API_KEY");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _console.MarkupLine("[red]Failed to start the codex CLI.[/]");
                return 1;
            }
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _console.MarkupLine("[red]Could not find the [bold]codex[/] CLI on PATH.[/]");
            _console.MarkupLine("[dim]Install it ([bold]npm i -g @openai/codex[/]), or run with [bold]--print-env[/] and launch it yourself.[/]");
            return 127;
        }
    }
}
