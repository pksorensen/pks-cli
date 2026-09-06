using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Reads secrets out of pks-agent-vault by driving its CLI, so the runner can fetch what it
/// needs itself instead of being wrapped in <c>vault agent run -- pks …</c>.
///
/// The wrapper existed for one mechanical reason: <c>--file</c> puts a secret in an anonymous
/// memfd and passes <c>/proc/self/fd/N</c>, and a file descriptor only flows parent → child. That
/// forced vault to be the parent and pks to be the child, which in turn made every runner start
/// a command nobody remembers. Inverting it — pks as the parent, calling <c>vault agent read</c>
/// — costs nothing: <c>--file</c>'s purpose was keeping the value out of the process
/// <i>environment block</i>, and a string captured from stdout was never in one.
///
/// What we rely on, all of it already contract in the vault CLI:
/// <list type="bullet">
/// <item><c>read --field NAME</c> prints the value with no trailing newline, explicitly so it can
/// be captured into a variable.</item>
/// <item>Approval notices go to <b>stderr</b>, so stdout stays the value and nothing else. They
/// are relayed live, because <c>--wait</c> can block for minutes on a human.</item>
/// <item><c>grants --json</c> answers "what am I allowed to read" before we have any ids.</item>
/// <item>The identity file <b>pins the server</b>. There is no <c>--server</c> on these commands;
/// pointing at a different vault means a different identity file, which is why every call here
/// passes <c>--identity</c> explicitly rather than letting the default path decide.</item>
/// </list>
///
/// No method here logs, returns or formats a field value. <see cref="ReadFieldAsync"/> is the one
/// that has one, and it hands it straight back to the caller.
/// </summary>
public interface IVaultCliService
{
    /// <summary>Absolute path to the <c>vault</c> binary, or null when it is not installed.</summary>
    string? FindBinary();

    /// <summary>
    /// Who this identity file says we are — or null when there is no identity file, i.e. this host
    /// has never been enrolled. A malformed file throws; "not enrolled" and "broken" are different
    /// answers and the preflight prints different instructions for them.
    /// </summary>
    Task<VaultAgentIdentity?> WhoAmIAsync(string identityPath, CancellationToken ct = default);

    /// <summary>What this agent may read. Empty is a normal answer, not an error.</summary>
    Task<IReadOnlyList<VaultGrant>> GrantsAsync(string identityPath, CancellationToken ct = default);

    /// <summary>
    /// Reads one field. May block for as long as <paramref name="wait"/> if the grant needs a human
    /// to approve the release; progress goes to <paramref name="onNotice"/> so the runner does not
    /// look hung while it waits.
    /// </summary>
    Task<string> ReadFieldAsync(
        string identityPath,
        string vaultId,
        string itemId,
        string field,
        TimeSpan wait,
        Action<string>? onNotice = null,
        CancellationToken ct = default);
}

