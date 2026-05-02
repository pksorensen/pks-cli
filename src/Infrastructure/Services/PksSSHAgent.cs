using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services;

/// <summary>
/// A private SSH agent that loads an OpenSSH private key (ed25519 or RSA) and serves
/// the SSH agent protocol (draft-miller-ssh-agent-04) on a named pipe (Windows) or
/// Unix domain socket (Linux/macOS). Pass <see cref="SocketPath"/> as SSH_AUTH_SOCK
/// to VS Code Remote SSH so it can authenticate without needing an ~/.ssh/config entry.
/// </summary>
public sealed class PksSSHAgent : IAsyncDisposable
{
    // SSH agent protocol constants
    private const byte SSH_AGENT_FAILURE = 5;
    private const byte SSH2_AGENT_IDENTITIES_ANSWER = 12;
    private const byte SSH2_AGENTC_REQUEST_IDENTITIES = 11;
    private const byte SSH2_AGENTC_SIGN_REQUEST = 13;
    private const byte SSH2_AGENT_SIGN_RESPONSE = 14;

    // RSA sign flags
    private const uint SSH_AGENT_RSA_SHA2_256 = 2;
    private const uint SSH_AGENT_RSA_SHA2_512 = 4;

    private readonly string _keyPath;
    private readonly string _socketGuid;
    private readonly CancellationTokenSource _cts = new();

    private SshKeyInfo? _keyInfo;
    private Task? _listenerTask;
    private bool _disposed;

    /// <summary>
    /// The path to pass as SSH_AUTH_SOCK. On Windows this is a named pipe path;
    /// on Linux/macOS it is a Unix socket path.
    /// </summary>
    public string SocketPath { get; }

    public PksSSHAgent(string keyPath)
    {
        _keyPath = keyPath ?? throw new ArgumentNullException(nameof(keyPath));
        _socketGuid = Guid.NewGuid().ToString("N");

        if (OperatingSystem.IsWindows())
            SocketPath = $@"\\.\pipe\pks-ssh-agent-{_socketGuid}";
        else
            SocketPath = $"/tmp/pks-ssh-agent-{_socketGuid}.sock";
    }

    /// <summary>Loads the key and starts listening in the background.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _keyInfo = await Task.Run(() => LoadKey(_keyPath), cancellationToken);

        _listenerTask = OperatingSystem.IsWindows()
            ? ListenNamedPipeAsync(_cts.Token)
            : ListenUnixSocketAsync(_cts.Token);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Transport: Named Pipe (Windows)
    // ──────────────────────────────────────────────────────────────────────────

    private async Task ListenNamedPipeAsync(CancellationToken ct)
    {
        var pipeName = $"pks-ssh-agent-{_socketGuid}";
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(ct);
                _ = HandleClientAsync(pipe, ct);
            }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch { pipe.Dispose(); }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Transport: Unix Domain Socket (Linux / macOS)
    // ──────────────────────────────────────────────────────────────────────────

