using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

public class JobTokenClaims
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Environment { get; set; } = "";
    public string AppUuid { get; set; } = "";
    public string JobId { get; set; } = "";
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Every repository this job is allowed to ask for a credential for, as "owner/name".
    /// A single job legitimately touches more than one — a station that clones a repo and
    /// then fetches its submodules needs one entry per remote — so entitlement is a list
    /// rather than the single <see cref="Repo"/> the token was minted around.
    /// </summary>
    public IReadOnlyList<string> Repos { get; set; } = [];

    /// <summary>
    /// Case-insensitive, because GitHub treats owner and repository names that way and a
    /// clone URL's casing is whatever the person who wrote it typed.
    /// </summary>
    public bool IsEntitledTo(string owner, string repo) =>
        Repos.Any(r => string.Equals(r, $"{owner}/{repo}", StringComparison.OrdinalIgnoreCase));
}

public interface IJobTokenService
{
    /// <param name="entitledRepos">
    /// Repositories this token may fetch credentials for. Null means "just the one this
    /// token names", which is what the GitHub Actions path wants and why it never passes it.
    /// </param>
    string CreateToken(
        string owner,
        string repo,
        string branch,
        string environment,
        string appUuid,
        string jobId,
        IReadOnlyList<string>? entitledRepos = null);

    JobTokenClaims? ValidateToken(string token);
}

public class JobTokenService : IJobTokenService
{
    private readonly byte[] _key;
    private readonly TimeSpan _ttl;

    public JobTokenService(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromHours(4);
        _key = new byte[32];
        RandomNumberGenerator.Fill(_key);
    }

    public string CreateToken(
        string owner,
        string repo,
        string branch,
        string environment,
        string appUuid,
        string jobId,
        IReadOnlyList<string>? entitledRepos = null)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));

        var repos = entitledRepos is { Count: > 0 }
            ? entitledRepos.ToArray()
            : new[] { $"{owner}/{repo}" };

        var exp = new DateTimeOffset(DateTime.UtcNow.Add(_ttl)).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            owner,
            repo,
            branch,
            env = environment,
            app_uuid = appUuid,
            job_id = jobId,
            repos,
            exp
        }));

        var signature = Sign($"{header}.{payload}");

        return $"{header}.{payload}.{signature}";
    }

    public JobTokenClaims? ValidateToken(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
            return null;

        var expectedSignature = Sign($"{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedSignature),
                Encoding.UTF8.GetBytes(parts[2])))
            return null;

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            var exp = root.GetProperty("exp").GetInt64();
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;

            if (DateTime.UtcNow >= expiresAt)
                return null;

            var owner = root.GetProperty("owner").GetString() ?? "";
            var repo = root.GetProperty("repo").GetString() ?? "";

            return new JobTokenClaims
            {
                Owner = owner,
                Repo = repo,
                Branch = root.GetProperty("branch").GetString() ?? "",
                Environment = root.GetProperty("env").GetString() ?? "",
                AppUuid = root.GetProperty("app_uuid").GetString() ?? "",
                JobId = root.GetProperty("job_id").GetString() ?? "",
                Repos = ReadRepos(root, owner, repo),
                ExpiresAt = expiresAt
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A token minted before the claim existed still validates; it is entitled to exactly
    /// the repository it names. Tokens do not survive a runner restart — the signing key is
    /// per-process — so this is belt-and-braces rather than a wire-compatibility promise.
    /// </summary>
    private static IReadOnlyList<string> ReadRepos(JsonElement root, string owner, string repo)
    {
        if (!root.TryGetProperty("repos", out var repos) || repos.ValueKind != JsonValueKind.Array)
            return [$"{owner}/{repo}"];

        var list = repos.EnumerateArray()
            .Select(r => r.GetString())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .ToArray();

        return list.Length > 0 ? list : [$"{owner}/{repo}"];
    }

    private string Sign(string input)
    {
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
