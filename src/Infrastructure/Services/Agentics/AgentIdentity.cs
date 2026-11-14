using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Agentics;

/// <summary>
/// An AI agent's own identity on an Agentics server: a P-256 key the agent created
/// itself and registered by joining an artifact invite (<c>pks agentics agent join</c>).
///
/// ES256 rather than the Ed25519 the installation key uses: .NET has P-256 built in on
/// every OS, so this works on a bare Windows laptop with no OpenSSL — which is exactly
/// where a Codex or Claude Desktop user runs it.
///
/// The server never hands out a secret. Every request carries a fresh JWT the agent
/// signs (<c>iss=agentics-agent</c>, <c>sub</c> = the key's RFC 7638 thumbprint,
/// <c>aud</c> = the server host, five minutes), so nothing long-lived exists to steal
/// except the key file itself.
///
/// Stored in <c>~/.pks-cli/agents/&lt;host&gt;.json</c> — the user's home, not the
/// workspace, so it is not committed by accident and survives opening another folder.
/// <c>PKS_AGENT_KEY_DIR</c> overrides the directory.
/// </summary>
public sealed class AgentIdentity
{
    public const string Issuer = "agentics-agent";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ECDsa _key;

    public string Id { get; }
    public string Server { get; }
    public string? Name { get; set; }
    public string? OnBehalfOf { get; set; }
    public DateTimeOffset CreatedAt { get; }

    private AgentIdentity(ECDsa key, string server, string? name, string? onBehalfOf, DateTimeOffset createdAt)
    {
        _key = key;
        Server = server;
        Name = name;
        OnBehalfOf = onBehalfOf;
        CreatedAt = createdAt;
        Id = Thumbprint(PublicJwk());
    }

    public static AgentIdentity Create(string server) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), NormalizeServer(server), null, null, DateTimeOffset.UtcNow);

    /// <summary>The public half as a JWK — only kty, crv, x, y.</summary>
    public Dictionary<string, string> PublicJwk()
    {
        var p = _key.ExportParameters(false);
        return new Dictionary<string, string>
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url(p.Q.X!),
            ["y"] = Base64Url(p.Q.Y!),
        };
    }

    /// <summary>RFC 7638: sha256 over the required members in lexicographic order.</summary>
    public static string Thumbprint(IReadOnlyDictionary<string, string> jwk)
    {
        var canonical = $"{{\"crv\":\"{jwk["crv"]}\",\"kty\":\"{jwk["kty"]}\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>A fresh ES256 JWT for <paramref name="audience"/> (the server's host).</summary>
    public string MintJwt(string audience, DateTimeOffset? now = null)
    {
        var iat = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"JWT\"}"));

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", Issuer);
            writer.WriteString("sub", Id);
            writer.WriteString("aud", audience);
            writer.WriteNumber("iat", iat);
            writer.WriteNumber("exp", iat + (long)Lifetime.TotalSeconds);
            writer.WriteEndObject();
        }

        var signingInput = $"{header}.{Base64Url(buffer.ToArray())}";
        // .NET's default ECDSA signature format is IEEE P1363 (r||s) — what JWS requires.
        var signature = _key.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    /// <summary>The audience a JWT for this server names: its host[:port].</summary>
    public string Audience => new Uri(Server).Authority;

    // ── storage ─────────────────────────────────────────────────────────────

    public static string KeyDirectory(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var overridden = env("PKS_AGENT_KEY_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "agents");
    }

    public static string KeyPath(string server, string? directory = null) =>
        Path.Combine(directory ?? KeyDirectory(), SafeFileName(new Uri(NormalizeServer(server)).Authority) + ".json");

    public static AgentIdentity? Load(string server, string? directory = null)
    {
        var path = KeyPath(server, directory);
        if (!File.Exists(path)) return null;

        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"the agent key file {path} is empty or unreadable");
        var key = ECDsa.Create();
        key.ImportFromPem(stored.PrivateKeyPem);
        return new AgentIdentity(key, stored.Server ?? NormalizeServer(server), stored.Name, stored.OnBehalfOf, stored.CreatedAt);
    }

    /// <summary>Every identity stored in the key directory, one per server.</summary>
    public static IReadOnlyList<AgentIdentity> LoadAll(string? directory = null)
    {
        var dir = directory ?? KeyDirectory();
        if (!Directory.Exists(dir)) return Array.Empty<AgentIdentity>();
        var list = new List<AgentIdentity>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
                if (stored?.Server is null) continue;
                var key = ECDsa.Create();
                key.ImportFromPem(stored.PrivateKeyPem);
                list.Add(new AgentIdentity(key, stored.Server, stored.Name, stored.OnBehalfOf, stored.CreatedAt));
            }
            catch
            {
                // a foreign or damaged file — skip it rather than fail every command
            }
        }
        return list;
    }

    public string Save(string? directory = null)
    {
        var path = KeyPath(Server, directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stored = new Stored
        {
            Id = Id,
            Server = Server,
            Name = Name,
            OnBehalfOf = OnBehalfOf,
            CreatedAt = CreatedAt,
            PrivateKeyPem = _key.ExportPkcs8PrivateKeyPem(),
        };
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });

        // Write to a sibling and rename, with the mode set before any key bytes land.
        var tmp = path + ".tmp";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            fs.Write(Encoding.UTF8.GetBytes(json));
        }
        else
        {
            // %USERPROFILE% is already private to the user on Windows.
            File.WriteAllText(tmp, json);
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    public static string NormalizeServer(string server)
    {
        var s = server.Trim().TrimEnd('/');
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = (s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.StartsWith("127.0.0.1") ? "http://" : "https://") + s;
        var uri = new Uri(s);
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string SafeFileName(string authority) =>
        string.Concat(authority.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Stored
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("onBehalfOf")] public string? OnBehalfOf { get; set; }
        [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
        [JsonPropertyName("privateKeyPem")] public string PrivateKeyPem { get; set; } = "";
    }
}

/// <summary>What the agent tells the server about where it runs. Self-reported.</summary>
public static class AgentClientInfo
{
    public static Dictionary<string, string?> Collect(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return new Dictionary<string, string?>
        {
            ["machine"] = Environment.MachineName,
            ["os"] = RuntimeInformation.OSDescription,
            ["user"] = Environment.UserName,
            ["harness"] = DetectHarness(env),
            ["pksVersion"] = typeof(AgentClientInfo).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
                ? attrs[0].InformationalVersion.Split('+')[0]
                : null,
        };
    }

    public static string? DetectHarness(Func<string, string?> env)
    {
        if (!string.IsNullOrEmpty(env("CLAUDECODE"))) return "claude-code";
        if (!string.IsNullOrEmpty(env("CODEX_SANDBOX")) || !string.IsNullOrEmpty(env("CODEX_THREAD_ID"))
            || !string.IsNullOrEmpty(env("CODEX_HOME")) || !string.IsNullOrEmpty(env("CODEX_SANDBOX_NETWORK_DISABLED")))
            return "codex";
        if (!string.IsNullOrEmpty(env("CURSOR_TRACE_ID"))) return "cursor";
        return null;
    }
}
