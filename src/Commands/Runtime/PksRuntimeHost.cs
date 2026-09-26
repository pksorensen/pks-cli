using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Commands.Runtime.Speech;

namespace PKS.Commands.Runtime;

public static class PksRuntimeHost
{
    public static async Task<int> RunAsync(
        int port,
        IReadOnlyList<string>? forcedCapabilities = null,
        CancellationToken cancellationToken = default)
    {
        RuntimeHostOptions options;
        IReadOnlyList<IRuntimeCapability> capabilities;
        try
        {
            options = RuntimeHostOptions.FromEnvironment(forcedCapabilities: forcedCapabilities);
            capabilities = options.Capabilities.Select(CreateCapability).ToArray();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"pks runtime configuration error: {error.Message}");
            return 2;
        }

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        var app = builder.Build();
        app.UseWebSockets();

        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            runtime = "pks-cli",
            capabilities = capabilities.Select(capability => capability.Id),
        }));
        app.MapGet("/v1/capabilities", () => Results.Json(new
        {
            capabilities = capabilities.Select(capability => capability.Describe()),
        }));

        foreach (var capability in capabilities)
            capability.MapEndpoints(app, options.ServiceToken);

        Console.WriteLine($"pks-cli runtime listening on :{port} ({string.Join(", ", options.Capabilities)})");
        await app.StartAsync(cancellationToken);
        await app.WaitForShutdownAsync(cancellationToken);
        return 0;
    }

    private static IRuntimeCapability CreateCapability(string capability) => capability switch
    {
        SpeechProtocol.Capability => new SpeechRuntimeCapability(SpeechRuntimeOptions.FromEnvironment()),
        _ => throw new InvalidOperationException($"Unsupported runtime capability '{capability}'"),
    };
}
