using System.Diagnostics;

namespace PKS.Infrastructure.Services;

public class SshExecutor : ISshExecutor
{
    private readonly Func<ProcessStartInfo, Process?> _processFactory;

    public SshExecutor() : this(Process.Start) { }

    public SshExecutor(Func<ProcessStartInfo, Process?> processFactory)
    {
        _processFactory = processFactory;
    }

    public async Task<SshResult> RunAsync(SshTarget target, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var args = $"-o StrictHostKeyChecking=no -p {target.Port}";
        if (!string.IsNullOrEmpty(target.KeyPath))
            args += $" -i \"{target.KeyPath}\"";
        args += $" {target.Username}@{target.Host} \"{command}\"";

        var psi = new ProcessStartInfo("ssh")
        {
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? proc;
        try
        {
            proc = _processFactory(psi);
        }
        catch
        {
            return new SshResult(-1, "", "Failed to start ssh process", false);
        }

        if (proc == null)
            return new SshResult(-1, "", "Failed to start ssh process", false);

        using (proc)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

                await proc.WaitForExitAsync(timeoutCts.Token);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                return new SshResult(proc.ExitCode, stdout, stderr, false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new SshResult(-1, "", "", true);
            }
        }
    }
}
