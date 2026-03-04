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
    private readonly string _socketDir;
    private readonly string _socketPath;
    private readonly IGitHubAuthenticationService _githubAuth;
    private readonly Action<string>? _onLog;
    private WebApplication? _app;

    public GitCredentialServer(IGitHubAuthenticationService githubAuth, string socketId, Action<string>? onLog = null)
    {
        // Use a stable directory so we can bind-mount the directory (not the file).
        // Directory mounts survive socket file recreation across runner restarts.
        _socketDir = Path.Combine(Path.GetTempPath(), $"pks-credentials-{socketId}");
        _socketPath = Path.Combine(_socketDir, "creds.sock");
        _githubAuth = githubAuth;
        _onLog = onLog;
    }

    /// <summary>
    /// The directory containing the credential socket. Bind-mount this directory
    /// (not the socket file) so that containers survive runner restarts.
    /// </summary>
    public string SocketDirectory => _socketDir;

    public string SocketPath => _socketPath;

    public async Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_socketDir);

        if (File.Exists(_socketPath))
            File.Delete(_socketPath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(_socketPath);
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _app = builder.Build();

        _app.MapGet("/git-credential", async (HttpRequest request) =>
        {
            var host = request.Query["host"].FirstOrDefault() ?? "unknown";
            _onLog?.Invoke($"Credential request received for host: {host}");

            var storedToken = await _githubAuth.GetStoredTokenAsync();
            if (storedToken is { IsValid: true, AccessToken: not null })
            {
                _onLog?.Invoke($"Credential served successfully for host: {host}");
                return Results.Json(new { password = storedToken.AccessToken });
            }

            _onLog?.Invoke($"Credential unavailable (503) for host: {host}");
            return Results.Problem(
                "No git credential available — run 'pks github runner register' first",
                statusCode: (int)HttpStatusCode.ServiceUnavailable);
        });

        await _app.StartAsync(ct);

        // Make socket world-accessible so container users (e.g. 'node') can connect
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (File.Exists(_socketPath))
            {
                File.SetUnixFileMode(_socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            }
        }
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