    private async Task ListenUnixSocketAsync(CancellationToken ct)
    {
        if (File.Exists(SocketPath)) File.Delete(SocketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        listener.Listen(8);

        await using var reg = ct.Register(() => listener.Close());
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct); }
            catch { break; }
            _ = HandleClientSocketAsync(client, ct);
        }

        if (File.Exists(SocketPath)) try { File.Delete(SocketPath); } catch { }
    }

    private async Task HandleClientSocketAsync(Socket client, CancellationToken ct)
    {
        await using var ns = new NetworkStream(client, ownsSocket: true);
        await HandleClientAsync(ns, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Protocol handler (shared between both transports)
    // ──────────────────────────────────────────────────────────────────────────

    private async Task HandleClientAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Read 4-byte big-endian length prefix
                var lenBuf = new byte[4];
                if (!await ReadExactAsync(stream, lenBuf, ct)) break;
                int msgLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                if (msgLen is <= 0 or > 256 * 1024) break;

                var msg = new byte[msgLen];
                if (!await ReadExactAsync(stream, msg, ct)) break;

                byte msgType = msg[0];
                byte[] response = msgType switch
                {
                    SSH2_AGENTC_REQUEST_IDENTITIES => HandleRequestIdentities(),
                    SSH2_AGENTC_SIGN_REQUEST => HandleSignRequest(msg),
                    _ => BuildFailure()
                };

                // Write 4-byte length + response
                var respLen = new byte[4];
                int rl = response.Length;
                respLen[0] = (byte)(rl >> 24);
                respLen[1] = (byte)(rl >> 16);
                respLen[2] = (byte)(rl >> 8);
                respLen[3] = (byte)rl;

                await stream.WriteAsync(respLen, ct);
                await stream.WriteAsync(response, ct);
                await stream.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* client disconnected */ }
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SSH2_AGENTC_REQUEST_IDENTITIES (11) → SSH2_AGENT_IDENTITIES_ANSWER (12)
    // ──────────────────────────────────────────────────────────────────────────

    private byte[] HandleRequestIdentities()
    {
        if (_keyInfo == null) return BuildFailure();

        var w = new SshWriter();
        w.WriteByte(SSH2_AGENT_IDENTITIES_ANSWER);
        w.WriteUint32(1);                              // nkeys = 1
        w.WriteString(_keyInfo.PublicKeyBlob);         // key blob
        w.WriteString(Encoding.UTF8.GetBytes(_keyInfo.Comment)); // comment
        return w.ToArray();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SSH2_AGENTC_SIGN_REQUEST (13) → SSH2_AGENT_SIGN_RESPONSE (14)
    // ──────────────────────────────────────────────────────────────────────────

    private byte[] HandleSignRequest(byte[] msg)
    {
        if (_keyInfo == null) return BuildFailure();

        try
        {
            var r = new SshReader(msg, 1); // skip message type byte
            var keyBlob = r.ReadString();
            var data = r.ReadString();
            var flags = r.ReadUint32();

            // Verify the key blob matches our key
            if (!keyBlob.SequenceEqual(_keyInfo.PublicKeyBlob))
                return BuildFailure();

            byte[] signatureBlob = _keyInfo.KeyType switch
            {
                SshKeyType.Ed25519 => SignEd25519(data),
                SshKeyType.Rsa => SignRsa(data, flags),
                _ => throw new CryptographicException("Unsupported key type")
            };

            var w = new SshWriter();
            w.WriteByte(SSH2_AGENT_SIGN_RESPONSE);
            w.WriteString(signatureBlob);
            return w.ToArray();
        }
        catch
        {
            return BuildFailure();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Signing implementations
    // ──────────────────────────────────────────────────────────────────────────

    private byte[] SignEd25519(byte[] data)
    {
        var rawSig = Ed25519OpenSsl.Sign(_keyInfo!.Ed25519PrivateSeed!, data);

        // signature_blob = string("ssh-ed25519") + string(raw64)
        var sw = new SshWriter();
        sw.WriteString(Encoding.UTF8.GetBytes("ssh-ed25519"));
        sw.WriteString(rawSig);
        return sw.ToArray();
    }

    private byte[] SignRsa(byte[] data, uint flags)
    {
        string algName;
        HashAlgorithmName hashAlg;
        RSASignaturePadding padding = RSASignaturePadding.Pkcs1;

        if ((flags & SSH_AGENT_RSA_SHA2_512) != 0) { algName = "rsa-sha2-512"; hashAlg = HashAlgorithmName.SHA512; }
        else if ((flags & SSH_AGENT_RSA_SHA2_256) != 0) { algName = "rsa-sha2-256"; hashAlg = HashAlgorithmName.SHA256; }
        else { algName = "ssh-rsa"; hashAlg = HashAlgorithmName.SHA1; }

        var rawSig = _keyInfo!.RsaKey!.SignData(data, hashAlg, padding);

        var sw = new SshWriter();
        sw.WriteString(Encoding.UTF8.GetBytes(algName));
        sw.WriteString(rawSig);
        return sw.ToArray();
    }

    private static byte[] BuildFailure() => new[] { SSH_AGENT_FAILURE };

    // ──────────────────────────────────────────────────────────────────────────
    // Key loading: OpenSSH private key parser
    // ──────────────────────────────────────────────────────────────────────────

    private static SshKeyInfo LoadKey(string path)
    {
        var text = File.ReadAllText(path);

        if (text.Contains("BEGIN OPENSSH PRIVATE KEY"))
            return ParseOpenSshKey(text);

        throw new NotSupportedException(
            "Only unencrypted OpenSSH private key format (BEGIN OPENSSH PRIVATE KEY) is supported.");
    }

    private static SshKeyInfo ParseOpenSshKey(string pem)
    {
        var match = Regex.Match(pem,
            @"-----BEGIN OPENSSH PRIVATE KEY-----(.+?)-----END OPENSSH PRIVATE KEY-----",
            RegexOptions.Singleline);
        if (!match.Success)
            throw new FormatException("Could not find OPENSSH PRIVATE KEY block");

        var b64 = Regex.Replace(match.Groups[1].Value, @"\s", "");
        var data = Convert.FromBase64String(b64);

        // Magic: "openssh-key-v1\0" = 15 bytes
        const string magic = "openssh-key-v1";
        if (data.Length < 16 || Encoding.ASCII.GetString(data, 0, magic.Length) != magic)
            throw new FormatException("Not an OpenSSH private key");

        var r = new SshReader(data, 15); // skip magic + null

        var cipher = r.ReadString();
        if (!Encoding.ASCII.GetString(cipher).Equals("none", StringComparison.Ordinal))
            throw new NotSupportedException("Encrypted OpenSSH private keys are not supported.");

        r.ReadString(); // kdf (none)
        r.ReadString(); // kdf options (empty)

        uint nkeys = r.ReadUint32();
        if (nkeys != 1)
            throw new FormatException($"Expected 1 key, found {nkeys}");

        // Public key blob (outer)
        var pubKeyBlob = r.ReadString();

        // Private section
        var privBlob = r.ReadString();
        var rp = new SshReader(privBlob, 0);
        uint check1 = rp.ReadUint32();
        uint check2 = rp.ReadUint32();
        if (check1 != check2)
            throw new CryptographicException("Passphrase checksum mismatch (key may be encrypted)");

        var keyType = Encoding.ASCII.GetString(rp.ReadString());
        return keyType switch
        {
            "ssh-ed25519" => ParseEd25519Key(rp, pubKeyBlob),
            "ssh-rsa" => ParseRsaKey(rp, pubKeyBlob),
            _ => throw new NotSupportedException($"Key type '{keyType}' is not supported.")
        };
    }

    private static SshKeyInfo ParseEd25519Key(SshReader rp, byte[] pubKeyBlob)
    {
        var pub32 = rp.ReadString();  // 32-byte public key
        var priv64 = rp.ReadString();  // 64-byte private+public
        var comment = Encoding.UTF8.GetString(rp.ReadString());

        if (priv64.Length < 32)
            throw new FormatException("Invalid ed25519 private key length");

        var privSeed = priv64[..32];

        return new SshKeyInfo
        {
            KeyType = SshKeyType.Ed25519,
            PublicKeyBlob = pubKeyBlob,
            Ed25519PrivateSeed = privSeed,
            Comment = comment
        };
    }

    private static SshKeyInfo ParseRsaKey(SshReader rp, byte[] pubKeyBlob)
    {
        // RSA private key fields (OpenSSH order):
        // n, e, d, iqmp, p, q
        var n = rp.ReadMpint();
        var e = rp.ReadMpint();
        var d = rp.ReadMpint();
        var iqmp = rp.ReadMpint();
        var p = rp.ReadMpint();
        var q = rp.ReadMpint();
        var comment = Encoding.UTF8.GetString(rp.ReadString());

        // Reconstruct CRT parameters: dp = d mod (p-1), dq = d mod (q-1)
        var bn = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(n), System.Globalization.NumberStyles.HexNumber);
        var be = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(e), System.Globalization.NumberStyles.HexNumber);
        var bd = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(d), System.Globalization.NumberStyles.HexNumber);
        var bp = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(p), System.Globalization.NumberStyles.HexNumber);
        var bq = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(q), System.Globalization.NumberStyles.HexNumber);
        var biqmp = System.Numerics.BigInteger.Parse("0" + Convert.ToHexString(iqmp), System.Globalization.NumberStyles.HexNumber);
        var bdp = bd % (bp - 1);
        var bdq = bd % (bq - 1);

        static byte[] BigIntToUnsignedBigEndian(System.Numerics.BigInteger v)
        {
            var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            return bytes;
        }

        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = BigIntToUnsignedBigEndian(bn),
            Exponent = BigIntToUnsignedBigEndian(be),
            D = BigIntToUnsignedBigEndian(bd),
            P = BigIntToUnsignedBigEndian(bp),
            Q = BigIntToUnsignedBigEndian(bq),
            DP = BigIntToUnsignedBigEndian(bdp),
            DQ = BigIntToUnsignedBigEndian(bdq),
            InverseQ = BigIntToUnsignedBigEndian(biqmp),
        });

        return new SshKeyInfo
        {
            KeyType = SshKeyType.Rsa,
            PublicKeyBlob = pubKeyBlob,
            RsaKey = rsa,
            Comment = comment
        };
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IAsyncDisposable
    // ──────────────────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        if (_listenerTask != null)
        {
            try { await _listenerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* expected: cancel or timeout */ }
        }
        _cts.Dispose();
        _keyInfo?.RsaKey?.Dispose();

        if (!OperatingSystem.IsWindows() && File.Exists(SocketPath))
        {
            try { File.Delete(SocketPath); } catch { }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Internal types
    // ──────────────────────────────────────────────────────────────────────────

    private enum SshKeyType { Ed25519, Rsa }

    private sealed class SshKeyInfo
    {
        public SshKeyType KeyType { get; init; }
        public byte[] PublicKeyBlob { get; init; } = Array.Empty<byte>();
        public byte[]? Ed25519PrivateSeed { get; init; }
        public RSA? RsaKey { get; init; }
        public string Comment { get; init; } = "";
    }

    /// <summary>Reads SSH wire-format fields from a byte array.</summary>
    private sealed class SshReader(byte[] data, int offset)
    {
        private int _pos = offset;

        public uint ReadUint32()
        {
            uint v = ((uint)data[_pos] << 24) | ((uint)data[_pos + 1] << 16)
                   | ((uint)data[_pos + 2] << 8) | data[_pos + 3];
            _pos += 4;
            return v;
        }

        public byte[] ReadString()
        {
            int n = (int)ReadUint32();
            var s = data[_pos..(_pos + n)];
            _pos += n;
            return s;
        }

        /// <summary>
        /// Read an SSH "mpint": big-endian, may have leading 0x00 sign byte for positive numbers.
        /// Returns the absolute value as big-endian bytes (no leading zeros).
        /// </summary>
        public byte[] ReadMpint()
        {
            var raw = ReadString();
            // strip leading sign byte(s) used for positive representation
            int start = 0;
            while (start < raw.Length - 1 && raw[start] == 0x00) start++;
            return raw[start..];
        }
    }

    /// <summary>Builds SSH wire-format messages.</summary>
    private sealed class SshWriter
    {
        private readonly System.IO.MemoryStream _ms = new();

        public void WriteByte(byte b) => _ms.WriteByte(b);

        public void WriteUint32(uint v)
        {
            _ms.WriteByte((byte)(v >> 24));
            _ms.WriteByte((byte)(v >> 16));
            _ms.WriteByte((byte)(v >> 8));
            _ms.WriteByte((byte)v);
        }

        public void WriteString(byte[] data)
        {
            WriteUint32((uint)data.Length);
            _ms.Write(data);
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// OpenSSL Ed25519 signing via P/Invoke (no BouncyCastle / SSH.NET needed)
// .NET 10 does not yet expose Ed25519 in System.Security.Cryptography,
// but OpenSSL (libcrypto) is always present on Linux/macOS.
// ──────────────────────────────────────────────────────────────────────────────
internal static class Ed25519OpenSsl
{
    // NID_ED25519 = 1087 in OpenSSL 1.1+
    private const int EVP_PKEY_ED25519 = 1087;

    [DllImport("libcrypto", EntryPoint = "EVP_PKEY_new_raw_private_key")]
    private static extern IntPtr EvpPkeyNewRawPrivateKey(int type, IntPtr engine, byte[] key, UIntPtr keyLen);

    [DllImport("libcrypto", EntryPoint = "EVP_PKEY_get_raw_public_key")]
    private static extern int EvpPkeyGetRawPublicKey(IntPtr pkey, byte[] pub, ref UIntPtr len);

    [DllImport("libcrypto", EntryPoint = "EVP_PKEY_free")]
    private static extern void EvpPkeyFree(IntPtr pkey);

    [DllImport("libcrypto", EntryPoint = "EVP_MD_CTX_new")]
    private static extern IntPtr EvpMdCtxNew();

    [DllImport("libcrypto", EntryPoint = "EVP_MD_CTX_free")]
    private static extern void EvpMdCtxFree(IntPtr ctx);

    [DllImport("libcrypto", EntryPoint = "EVP_DigestSignInit")]
    private static extern int EvpDigestSignInit(IntPtr ctx, IntPtr pctx, IntPtr md, IntPtr engine, IntPtr pkey);

    [DllImport("libcrypto", EntryPoint = "EVP_DigestSign")]
    private static extern int EvpDigestSign(IntPtr ctx, byte[]? sig, ref UIntPtr sigLen, byte[] tbs, UIntPtr tbsLen);

    public static byte[] Sign(byte[] privateKey32, byte[] data)
    {
        var pkey = EvpPkeyNewRawPrivateKey(EVP_PKEY_ED25519, IntPtr.Zero, privateKey32, (UIntPtr)32);
        if (pkey == IntPtr.Zero)
            throw new CryptographicException("EVP_PKEY_new_raw_private_key failed for Ed25519");
        try
        {
            var ctx = EvpMdCtxNew();
            if (ctx == IntPtr.Zero)
                throw new CryptographicException("EVP_MD_CTX_new failed");
            try
            {
                if (EvpDigestSignInit(ctx, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, pkey) != 1)
                    throw new CryptographicException("EVP_DigestSignInit failed for Ed25519");

                UIntPtr sigLen = UIntPtr.Zero;
                if (EvpDigestSign(ctx, null, ref sigLen, data, (UIntPtr)data.Length) != 1)
                    throw new CryptographicException("EVP_DigestSign (query length) failed");

                var sig = new byte[(int)sigLen];
                if (EvpDigestSign(ctx, sig, ref sigLen, data, (UIntPtr)data.Length) != 1)
                    throw new CryptographicException("EVP_DigestSign (sign) failed");

                return sig;
            }
            finally { EvpMdCtxFree(ctx); }
        }
        finally { EvpPkeyFree(pkey); }
    }

    /// <summary>Derives the 32-byte public key from a 32-byte private seed.</summary>
    public static byte[] GetPublicKey(byte[] privateKey32)
    {
        var pkey = EvpPkeyNewRawPrivateKey(EVP_PKEY_ED25519, IntPtr.Zero, privateKey32, (UIntPtr)32);
        if (pkey == IntPtr.Zero)
            throw new CryptographicException("EVP_PKEY_new_raw_private_key failed for Ed25519");
        try
        {
            UIntPtr len = (UIntPtr)32;
            var pub = new byte[32];
            if (EvpPkeyGetRawPublicKey(pkey, pub, ref len) != 1)
                throw new CryptographicException("EVP_PKEY_get_raw_public_key failed");
            return pub;
        }
        finally { EvpPkeyFree(pkey); }
    }
}
