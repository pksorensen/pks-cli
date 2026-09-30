using System.Diagnostics;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;

namespace PKS.Commands.Vm;

/// <summary>
/// Shared by <c>vm snapshot</c>, <c>vm reset</c> and <c>vm init</c>: flushing the guest before a
/// snapshot, and retrying an ARM call once a PIM role has been activated (<see cref="AzurePimRetry"/>).
/// </summary>
public static class VmBaseline
{
    /// <summary>Runs <c>sync</c> on the VM so a snapshot of the running disk holds everything the
    /// guest has written. Best effort: a snapshot without it is still crash-consistent.</summary>
    public static async Task<bool> TrySyncAsync(string host, string user, string keyPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(host)) return false;
        try
        {
            var args = "-o StrictHostKeyChecking=no -o BatchMode=yes -o ConnectTimeout=10";
            if (!string.IsNullOrEmpty(keyPath)) args += $" -i \"{keyPath}\"";
            using var proc = Process.Start(new ProcessStartInfo("ssh", $"{args} {user}@{host} sync")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc == null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await proc.WaitForExitAsync(timeout.Token);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>See <see cref="AzurePimRetry"/>.</summary>
    public static Task<T> WithPimRetryAsync<T>(
        Func<Task<T>> operation, IAnsiConsole console, IHttpClientFactory httpClientFactory,
        string token, string subscriptionId, string purpose)
        => AzurePimRetry.RunAsync(operation, console, httpClientFactory.CreateClient(), token, subscriptionId, purpose);
}
