using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

/// <summary>
/// Spawns a local Claude Code session that runs against a Codex / GPT-5.x model on Azure AI
/// Foundry. pks-cli hosts an in-process translating proxy that speaks the Anthropic Messages
/// API on the inside (what Claude Code talks to via <c>ANTHROPIC_BASE_URL</c>) and transforms
/// every request into an Azure OpenAI <b>Responses API</b> call, then streams the response back
/// in Anthropic SSE form.
///
///   pks claude codex                       # launch Claude Code on the default codex deployment
///   pks claude codex --model gpt-5.1-codex # pick a deployment
///   pks claude codex --print-env           # just run the proxy + print ANTHROPIC_* exports
///
/// Translation lives in <see cref="AnthropicToResponsesTranslator"/> and
/// <see cref="ResponsesToAnthropicStreamConverter"/>.
/// </summary>
[Description("Run Claude Code locally against a Codex / GPT-5.x model on Azure AI Foundry via a translating proxy")]
public sealed class ClaudeCodexCommand : AsyncCommand<ClaudeCodexCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public ClaudeCodexCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("-m|--model")]
        [Description("Foundry deployment name (e.g. gpt-5.1-codex, gpt-5.3-codex). Defaults to the configured model or gpt-5.1-codex.")]
        public string? Model { get; set; }

        [CommandOption("-e|--reasoning-effort")]
        [Description("Reasoning effort: none|low|medium|high|xhigh (default: medium)")]
        public string ReasoningEffort { get; set; } = "medium";

        [CommandOption("-p|--port")]
        [Description("Port for the local proxy (default: random free port)")]
        public int? Port { get; set; }

        [CommandOption("--print-env")]
        [Description("Run the proxy in the foreground and print ANTHROPIC_* exports instead of launching Claude Code")]
        public bool PrintEnv { get; set; }

        [CommandOption("--no-thinking")]
        [Description("Do not surface Codex reasoning summaries as Claude thinking blocks")]
        public bool NoThinking { get; set; }

        [CommandArgument(0, "[claudeArgs]")]
        [Description("Extra arguments passed through to the claude CLI")]
        public string[] ClaudeArgs { get; set; } = Array.Empty<string>();
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
        if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceEndpoint))
        {
            _console.MarkupLine("[red]No Foundry endpoint configured — run [bold]pks foundry init[/].[/]");
            return 1;
        }

        var deployment = settings.Model
            ?? (LooksLikeCodex(creds.DefaultModel) ? creds.DefaultModel : null)
            ?? "gpt-5.5";

        var responsesUrl = BuildResponsesUrl(creds.SelectedResourceEndpoint);
        var port = settings.Port ?? AnthropicProxyUtil.FindFreePort();
        var proxyToken = Guid.NewGuid().ToString("N");
        var emitThinking = !settings.NoThinking;

        // ---- build the in-process translating proxy ----
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("codex-upstream")
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        var app = builder.Build();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        app.MapPost("/v1/messages/count_tokens", async (HttpContext ctx) =>
        {
            if (!AnthropicProxyUtil.ValidateToken(ctx, proxyToken)) return;
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted);
            var tokens = TokenEstimator.EstimateInputTokens(doc.RootElement);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(new JsonObject { ["input_tokens"] = tokens }.ToJsonString(), ctx.RequestAborted);
        });

        app.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            if (!AnthropicProxyUtil.ValidateToken(ctx, proxyToken)) return;

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted);
            var anthropic = doc.RootElement;
            var wantsStream = anthropic.TryGetProperty("stream", out var s) &&
                              s.ValueKind == JsonValueKind.True;
            var inputTokens = TokenEstimator.EstimateInputTokens(anthropic);

            var responsesBody = AnthropicToResponsesTranslator.BuildResponsesRequest(
                anthropic, deployment, settings.ReasoningEffort, stream: true);

            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, responsesUrl)
            {
                Content = new StringContent(responsesBody.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            await ApplyUpstreamAuthAsync(upstreamReq, creds, ctx.RequestAborted);

            var client = httpClientFactory.CreateClient("codex-upstream");
            using var upstream = await client.SendAsync(
                upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            if (!upstream.IsSuccessStatusCode)
            {
                await AnthropicProxyUtil.RelayUpstreamErrorAsync(ctx, upstream);
                return;
            }

            var converter = new ResponsesToAnthropicStreamConverter(deployment, inputTokens, emitThinking);
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);

            if (wantsStream)
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                await foreach (var evt in AnthropicProxyUtil.ReadSseEventsAsync(upstreamStream, ctx.RequestAborted))
                {
                    foreach (var frame in converter.Handle(evt))
                    {
                        await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
                    }
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            else
            {
                await foreach (var evt in AnthropicProxyUtil.ReadSseEventsAsync(upstreamStream, ctx.RequestAborted))
                {
                    foreach (var _ in converter.Handle(evt)) { /* drain; accumulate */ }
                }
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(converter.BuildFinalMessage().ToJsonString(), ctx.RequestAborted);
            }
        });

        await app.StartAsync();
        var baseUrl = $"http://127.0.0.1:{port}";

        // Preflight the deployment so a wrong name / auth issue surfaces here, instead of
        // Claude Code reporting a vague "model may not exist".
        var preflightError = await PreflightAsync(httpClientFactory, responsesUrl, deployment, creds, settings.ReasoningEffort);
        if (preflightError is not null)
        {
            _console.MarkupLine($"[red]Foundry preflight failed for deployment [bold]{deployment}[/]:[/]");
            _console.WriteLine(preflightError);
            _console.MarkupLine("[dim]Pass a valid deployment with [bold]--model <name>[/] (run [bold]pks foundry status[/] to check your resource).[/]");
            await app.StopAsync();
            return 1;
        }

        if (settings.PrintEnv)
        {
            Console.WriteLine($"export ANTHROPIC_BASE_URL={baseUrl}");
            Console.WriteLine($"export ANTHROPIC_AUTH_TOKEN={proxyToken}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_OPUS_MODEL={deployment}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_SONNET_MODEL={deployment}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_HAIKU_MODEL={deployment}");
            _console.MarkupLine($"[green]Codex proxy live[/] [dim]→ {deployment} @ {responsesUrl}[/]");
            _console.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
            await app.WaitForShutdownAsync();
            return 0;
        }

        _console.MarkupLine($"[green]Launching Claude Code on[/] [bold]{deployment}[/] [dim](Codex via Foundry, effort={settings.ReasoningEffort})[/]");
        var exitCode = await LaunchClaudeAsync(baseUrl, proxyToken, deployment, settings.ClaudeArgs);
        await app.StopAsync();
        return exitCode;
    }

    /// <summary>Sends a tiny Responses call to validate deployment + auth + endpoint. Returns null on success, else the error text.</summary>
    private async Task<string?> PreflightAsync(
        IHttpClientFactory factory, string responsesUrl, string deployment, FoundryStoredCredentials creds, string effort)
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = deployment,
                ["input"] = "ping",
                ["max_output_tokens"] = 16,
                ["stream"] = false,
                ["store"] = false,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, responsesUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            await ApplyUpstreamAuthAsync(req, creds, default);

            var client = factory.CreateClient("codex-upstream");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await client.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode) return null;

            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            return $"  HTTP {(int)resp.StatusCode}: {AnthropicProxyUtil.Truncate(text, 1200)}";
        }
        catch (Exception ex)
        {
            return $"  {ex.Message}";
        }
    }

    private static bool LooksLikeCodex(string? model) =>
        !string.IsNullOrEmpty(model) &&
        (model.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("codex", StringComparison.OrdinalIgnoreCase));

    private async Task<int> LaunchClaudeAsync(string baseUrl, string token, string deployment, string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
        };
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);

        psi.Environment["ANTHROPIC_BASE_URL"] = baseUrl;
        psi.Environment["ANTHROPIC_AUTH_TOKEN"] = token;
        psi.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = deployment;
        psi.Environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = deployment;
        psi.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = deployment;
        // Avoid Claude Code trying to use a real Anthropic key from the ambient env.
        psi.Environment.Remove("ANTHROPIC_API_KEY");

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _console.MarkupLine("[red]Failed to start the claude CLI.[/]");
                return 1;
            }
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _console.MarkupLine("[red]Could not find the [bold]claude[/] CLI on PATH.[/]");
            _console.MarkupLine("[dim]Install Claude Code, or run with [bold]--print-env[/] and launch it yourself.[/]");
            return 127;
        }
    }

    private async Task ApplyUpstreamAuthAsync(HttpRequestMessage req, FoundryStoredCredentials creds, CancellationToken ct)
    {
        // Codex deployments authenticate with an api-key (Entra ID is not supported for Codex);
        // fall back to a bearer token for plain GPT-5 deployments.
        if (!string.IsNullOrEmpty(creds.ApiKey))
        {
            req.Headers.TryAddWithoutValidation("api-key", creds.ApiKey);
            return;
        }

        var token = await _authService.GetAccessTokenAsync(_config.CognitiveScope, ct);
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static string BuildResponsesUrl(string endpoint)
    {
        var baseUrl = endpoint.TrimEnd('/');
        // Normalise to the v1 Responses path regardless of how the endpoint was stored.
        if (baseUrl.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/responses";
        if (baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
            return baseUrl + "/v1/responses";
        return baseUrl + "/openai/v1/responses";
    }
}
