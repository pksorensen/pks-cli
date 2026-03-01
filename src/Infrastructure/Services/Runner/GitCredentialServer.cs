using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Lightweight HTTP server running on a Unix socket that serves the locally stored
/// device-code OAuth token as a git credential.
/// </summary>
public class GitCredentialServer : IAsyncDisposable
{
    private readonly string _socketPath;
    private readonly IGitHubAuthenticationService _githubAuth;
    private WebApplication? _app;

    public GitCredentialServer(IGitHubAuthenticationService githubAuth, string socketId)
    {
        _socketPath = Path.Combine(Path.GetTempPath(), $"pks-credentials-{socketId}.sock");
        _githubAuth = githubAuth;
    }

    public string SocketPath => _socketPath;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(_socketPath);
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        _app.MapGet("/git-credential", async () =>
        {
            var storedToken = await _githubAuth.GetStoredTokenAsync();
            if (storedToken is { IsValid: true, AccessToken: not null })
            {
                return Results.Json(new { password = storedToken.AccessToken });
            }

            return Results.Problem(
                "No git credential available — run 'pks github runner register' first",
                statusCode: (int)HttpStatusCode.ServiceUnavailable);
        });

        await _app.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);
    }
}
