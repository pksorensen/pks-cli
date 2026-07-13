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
/// no Anthropic translation. pks launches a thin loopback passthrough that injects Foundry auth
/// while forwarding Codex's Responses requests unchanged.
///
///   pks codex                         # launch codex on the default Foundry deployment
///   pks codex -m gpt-5.6-sol          # pick a deployment
///   pks codex --print-env             # run the passthrough + print the launch command instead
///   pks codex resume --last           # resume the latest Codex session through Foundry
/// </summary>
[Description("Run the real Codex CLI against a Codex/GPT deployment on Azure AI Foundry (native, no translation)")]
public sealed class CodexCommand : AsyncCommand<CodexSettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    private static readonly HashSet<string> NativeCodexSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive",
        "delete",
        "exec",
        "fork",
        "resume",
        "unarchive",
    };

    internal static IReadOnlyList<string>? CurrentArgv { get; set; }

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
        var deployment = CodexCliConfig.NormalizeDeploymentName(settings.Model)
            ?? (CodexCliConfig.LooksLikeCodex(creds.DefaultModel)
                ? CodexCliConfig.NormalizeDeploymentName(creds.DefaultModel)
                : null)
            ?? CodexCliConfig.NormalizeDeploymentName(launchCfg.Deployment)
            ?? "gpt-5-codex";
        var port = settings.Port ?? launchCfg.Port;
        var effort = settings.ReasoningEffort ?? launchCfg.ReasoningEffort;

        var proxyToken = Guid.NewGuid().ToString("N");
        var passthrough = await StartPassthroughAsync(creds, proxyToken, port);
        if (passthrough is null) return 1;

        try
        {
            var baseUrl = $"http://127.0.0.1:{passthrough.Port}/openai/v1";
            var existingToml = File.Exists(CodexCliConfig.ConfigTomlPath())
                ? File.ReadAllText(CodexCliConfig.ConfigTomlPath())
                : null;
            if (!CodexCliConfig.HasManagedBlockForBaseUrl(existingToml, baseUrl))
            {
                CodexCliConfig.WriteProviderBlock(CodexCliConfig.BuildProxyProviderBlock(passthrough.Port));
                _console.MarkupLine($"[dim]Wrote Foundry provider block to [bold]{CodexCliConfig.ConfigTomlPath()}[/] (proxy port {passthrough.Port}).[/]");
            }

            if (settings.PrintEnv)
            {
                Console.WriteLine($"export PKS_CODEX_TOKEN={proxyToken}");
                Console.WriteLine(BuildCodexCommandLine(deployment, effort, GetCodexArgs(context, settings), bypass: !settings.Safe));
                _console.MarkupLine($"[green]Codex passthrough live[/] [dim]→ {deployment} @ {passthrough.Port}[/]");
                _console.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
                var done = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
                await done.Task;
                return 0;
            }

            var bypass = !settings.Safe;
            _console.MarkupLine($"[green]Launching Codex on[/] [bold]{deployment}[/] [dim](Foundry passthrough, effort={effort}{(bypass ? ", bypass" : ", sandboxed")})[/]");
            _console.MarkupLine("[dim]Codex passthrough failures are logged to [bold]~/.pks-cli/codex-passthrough-failures.log[/].[/]");
            return await LaunchCodexAsync(
                deployment,
                effort,
                envName: "PKS_CODEX_TOKEN",
                envValue: proxyToken,
                codexArgs: GetCodexArgs(context, settings),
                bypass: bypass);
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
        var passthrough = new FoundryResponsesPassthrough(creds, _authService, _config.AiFoundryScope, proxyToken, desiredPort);
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

            var fallback = new FoundryResponsesPassthrough(creds, _authService, _config.AiFoundryScope, proxyToken, freePort);
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
    internal static readonly IReadOnlyList<string> FoundryCompatibilityFeatureDisables =
    [
        // Azure AI Foundry rejects OpenAI-internal encrypted tool namespaces such as
        // `collaboration`; keep pks codex on the public/client-executed tool path.
        "collaboration_modes",
        "apps",
        "multi_agent_v2",
        "multi_agent",
    ];

    private static IReadOnlyList<string> GetCodexArgs(CommandContext context, CodexSettings settings)
    {
        var rawNativeArgs = GetNativeArgsFromArgv(context.Name, CurrentArgv);
        if (rawNativeArgs is not null)
        {
            return rawNativeArgs;
        }

        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Name) && NativeCodexSubcommands.Contains(context.Name))
        {
            args.Add(context.Name);
        }

        args.AddRange(settings.Args);
        args.AddRange(context.Remaining.Raw);
        return args;
    }

    internal static IReadOnlyList<string>? GetNativeArgsFromArgv(string? commandName, IReadOnlyList<string>? argv)
    {
        if (string.IsNullOrWhiteSpace(commandName) ||
            !NativeCodexSubcommands.Contains(commandName) ||
            argv is null ||
            argv.Count == 0)
        {
            return null;
        }

        var codexIndex = IndexOf(argv, "codex", 0);
        if (codexIndex < 0) return null;

        var subcommandIndex = IndexOf(argv, commandName, codexIndex + 1);
        if (subcommandIndex < 0) return null;

        var args = new List<string> { commandName };
        for (var i = subcommandIndex + 1; i < argv.Count; i++)
        {
            args.Add(argv[i]);
        }

        return args;
    }

    private static int IndexOf(IReadOnlyList<string> values, string target, int start)
    {
        for (var i = start; i < values.Count; i++)
        {
            if (values[i].Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    internal static string BuildCodexCommandLine(string deployment, string effort, IReadOnlyList<string> codexArgs, bool bypass)
    {
        var parts = new List<string> { "codex" };
        var remainingCodexArgs = AddNativeSubcommandIfPresent(parts, codexArgs);
        parts.Add("-m");
        parts.Add(QuoteArg(deployment));
        parts.Add("-c");
        parts.Add(QuoteArg($"model_provider=\"{CodexCliConfig.ProviderName}\""));
        if (!string.IsNullOrEmpty(effort) && !effort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("-c");
            parts.Add(QuoteArg($"model_reasoning_effort=\"{effort}\""));
        }
        AddFoundryCompatibilityFeatureDisables(parts);
        if (bypass) parts.Add(BypassFlag);
        parts.AddRange(remainingCodexArgs.Select(QuoteArg));
        return string.Join(' ', parts);
    }

    private static IReadOnlyList<string> AddNativeSubcommandIfPresent(ICollection<string> args, IReadOnlyList<string> codexArgs)
    {
        if (codexArgs.Count > 0 && NativeCodexSubcommands.Contains(codexArgs[0]))
        {
            args.Add(codexArgs[0]);
            return codexArgs.Skip(1).ToArray();
        }

        return codexArgs;
    }

    private static void AddFoundryCompatibilityFeatureDisables(ICollection<string> args)
    {
        foreach (var feature in FoundryCompatibilityFeatureDisables)
        {
            args.Add("--disable");
            args.Add(feature);
        }
    }

    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        return arg.Any(char.IsWhiteSpace) || arg.Contains('"') || arg.Contains('\'')
            ? "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'"
            : arg;
    }

    private async Task<int> LaunchCodexAsync(
        string deployment,
        string effort,
        string envName,
        string envValue,
        IReadOnlyList<string> codexArgs,
        bool bypass)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "codex",
            UseShellExecute = false,
        };
        var remainingCodexArgs = AddNativeSubcommandIfPresent(psi.ArgumentList, codexArgs);

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(deployment);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"model_provider=\"{CodexCliConfig.ProviderName}\"");
        if (!string.IsNullOrEmpty(effort) && !effort.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"model_reasoning_effort=\"{effort}\"");
        }
        AddFoundryCompatibilityFeatureDisables(psi.ArgumentList);
        if (bypass) psi.ArgumentList.Add(BypassFlag);
        foreach (var arg in remainingCodexArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment[envName] = envValue;
        // Don't let codex pick up an ambient OpenAI key and bypass the selected Foundry provider.
        psi.Environment.Remove("OPENAI_API_KEY");
        if (!string.Equals(envName, CodexCliConfig.ApiKeyEnvVar, StringComparison.Ordinal))
        {
            psi.Environment.Remove(CodexCliConfig.ApiKeyEnvVar);
        }

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
