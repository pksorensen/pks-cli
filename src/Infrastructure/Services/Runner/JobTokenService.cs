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
}

public interface IJobTokenService
{
    string CreateToken(string owner, string repo, string branch, string environment, string appUuid, string jobId);
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

    public string CreateToken(string owner, string repo, string branch, string environment, string appUuid, string jobId)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));

        var exp = new DateTimeOffset(DateTime.UtcNow.Add(_ttl)).ToUnixTimeSeconds();
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            owner,
            repo,
            branch,
            env = environment,
            app_uuid = appUuid,
            job_id = jobId,
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

            return new JobTokenClaims
            {
                Owner = root.GetProperty("owner").GetString() ?? "",
                Repo = root.GetProperty("repo").GetString() ?? "",
                Branch = root.GetProperty("branch").GetString() ?? "",
                Environment = root.GetProperty("env").GetString() ?? "",
                AppUuid = root.GetProperty("app_uuid").GetString() ?? "",
                JobId = root.GetProperty("job_id").GetString() ?? "",
                ExpiresAt = expiresAt
            };
        }
        catch
        {
            return null;
        }
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
