using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Codex;
using PKS.Infrastructure.Services.Agent.Foundry;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Codex;

/// <summary>
/// Sets up the genuine <c>codex</c> CLI to run against an Azure AI Foundry Responses deployment.
/// Writes an idempotent managed provider block into <c>~/.codex/config.toml</c> and records pks-side
/// launch defaults in <c>~/.pks-cli/codex.json</c>. Auth is injected at run time by the
/// <c>pks codex</c> passthrough, so nothing secret is written to disk here.
/// </summary>
[Description("Set up the real Codex CLI to run against Azure AI Foundry (writes ~/.codex/config.toml)")]
public sealed class CodexInitCommand : AsyncCommand<CodexSettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;

    public CodexInitCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _httpClientFactory = httpClientFactory;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, CodexSettings settings)
    {
        var port = settings.Port ?? 8788;
        var reasoningEffort = settings.ReasoningEffort ?? "medium";
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

        var deployment = CodexCliConfig.NormalizeDeploymentName(settings.Model)
            ?? (CodexCliConfig.LooksLikeCodex(creds.DefaultModel)
                ? CodexCliConfig.NormalizeDeploymentName(creds.DefaultModel)
                : null)
            ?? "gpt-5-codex";

        // Preflight the deployment so a wrong name / auth issue surfaces here, not deep inside codex.
        var client = _httpClientFactory.CreateClient("codex-passthrough");
        var preflightError = await FoundryResponsesEndpoint.PreflightAsync(
            client, creds, _authService, _config.AiFoundryScope, deployment, forceBearer: true);
        if (preflightError is not null)
        {
            _console.MarkupLine($"[red]Foundry preflight failed for deployment [bold]{deployment}[/]:[/]");
            _console.WriteLine(preflightError);
            _console.MarkupLine("[dim]Pass a valid deployment with [bold]--model <name>[/] (run [bold]pks foundry status[/] to check your resource).[/]");
            return 1;
        }

        CodexCliConfig.WriteProviderBlock(CodexCliConfig.BuildProxyProviderBlock(port));
        CodexCliConfig.SaveLaunchConfig(new CodexLaunchConfig
        {
            Deployment = deployment,
            Port = port,
            ReasoningEffort = reasoningEffort,
        });

        var mode = "passthrough with Entra auth";
        _console.MarkupLine($"[green]Codex CLI configured for Foundry[/] [dim](provider [bold]{CodexCliConfig.ProviderName}[/], default {deployment}, {mode})[/]");
        _console.MarkupLine($"[dim]Wrote managed block to [bold]{CodexCliConfig.ConfigTomlPath()}[/] (your other Codex config is untouched).[/]");
        _console.MarkupLine("[dim]Launch with [bold]pks codex[/] — it boots the auth passthrough and runs the real codex CLI.[/]");
        return 0;
    }
}
