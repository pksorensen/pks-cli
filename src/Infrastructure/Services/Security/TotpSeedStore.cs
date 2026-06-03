using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Security;

public enum VerifyStatus { Ok, NotEnrolled, Invalid, Replay, LockedOut }

public sealed record VerifyOutcome(VerifyStatus Status, string? Detail = null)
{
    public bool Ok => Status == VerifyStatus.Ok;
}

/// <summary>What the user provides at enrollment time. Recovery codes are hashed before storage.</summary>
public sealed record TotpEnrollment(string SecretBase32, IReadOnlyList<string> RecoveryCodesPlain);

/// <summary>
/// Persists the TOTP seed + anti-abuse state at <c>~/.pks-cli/authenticator.json</c> (0600).
/// Deliberately exposes NO way to read back the seed or compute a current code — only
/// <see cref="VerifyAsync"/> (verify a user-supplied code) and enrollment. This is what makes
/// the gate hold even though an agent can run any <c>pks</c> subcommand as the pks user.
/// Verification is single-use per time-step (cross-process locked) and rate-limited with lockout.
/// </summary>
public interface ITotpSeedStore
{
    Task<bool> IsEnrolledAsync();
    Task EnrollAsync(TotpEnrollment enrollment);
    Task<VerifyOutcome> VerifyAsync(string code);
    Task<int> RecoveryCodesRemainingAsync();
    Task ClearAsync();
}

public sealed class TotpSeedStore : ITotpSeedStore
{
    private const int MaxFailuresBeforeLockout = 5;
    private const double BaseLockoutSeconds = 30;
    private const double MaxLockoutSeconds = 1800;

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TotpSeedStore() : this(SecurityFiles.PathFor("authenticator.json"), TimeProvider.System) { }

    public TotpSeedStore(string path, TimeProvider clock)
    {
        _path = path;
        _clock = clock;
    }

    private sealed class RecoveryEntry
    {
        public string Hash { get; set; } = "";
        public string Salt { get; set; } = "";
        public DateTime? UsedUtc { get; set; }
    }

    private sealed class State
    {
        public string ProviderKey { get; set; } = "totp";
        public string SecretBase32 { get; set; } = "";
        public int Digits { get; set; } = TotpService.Digits;
        public int Period { get; set; } = TotpService.PeriodSeconds;
        public string Algorithm { get; set; } = "SHA1";
        public List<RecoveryEntry> RecoveryCodes { get; set; } = new();
        public DateTime EnrolledUtc { get; set; }
        public int FailedAttempts { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
        public List<long> ConsumedSteps { get; set; } = new();
    }

    private static State? ReadFile(FileStream fs)
    {
        fs.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, leaveOpen: true);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<State>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static void WriteFile(FileStream fs, State state)
    {
        fs.Seek(0, SeekOrigin.Begin);
        fs.SetLength(0);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush();
    }

    /// <summary>Open the state file with an exclusive cross-process lock (retry briefly on contention).</summary>
    private async Task<FileStream> OpenExclusiveAsync(FileMode mode)
    {
        SecurityFiles.EnsureDirectory(_path);
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(_path, mode, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(40);
            }
        }
    }

    public async Task<bool> IsEnrolledAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return false;
            using var fs = await OpenExclusiveAsync(FileMode.Open);
            var state = ReadFile(fs);
            return state != null && !string.IsNullOrEmpty(state.SecretBase32);
        }
        finally { _lock.Release(); }
    }

    public async Task EnrollAsync(TotpEnrollment enrollment)
    {
        await _lock.WaitAsync();
        try
        {
            var state = new State
            {
                SecretBase32 = enrollment.SecretBase32,
                EnrolledUtc = _clock.GetUtcNow().UtcDateTime,
                RecoveryCodes = enrollment.RecoveryCodesPlain.Select(c =>
                {
                    var (hash, salt) = TotpService.HashRecoveryCode(c);
                    return new RecoveryEntry { Hash = hash, Salt = salt };
                }).ToList(),
            };
            using var fs = await OpenExclusiveAsync(FileMode.Create);
            WriteFile(fs, state);
            SecurityFiles.Restrict(_path);
        }
        finally { _lock.Release(); }
    }

    public async Task<VerifyOutcome> VerifyAsync(string code)
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return new VerifyOutcome(VerifyStatus.NotEnrolled);
            using var fs = await OpenExclusiveAsync(FileMode.Open);
            var state = ReadFile(fs);
            if (state == null || string.IsNullOrEmpty(state.SecretBase32))
                return new VerifyOutcome(VerifyStatus.NotEnrolled);

            var now = _clock.GetUtcNow();
            if (state.LockoutUntilUtc is { } until && until > now.UtcDateTime)
                return new VerifyOutcome(VerifyStatus.LockedOut, $"Too many attempts — locked until {until:HH:mm:ss} UTC.");

            var trimmed = (code ?? string.Empty).Trim();

            // Recovery code path: anything that isn't a bare N-digit TOTP code.
            var isDigits = trimmed.Length == state.Digits && trimmed.All(char.IsDigit);
            if (!isDigits)
            {
                foreach (var rc in state.RecoveryCodes)
                {
                    if (rc.UsedUtc == null && TotpService.VerifyRecoveryCode(trimmed, rc.Hash, rc.Salt))
                    {
                        rc.UsedUtc = now.UtcDateTime;
                        state.FailedAttempts = 0;
                        state.LockoutUntilUtc = null;
                        WriteFile(fs, state);
                        return new VerifyOutcome(VerifyStatus.Ok, "recovery");
                    }
                }
            }
            else
            {
                var step = TotpService.TimeStep(now);
                for (long s = step - 1; s <= step + 1; s++)
                {
                    if (!TotpService.CodesEqual(trimmed, TotpService.ComputeCode(state.SecretBase32, s))) continue;

                    if (state.ConsumedSteps.Contains(s))
                        return new VerifyOutcome(VerifyStatus.Replay, "That code was already used.");

                    state.ConsumedSteps.Add(s);
                    state.ConsumedSteps.RemoveAll(x => x < step - 1); // expired steps can't be reused anyway
                    state.FailedAttempts = 0;
                    state.LockoutUntilUtc = null;
                    WriteFile(fs, state);
                    return new VerifyOutcome(VerifyStatus.Ok);
                }
            }

            // No match → count the failure and maybe lock out.
            state.FailedAttempts++;
            if (state.FailedAttempts >= MaxFailuresBeforeLockout)
            {
                var over = state.FailedAttempts - MaxFailuresBeforeLockout;
                var secs = Math.Min(BaseLockoutSeconds * Math.Pow(2, over), MaxLockoutSeconds);
                state.LockoutUntilUtc = now.UtcDateTime.AddSeconds(secs);
            }
            WriteFile(fs, state);
            return state.LockoutUntilUtc != null
                ? new VerifyOutcome(VerifyStatus.LockedOut, "Too many attempts — temporarily locked.")
                : new VerifyOutcome(VerifyStatus.Invalid, "Invalid code.");
        }
        finally { _lock.Release(); }
    }

    public async Task<int> RecoveryCodesRemainingAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return 0;
            using var fs = await OpenExclusiveAsync(FileMode.Open);
            var state = ReadFile(fs);
            return state?.RecoveryCodes.Count(r => r.UsedUtc == null) ?? 0;
        }
        finally { _lock.Release(); }
    }

    public async Task ClearAsync()
    {
        await _lock.WaitAsync();
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _lock.Release(); }
    }
}
