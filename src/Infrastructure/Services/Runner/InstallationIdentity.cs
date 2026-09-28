using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// A self-hosted Agentics box, as its installer describes it to pks through the environment
/// (<c>/etc/agentics/runner.env</c>, loaded by the systemd unit and by login shells):
///
/// <list type="bullet">
/// <item><c>AGENTICS_INSTALLATION_ID</c> — the id agentics.dk enrolled the box under.</item>
/// <item><c>AGENTICS_INSTALLATION_KEY</c> — the box's Ed25519 key, the same one that signs its
/// registry pulls. It is the runner's only credential: no control token is handed out.</item>
/// <item><c>AGENTICS_SERVER</c> — the platform's public URL, what jobs and vibecast are given.</item>
/// <item><c>AGENTICS_INTERNAL_SERVER</c> — the platform as reachable from this machine (the
/// loopback port), where the runner's own traffic goes.</item>
/// </list>
///
/// With these set, <c>pks agentics runner start</c> needs no arguments at all: that is the point.
/// </summary>
public sealed record InstallationContext(string Id, string KeyPath, string Server, string? InternalServer)
{
    public const string IdVariable = "AGENTICS_INSTALLATION_ID";
    public const string KeyVariable = "AGENTICS_INSTALLATION_KEY";
    public const string ServerVariable = "AGENTICS_SERVER";
    public const string InternalServerVariable = "AGENTICS_INTERNAL_SERVER";

    /// <summary>The base URL for the runner's own calls.</summary>
    public string ApiBase => string.IsNullOrWhiteSpace(InternalServer) ? Server : InternalServer.TrimEnd('/');

    /// <summary>What a token for this platform names as its audience: the public host, the same
    /// whichever address the request actually travels to.</summary>
    public string Audience => new Uri(Server).Authority;

    /// <summary>The installation this process runs on, or null when it is not a configured box.
    /// A key that is named but unreadable is treated as "not a box" too: the caller falls back to
    /// the per-project runner and says why, rather than failing on a half-configured host.</summary>
    public static InstallationContext? FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var id = env(IdVariable)?.Trim();
        var key = env(KeyVariable)?.Trim();
        var server = env(ServerVariable)?.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(server)) return null;

        var internalServer = env(InternalServerVariable)?.Trim();
        return new InstallationContext(
            id,
            key,
            RunnerRegistrar.NormalizeServer(server),
            string.IsNullOrEmpty(internalServer) ? null : RunnerRegistrar.NormalizeServer(internalServer));
    }
}

public interface IInstallationTokenSigner
{
    /// <summary>A short-lived EdDSA JWT (<c>iss=agentics-installation</c>) for <paramref name="audience"/>.</summary>
    Task<string> MintAsync(InstallationContext installation, string audience, CancellationToken ct = default);
}

/// <summary>
/// Signs installation JWTs with <c>openssl pkeyutl</c> — exactly the recipe the box's docker
/// credential helper uses for the registry (selfhost.sh), so the platform verifies one format.
/// .NET has no Ed25519, and the installer already requires OpenSSL 3 for this very key.
/// </summary>
public sealed class OpenSslInstallationTokenSigner : IInstallationTokenSigner
{
    /// <summary>Five minutes: well inside the platform's ten-minute ceiling, and every request
    /// gets a fresh one, so nothing long-lived ever exists to be stolen.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now;

    public OpenSslInstallationTokenSigner(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public async Task<string> MintAsync(InstallationContext installation, string audience, CancellationToken ct = default)
    {
        var iat = _now().ToUnixTimeSeconds();
        var signingInput = SigningInput(installation.Id, audience, iat, iat + (long)Lifetime.TotalSeconds);

        // pkeyutl's one-shot Ed25519 mode needs a file it can size; stdin is refused.
        var inputFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(inputFile, signingInput, ct);
            var psi = new ProcessStartInfo("openssl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "pkeyutl", "-sign", "-inkey", installation.KeyPath, "-rawin", "-in", inputFile })
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start openssl");
            using var signature = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(signature, ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(copy, error, process.WaitForExitAsync(ct));
            if (process.ExitCode != 0 || signature.Length != 64)
                throw new InvalidOperationException(
                    $"signing with the installation key failed (openssl exit {process.ExitCode}): {(await error).Trim()}");

            return $"{signingInput}.{Base64Url(signature.ToArray())}";
        }
        finally
        {
            try { File.Delete(inputFile); } catch { /* best effort */ }
        }
    }

    /// <summary>The <c>header.claims</c> part. Written by hand in a fixed property order so the
    /// bytes match the shell helper's and the platform's shared test vector.</summary>
    internal static string SigningInput(string sub, string audience, long iat, long exp)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"EdDSA\",\"typ\":\"JWT\"}"));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", "agentics-installation");
            writer.WriteString("sub", sub);
            writer.WriteString("aud", audience);
            writer.WriteNumber("iat", iat);
            writer.WriteNumber("exp", exp);
            writer.WriteEndObject();
        }

        return $"{header}.{Base64Url(buffer.ToArray())}";
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
