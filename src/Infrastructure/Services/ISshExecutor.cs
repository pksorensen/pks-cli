namespace PKS.Infrastructure.Services;

public record SshResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public interface ISshExecutor
{
    Task<SshResult> RunAsync(SshTarget target, string command, TimeSpan timeout, CancellationToken ct = default);
}
