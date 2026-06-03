using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// RFC 6238 TOTP (HMAC-SHA1, 6 digits, 30s period) plus enrollment helpers. Pure functions —
/// no persistence, no state, and it never logs or exposes a stored seed. The only place a
/// secret leaves this type is <see cref="GenerateSecretBase32"/> at enrollment time.
/// </summary>
public static class TotpService
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    /// <summary>Generate a 160-bit random secret, Base32-encoded for authenticator apps.</summary>
    public static string GenerateSecretBase32() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>The 30-second time step for the given instant.</summary>
    public static long TimeStep(DateTimeOffset utc) => utc.ToUnixTimeSeconds() / PeriodSeconds;

    /// <summary>Compute the code for a Base32 secret and time step.</summary>
    public static string ComputeCode(string secretBase32, long step, int digits = Digits)
    {
        var key = Base32.Decode(secretBase32);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        int offset = hash[19] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);
        int mod = 1;
        for (int i = 0; i < digits; i++) mod *= 10;
        return (binary % mod).ToString().PadLeft(digits, '0');
    }

    public static bool CodesEqual(string a, string b)
    {
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    public static string BuildOtpAuthUri(string secretBase32, string account, string issuer = "pks-cli")
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        return $"otpauth://totp/{label}?secret={secretBase32}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    /// <summary>Generate N high-entropy recovery codes, grouped as XXXXX-XXXXX.</summary>
    public static IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var raw = Base32.Encode(RandomNumberGenerator.GetBytes(7)); // >= 11 chars
            codes.Add($"{raw[..5]}-{raw[5..10]}");
        }
        return codes;
    }

    /// <summary>Normalize a recovery code for hashing/compare (strip separators, upper-case).</summary>
    public static string NormalizeRecovery(string code) =>
        code.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();

    public static (string hash, string salt) HashRecoveryCode(string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(NormalizeRecovery(code), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyRecoveryCode(string code, string hashB64, string saltB64)
    {
        var salt = Convert.FromBase64String(saltB64);
        var hash = Rfc2898DeriveBytes.Pbkdf2(NormalizeRecovery(code), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(hashB64));
    }
}