/// <summary>What <c>vault agent whoami</c> reports about this host.</summary>
public record VaultAgentIdentity(
    string AgentId,
    string Owner,
    string Server,
    string Protection,
    string IdentityPath,
    bool HasServiceAccount)
{
    /// <summary>
    /// An identity file with no passphrase. Correct for a headless runner — the alternative is a
    /// process that blocks on a passphrase prompt nobody is there to answer — and worth saying out
    /// loud, because anyone who can read the file can be this agent until the owner revokes it.
    /// </summary>
    public bool IsUnprotected => string.Equals(Protection, "none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One grant, as <c>vault agent grants --json</c> reports it.</summary>
public record VaultGrant
{
    [JsonPropertyName("policyId")] public string PolicyId { get; init; } = "";
    [JsonPropertyName("vaultId")] public string VaultId { get; init; } = "";
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    [JsonPropertyName("purpose")] public string Purpose { get; init; } = "";
    [JsonPropertyName("expiresAt")] public string ExpiresAt { get; init; } = "";
    [JsonPropertyName("usesLeft")] public int UsesLeft { get; init; }
    [JsonPropertyName("maxUses")] public int MaxUses { get; init; }
    [JsonPropertyName("consentMode")] public string ConsentMode { get; init; } = "";
    [JsonPropertyName("usable")] public bool Usable { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>
    /// True when reading this grant will page a human. It is a legitimate choice for a key this
    /// valuable, and a nasty surprise at 2am if nobody said so — the preflight says so.
    /// </summary>
    public bool NeedsApproval =>
        !string.Equals(ConsentMode, "never", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(ConsentMode);
}

/// <summary>
/// Runs the vault CLI. Separate from <see cref="IProcessRunner"/> for two reasons this service
/// needs and that one does not have: arguments are passed as a list rather than a joined string
/// (identity paths contain spaces; a quoting bug here would read the wrong file), and stderr is
/// delivered line by line while the process is still running, because that is where approval
/// prompts appear.
/// </summary>
public interface IVaultProcessRunner
{
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onStderrLine,
        CancellationToken ct);
}

/// <inheritdoc/>
public sealed class VaultProcessRunner : IVaultProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        Action<string>? onStderrLine,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            stderr.AppendLine(e.Data);
            onStderrLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();
        // stdout is read to the end rather than line-buffered: `read --field` deliberately emits no
        // trailing newline, so a line reader would drop the last (and only) value.
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new ProcessResult(process.ExitCode, stdout, stderr.ToString());
    }
}

/// <inheritdoc cref="IVaultCliService"/>
public sealed class VaultCliService : IVaultCliService
{
    /// <summary>Overrides PATH lookup. For a runner whose vault lives somewhere unusual.</summary>
    public const string BinaryPathVariable = "PKS_VAULT_CLI";

    /// <summary>The one-liner the preflight prints when the binary is missing.</summary>
    public const string InstallHint =
        "curl -fsSL https://vault.agentics.dk/install.sh | sh";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IVaultProcessRunner _processes;
    private readonly Func<string, string?> _readEnvironment;

    public VaultCliService(IVaultProcessRunner? processes = null, Func<string, string?>? readEnvironment = null)
    {
        _processes = processes ?? new VaultProcessRunner();
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc/>
    public string? FindBinary()
    {
        var overridePath = _readEnvironment(BinaryPathVariable)?.Trim();
        if (!string.IsNullOrEmpty(overridePath))
            return File.Exists(overridePath) ? overridePath : null;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "vault.exe" : "vault";
        var path = _readEnvironment("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<VaultAgentIdentity?> WhoAmIAsync(string identityPath, CancellationToken ct = default)
    {
        // Asked before the file is known to exist, so "no identity file" has to be a null rather
        // than an exception: it is the ordinary state of a host nobody has enrolled yet, and the
        // preflight answers it with an enrolment ceremony, not an error.
        if (!File.Exists(identityPath)) return null;

        var result = await RunAsync(["agent", "whoami", "--identity", identityPath], null, ct);
        if (result.ExitCode != 0)
            throw new VaultCliException($"`vault agent whoami` failed: {Describe(result)}");

        return ParseWhoAmI(result.StandardOutput, identityPath);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VaultGrant>> GrantsAsync(string identityPath, CancellationToken ct = default)
    {
        var result = await RunAsync(["agent", "grants", "--json", "--identity", identityPath], null, ct);
        if (result.ExitCode != 0)
            throw new VaultCliException($"`vault agent grants` failed: {Describe(result)}");

        var json = result.StandardOutput.Trim();
        if (json.Length == 0 || json == "null") return [];
        try
        {
            return JsonSerializer.Deserialize<List<VaultGrant>>(json, JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new VaultCliException($"`vault agent grants --json` returned something unparseable: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<string> ReadFieldAsync(
        string identityPath,
        string vaultId,
        string itemId,
        string field,
        TimeSpan wait,
        Action<string>? onNotice = null,
        CancellationToken ct = default)
    {
        var args = new[]
        {
            "agent", "read",
            "--vault", vaultId,
            "--item", itemId,
            "--field", field,
            "--wait", $"{(int)Math.Max(1, wait.TotalSeconds)}s",
            "--identity", identityPath,
        };
        var result = await RunAsync(args, onNotice, ct);
        if (result.ExitCode != 0)
        {
            // Describe() prints stderr, which is where the vault puts its refusal reasons — and
            // never stdout, which on a partial success could hold part of a key.
            throw new VaultCliException(
                $"`vault agent read` could not read field '{field}' of {itemId}: {Describe(result)}");
        }

        // No Trim(): the CLI emits the value with no trailing newline precisely so it survives
        // capture, and a key's own leading or trailing whitespace is the caller's business.
        if (result.StandardOutput.Length == 0)
            throw new VaultCliException($"`vault agent read` returned an empty value for field '{field}' of {itemId}.");

        return result.StandardOutput;
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, Action<string>? onNotice, CancellationToken ct)
    {
        var binary = FindBinary()
            ?? throw new VaultCliException(
                "The `vault` CLI is not installed on this host, and this project's configuration needs it.\n\n"
                + $"  {InstallHint}\n\n"
                + $"Or set {BinaryPathVariable} to where it already lives.");

        Action<string>? relay = onNotice == null
            ? null
            : line =>
            {
                // The CLI prefixes its own notices. Strip it so the runner's console reads as one
                // voice, and pass everything else through untouched.
                const string prefix = "vault: ";
                onNotice(line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line);
            };

        return await _processes.RunAsync(binary, args, relay, ct);
    }

    /// <summary>
    /// Turns whoami's aligned key/value block into a record. Everything before the first run of
    /// spaces is the key, except <c>service acct</c>, which is two words — it is special-cased
    /// rather than parsed generically, because the alternative is a parser that silently reads
    /// "acct" as a value.
    /// </summary>
    internal static VaultAgentIdentity ParseWhoAmI(string output, string identityPath)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serviceAccount = "";

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;

            if (line.StartsWith("service acct", StringComparison.OrdinalIgnoreCase))
            {
                serviceAccount = line["service acct".Length..].Trim();
                continue;
            }

            var split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split <= 0) continue;
            fields[line[..split].Trim()] = line[split..].Trim();
        }

        // "(none)" is the CLI's way of saying the host can reach nothing: the identity file proves
        // which host is calling, but without a service account every call is refused with a 401
        // before any grant is even consulted.
        var hasServiceAccount = serviceAccount.Length > 0
            && !serviceAccount.StartsWith("(none)", StringComparison.OrdinalIgnoreCase);

        return new VaultAgentIdentity(
            AgentId: fields.GetValueOrDefault("agent", ""),
            Owner: fields.GetValueOrDefault("owner", ""),
            Server: fields.GetValueOrDefault("server", ""),
            Protection: fields.GetValueOrDefault("protection", ""),
            IdentityPath: fields.GetValueOrDefault("identity", identityPath),
            HasServiceAccount: hasServiceAccount);
    }

    private static string Describe(ProcessResult result)
    {
        var stderr = result.StandardError.Trim();

        return stderr.Length > 0 ? stderr : $"exit code {result.ExitCode}";
    }
}

/// <summary>
/// A vault call that did not produce a value. Its message is safe to print — it is built from
/// stderr and never from stdout, which is where secrets are.
/// </summary>
public class VaultCliException : Exception
{
    public VaultCliException(string message) : base(message) { }
    public VaultCliException(string message, Exception inner) : base(message, inner) { }
}
