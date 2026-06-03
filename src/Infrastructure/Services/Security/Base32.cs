namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// RFC 4648 Base32 (no padding). Used for TOTP secrets and recovery codes — the alphabet
/// authenticator apps expect. Decoding is tolerant of spaces, dashes and lower-case.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Alphabet[(buffer >> bitsLeft) & 31]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string input)
    {
        if (string.IsNullOrEmpty(input)) return Array.Empty<byte>();
        var s = input.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in s)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException($"Invalid Base32 character '{c}'.");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                bytes.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return bytes.ToArray();
    }
}
