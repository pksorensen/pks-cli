using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// Starts an HTTP proxy on 0.0.0.0:7878 that intercepts git smart HTTP operations
/// (push/pull/fetch/clone) targeting Azure DevOps, injects a fresh Bearer token
/// server-side, and forwards to dev.azure.com — the token never enters the container.
///
/// Two credential sources, tried in order:
///   1. ~/.pks-cli/ado-credentials.json — minimal file copied to VM by ClaudeSpawnCommand
///      (Host => VM => Docker => devcontainer scenario)
///   2. IAzureDevOpsAuthService / settings.json — full credentials from pks ado init
///      (Host => Docker => devcontainer scenario, proxy runs on the host)
///
/// Inside devcontainers, configure git to route through this proxy:
///   git config --global url.'http://172.17.0.1:7878/'.insteadOf 'https://dev.azure.com/'
///
/// Only repos listed via --allow are accepted. All other requests return 403.
/// Non-git-smart-HTTP requests return 400.
/// </summary>
[Description("Start the ADO git HTTP proxy (credential-injecting, token never enters container)")]
public class AdoGitProxyCommand : AsyncCommand<AdoGitProxyCommand.Settings>
{
    private const int ProxyPort = 7878;
    private const string ClientId = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1"; // VS Code well-known public client
    private static readonly string CredentialsFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "ado-credentials.json");

    private readonly IAzureDevOpsAuthService _authService;
    private readonly IAnsiConsole _console;

    public AdoGitProxyCommand(IAzureDevOpsAuthService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public class Settings : AdoSettings
    {
        [CommandOption("--allow")]
        [Description("Allowed repo as org/project/repo (repeatable). Example: --allow Delegate/MyProject/my-repo")]
        public string[]? Allow { get; set; }
    }

    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Transfer-Encoding",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Upgrade",
    };

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var hasCredFile = File.Exists(CredentialsFile);
        var hasAuthService = await _authService.IsAuthenticatedAsync();

        if (!hasCredFile && !hasAuthService)
        {
            _console.MarkupLine("[red]No ADO credentials found.[/]");
            _console.MarkupLine($"[dim]Either run [bold]pks ado init[/] on this machine, or ensure {CredentialsFile} exists (copied by pks claude for VM deployments).[/]");
            return 1;
        }

        var allowlist = BuildAllowlist(settings.Allow);
        if (allowlist.Count == 0)
        {
            _console.MarkupLine("[red]No repos in allowlist. Pass --allow org/project/repo.[/]");
            return 1;
        }

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://0.0.0.0:{ProxyPort}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        builder.Services.AddHttpClient("ado-git-proxy");

        var app = builder.Build();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        app.MapMethods("{**path}", AllHttpMethods, async (HttpContext ctx) =>
        {
            // Validate this is a git smart HTTP request
            var service = ctx.Request.Query["service"].FirstOrDefault() ?? "";
            var contentType = ctx.Request.ContentType ?? "";
            var isGitUpload = service == "git-upload-pack" || contentType.Contains("git-upload-pack");
            var isGitReceive = service == "git-receive-pack" || contentType.Contains("git-receive-pack");

            if (!isGitUpload && !isGitReceive)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Only git smart HTTP protocol accepted");
                return;
            }

            // Parse org/project/repo from path: /Org/Project/_git/Repo/...
            var pathValue = ctx.Request.Path.Value ?? "";
            if (!TryParseAdoPath(pathValue, out var org, out var project, out var repo))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Cannot parse ADO path — expected /org/project/_git/repo/...");
                return;
            }

            // Allowlist check
            var key = $"{org}/{project}/{repo}".ToLowerInvariant();
            if (!allowlist.Contains(key))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync($"Repo not in allowlist: {org}/{project}/{repo}");
                return;
            }

            // Read credentials and get a fresh Bearer token
            var token = await GetBearerTokenAsync(httpClientFactory, _authService);
            if (token == null)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync("Failed to acquire ADO access token — check ado-credentials.json");
                return;
            }

            // Reconstruct upstream URL
            var upstreamUrl = $"https://dev.azure.com{ctx.Request.Path}{ctx.Request.QueryString}";

            using var upstreamRequest = new HttpRequestMessage(
                new HttpMethod(ctx.Request.Method), upstreamUrl);

            if (ctx.Request.ContentLength > 0 ||
                ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamRequest.Content = new StreamContent(ctx.Request.Body);
                if (ctx.Request.ContentType != null)
                    upstreamRequest.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
            }

            foreach (var header in ctx.Request.Headers)
            {
                if (SkipHeaders.Contains(header.Key)) continue;
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            upstreamRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var client = httpClientFactory.CreateClient("ado-git-proxy");
            using var upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);

            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

            foreach (var header in upstreamResponse.Headers)
                ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
            foreach (var header in upstreamResponse.Content.Headers)
                ctx.Response.Headers.Append(header.Key, header.Value.ToArray());

            ctx.Response.Headers.Remove("Transfer-Encoding");

            await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });

        await app.RunAsync();
        return 0;
    }

    /// <summary>
    /// Gets a fresh ADO Bearer token. Tries sources in order:
    ///   1. ~/.pks-cli/ado-credentials.json (minimal file, VM scenario)
    ///   2. IAzureDevOpsAuthService / settings.json (local Docker scenario)
    /// </summary>
    private static async Task<string?> GetBearerTokenAsync(
        IHttpClientFactory httpClientFactory, IAzureDevOpsAuthService authService)
    {
        // Source 1: minimal credentials file (VM scenario — copied by ClaudeSpawnCommand)
        if (File.Exists(CredentialsFile))
        {
            AdoMinimalCredentials creds;
            try
            {
                var json = await File.ReadAllTextAsync(CredentialsFile);
                creds = JsonSerializer.Deserialize<AdoMinimalCredentials>(json)
                        ?? throw new InvalidOperationException("Empty credentials file");
            }
            catch (Exception ex)
            {
                await LogAsync($"ERROR reading credentials file: {ex.Message}");
                // Don't return null yet — fall through to auth service
                goto tryAuthService;
            }

            var tenantId = string.IsNullOrWhiteSpace(creds.TenantId) ? "common" : creds.TenantId;
            var tokenUrl = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
            var body = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = creds.RefreshToken,
                ["scope"] = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access",
            };

            try
            {
                var client = httpClientFactory.CreateClient("ado-git-proxy");
                using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(body));
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    await LogAsync($"ERROR token endpoint {(int)response.StatusCode}: {responseJson}");
                    goto tryAuthService;
                }

                var tokenResponse = JsonSerializer.Deserialize<AdoTokenRefreshResponse>(responseJson);
                if (tokenResponse?.AccessToken == null) goto tryAuthService;

                // Rotate refresh token if the server issued a new one
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken) &&
                    tokenResponse.RefreshToken != creds.RefreshToken)
                {
                    creds.RefreshToken = tokenResponse.RefreshToken;
                    await File.WriteAllTextAsync(CredentialsFile,
                        JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = false }));
                }

                await LogAsync("TOKEN issued ok (credentials file)");
                return tokenResponse.AccessToken;
            }
            catch (Exception ex)
            {
                await LogAsync($"ERROR refreshing token from file: {ex.Message}");
            }
        }

        tryAuthService:
        // Source 2: full auth service / settings.json (local Docker scenario)
        try
        {
            var token = await authService.RefreshAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                await LogAsync("TOKEN issued ok (auth service)");
                return token;
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"ERROR refreshing token from auth service: {ex.Message}");
        }

        return null;
    }

    private static Task LogAsync(string message) =>
        File.AppendAllTextAsync("/tmp/pks-ado-proxy.log", $"{DateTime.UtcNow:O} {message}\n");

    private static HashSet<string> BuildAllowlist(string[]? allow)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allow == null) return set;
        foreach (var entry in allow)
            set.Add(entry.Trim().ToLowerInvariant());
        return set;
    }

    private static bool TryParseAdoPath(string path, out string org, out string project, out string repo)
    {
        org = project = repo = string.Empty;
        var trimmed = path.TrimStart('/');
        var parts = trimmed.Split('/');
        if (parts.Length < 4) return false;

        var gitIndex = Array.FindIndex(parts, p =>
            string.Equals(p, "_git", StringComparison.OrdinalIgnoreCase));
        if (gitIndex < 2) return false;

        org = Uri.UnescapeDataString(parts[0]);
        project = Uri.UnescapeDataString(string.Join("/", parts[1..gitIndex]));
        repo = Uri.UnescapeDataString(parts[gitIndex + 1]);

        return !string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(repo);
    }

    private sealed class AdoMinimalCredentials
    {
        [JsonPropertyName("TenantId")]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("RefreshToken")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class AdoTokenRefreshResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }
}
