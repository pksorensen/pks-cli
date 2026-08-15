using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Exec;

/// <summary>
/// A loopback managed-identity endpoint, for the lifetime of one child process.
///
/// Some tools want to authenticate the way an Azure workload does — read <c>IDENTITY_ENDPOINT</c>, ask
/// it for a token, use the token — and that is a better contract than handing them a key, because the
/// token is short-lived and the tool never holds a credential it could persist. This serves that shape
/// from the signed-in Foundry session.
///
/// It listens on loopback only and mints tokens for a caller-chosen scope. The <c>X-IDENTITY-HEADER</c>
/// secret is checked only when a request sends one, which means any other process on the machine can
/// reach it while a child is running. That is a deliberate, documented limitation and not a property to
/// rely on: the proxy's real boundary is that it exists for seconds and only ever on localhost.
/// </summary>
public sealed class ImdsProxy
{
    public string Endpoint { get; }
    public string Header { get; }

    private readonly CancellationTokenSource _cts;
    private readonly Task _runTask;

    private ImdsProxy(CancellationTokenSource cts, string endpoint, string header, Task runTask)
    {
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

        return new ImdsProxy(cts, $"http://localhost:{port}/", headerSecret, task);
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
