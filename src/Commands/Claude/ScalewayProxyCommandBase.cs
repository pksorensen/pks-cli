using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
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
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

/// <summary>
/// Base for the Scaleway-backed <c>pks claude</c> aliases (<c>scaleway</c> / <c>mistral</c> /
/// <c>qwen</c>). Each alias narrows <see cref="Candidates"/> to a slice of
/// <see cref="GenerativeModelCatalog"/>; the user picks the specific version (or passes it as the
/// first argument), then pks-cli hosts an in-process proxy that speaks the Anthropic Messages API
/// inbound (what Claude Code talks to via <c>ANTHROPIC_BASE_URL</c>) and translates every request to
/// the OpenAI <b>Chat Completions API</b> that Scaleway exposes, streaming the response back as
/// Anthropic SSE.
///
/// Translation lives in <see cref="AnthropicToChatCompletionsTranslator"/> and
/// <see cref="ChatCompletionsToAnthropicStreamConverter"/>; shared plumbing in
/// <see cref="AnthropicProxyUtil"/>. Auth is the stored Scaleway secret key sent as a Bearer token.
/// </summary>
public abstract class ScalewayProxyCommandBase : AsyncCommand<ScalewayProxyCommandBase.Settings>
{
    private readonly IScalewayService _scaleway;
    private readonly IAnsiConsole _console;

    protected ScalewayProxyCommandBase(IScalewayService scaleway, IAnsiConsole console)
    {
        _scaleway = scaleway;
        _console = console;
    }

    /// <summary>Models this alias offers in the picker.</summary>
    protected abstract IReadOnlyList<GenerativeModel> Candidates();

    /// <summary>Human label for the alias used in prompts (e.g. "Scaleway", "Mistral").</summary>
    protected abstract string FamilyLabel { get; }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[model]")]
        [Description("Model id to use (skips the picker). Run with no model to choose interactively.")]
        public string? Model { get; set; }

        [CommandOption("-p|--port")]
        [Description("Port for the local proxy (default: random free port)")]
        public int? Port { get; set; }

        [CommandOption("--print-env")]
        [Description("Run the proxy in the foreground and print ANTHROPIC_* exports instead of launching Claude Code")]
        public bool PrintEnv { get; set; }

        [CommandOption("--no-thinking")]
        [Description("Do not surface model reasoning as Claude thinking blocks")]
        public bool NoThinking { get; set; }

