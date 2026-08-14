using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

public interface ITailscaleService
{
    Task<bool> IsAuthenticatedAsync();
    Task<TailscaleStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(TailscaleStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();

    /// <summary>
    /// Builds the <c>tailscale up …</c> argument line for a given hostname. It carries the auth key,
    /// so it comes back as a <see cref="SecretValue"/>: a caller can hand it to cloud-init through
    /// <c>ScalewayCreateOptions.TailscaleUpArgs</c>, but cannot print it.
    /// </summary>
    SecretValue BuildUpArgs(TailscaleStoredCredentials creds, string hostname);

    /// <summary>
    /// Runs <c>tailscale up</c> on an already-reachable host. The command line is composed here and
    /// handed straight to <paramref name="run"/>, so the calling command never holds the auth key —
    /// which is the whole reason this method exists rather than the caller doing
    /// <c>$"tailscale up {BuildUpArgs(…)}"</c> itself.
    /// </summary>
    /// <param name="run">Executes one shell command on the target host; typically the caller's
    /// spinner-wrapped SSH step. Null means the step did not complete.</param>
    Task<SshResult?> JoinTailnetAsync(
        TailscaleStoredCredentials creds, string hostname, string sudoPrefix,
        Func<string, Task<SshResult?>> run);
}

public class TailscaleService : ITailscaleService
{
    private const string StorageKey = "tailscale.auth.credentials";
    private readonly IConfigurationService _config;
    private readonly ISecretResolver _secrets;

    public TailscaleService(IConfigurationService config, ISecretResolver secrets)
    {
        _config = config;
        _secrets = secrets;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var creds = await GetStoredCredentialsAsync();
        return creds != null && creds.AuthKey.HasValue;
    }

    public async Task<TailscaleStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<TailscaleStoredCredentials>(json, SecretJson.Persistence); }
        catch (JsonException) { return null; }
    }

    public async Task StoreCredentialsAsync(TailscaleStoredCredentials credentials)
    {
        // Persistence options — the default masks the auth key, and a stored "***" would silently
        // stop every future VM from joining the tailnet.
        var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
        await _config.SetAsync(StorageKey, json, global: true);
    }

    public Task ClearStoredCredentialsAsync() => _config.DeleteAsync(StorageKey);

    public SecretValue BuildUpArgs(TailscaleStoredCredentials creds, string hostname)
    {
        var args = new List<string>
        {
            $"--authkey={creds.AuthKey.Reveal()}",
            $"--hostname={Sanitize(hostname)}"
        };
        if (creds.EnableSsh) args.Add("--ssh");
        if (creds.AcceptRoutes) args.Add("--accept-routes");
        if (creds.AdvertiseExitNode) args.Add("--advertise-exit-node");
        if (!string.IsNullOrWhiteSpace(creds.LoginServer)) args.Add($"--login-server={creds.LoginServer}");
        return SecretValue.From(string.Join(' ', args));
    }

    public Task<SshResult?> JoinTailnetAsync(
        TailscaleStoredCredentials creds, string hostname, string sudoPrefix,
        Func<string, Task<SshResult?>> run)
        => run($"{sudoPrefix}tailscale up {BuildUpArgs(creds, hostname).Reveal()}");

    // Tailscale hostnames must be DNS-label-safe: lowercase alphanumerics and hyphens.
    private static string Sanitize(string name)
    {
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