        [CommandArgument(1, "[claudeArgs]")]
        [Description("Extra arguments passed through to the claude CLI")]
        public string[] ClaudeArgs { get; set; } = Array.Empty<string>();
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _scaleway.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Scaleway.[/]");
            _console.MarkupLine("[dim]Run [bold]pks scaleway init[/] first.[/]");
            return 1;
        }

        var creds = await _scaleway.GetStoredCredentialsAsync();
        if (creds == null || string.IsNullOrEmpty(creds.SecretKey))
        {
            _console.MarkupLine("[red]No Scaleway secret key configured — run [bold]pks scaleway init[/].[/]");
            return 1;
        }

        var model = ResolveModel(settings.Model);
        if (model is null) return 1;

        var chatUrl = GenerativeModelCatalog.ScalewayBaseUrl.TrimEnd('/') + "/chat/completions";
        var port = settings.Port ?? AnthropicProxyUtil.FindFreePort();
        var proxyToken = Guid.NewGuid().ToString("N");
        var emitThinking = !settings.NoThinking;
        var apiKey = creds.SecretKey;

        // ---- build the in-process translating proxy ----
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("scaleway-upstream")
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
            var wantsStream = anthropic.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
            var inputTokens = TokenEstimator.EstimateInputTokens(anthropic);

            var chatBody = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, model.Id, stream: true, maxOutputCap: model.MaxOutputTokens);

            using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = new StringContent(chatBody.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            upstreamReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var client = httpClientFactory.CreateClient("scaleway-upstream");
            using var upstream = await client.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

            if (!upstream.IsSuccessStatusCode)
            {
                await AnthropicProxyUtil.RelayUpstreamErrorAsync(ctx, upstream);
                return;
            }

            var converter = new ChatCompletionsToAnthropicStreamConverter(model.Id, inputTokens, emitThinking);
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
                foreach (var frame in converter.Flush())
                {
                    await ctx.Response.WriteAsync(frame, ctx.RequestAborted);
                }
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
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

        // Preflight so a wrong model / auth issue surfaces here, not as Claude Code's vague error.
        var preflightError = await PreflightAsync(httpClientFactory, chatUrl, model.Id, apiKey);
        if (preflightError is not null)
        {
            _console.MarkupLine($"[red]Scaleway preflight failed for model [bold]{model.Id}[/]:[/]");
            _console.WriteLine(preflightError);
            _console.MarkupLine("[dim]Pick another model, or check [bold]pks scaleway init[/].[/]");
            await app.StopAsync();
            return 1;
        }

        if (settings.PrintEnv)
        {
            Console.WriteLine($"export ANTHROPIC_BASE_URL={baseUrl}");
            Console.WriteLine($"export ANTHROPIC_AUTH_TOKEN={proxyToken}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_OPUS_MODEL={model.Id}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_SONNET_MODEL={model.Id}");
            Console.WriteLine($"export ANTHROPIC_DEFAULT_HAIKU_MODEL={model.Id}");
            _console.MarkupLine($"[green]Scaleway proxy live[/] [dim]→ {model.Id} @ {chatUrl}[/]");
            _console.MarkupLine("[dim]Press Ctrl+C to stop.[/]");
            await app.WaitForShutdownAsync();
            return 0;
        }

        _console.MarkupLine($"[green]Launching Claude Code on[/] [bold]{model.Id}[/] [dim]({FamilyLabel} via Scaleway)[/]");
        var exitCode = await LaunchClaudeAsync(baseUrl, proxyToken, model.Id, settings.ClaudeArgs);
        await app.StopAsync();
        return exitCode;
    }

    /// <summary>Resolves the model from the argument, or prompts the user, defaulting sensibly.</summary>
    private GenerativeModel? ResolveModel(string? requested)
    {
        var candidates = Candidates();
        if (candidates.Count == 0)
        {
            _console.MarkupLine("[red]No models available for this alias.[/]");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = candidates.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
            // Allow any catalog model id even if outside this alias's slice.
            var anyMatch = GenerativeModelCatalog.Scaleway.FirstOrDefault(m => string.Equals(m.Id, requested, StringComparison.OrdinalIgnoreCase));
            if (anyMatch is not null) return anyMatch;
            // Unknown id: trust the user and pass it through (lets new Scaleway models work before the catalog catches up).
            return new GenerativeModel(requested, FamilyLabel.ToLowerInvariant(), GenerativeModelCatalog.ScalewayProvider, requested);
        }

        var def = GenerativeModelCatalog.DefaultIn(candidates)!;

        // Non-interactive (piped/CI): take the default without prompting.
        if (!_console.Profile.Capabilities.Interactive)
        {
            return def;
        }

        // Order the default first so pressing Enter picks it.
        var ordered = candidates.OrderByDescending(m => m == def).ThenBy(m => m.Id).ToList();
        var prompt = new SelectionPrompt<GenerativeModel>()
            .Title($"Pick a [bold]{FamilyLabel}[/] model [dim](default: {def.Id})[/]:")
            .PageSize(15)
            .UseConverter(m => m == def ? $"{m.Label} [green](default)[/]" : m.Label)
            .AddChoices(ordered);

        return _console.Prompt(prompt);
    }

    /// <summary>Sends a tiny chat call to validate model + auth + endpoint. Returns null on success, else the error text.</summary>
    private async Task<string?> PreflightAsync(IHttpClientFactory factory, string chatUrl, string model, string apiKey)
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "ping" } },
                ["max_tokens"] = 16,
                ["stream"] = false,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var client = factory.CreateClient("scaleway-upstream");
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

    private async Task<int> LaunchClaudeAsync(string baseUrl, string token, string model, string[] extraArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
        };
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);

        psi.Environment["ANTHROPIC_BASE_URL"] = baseUrl;
        psi.Environment["ANTHROPIC_AUTH_TOKEN"] = token;
        psi.Environment["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
        psi.Environment["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
        psi.Environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model;
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
}
