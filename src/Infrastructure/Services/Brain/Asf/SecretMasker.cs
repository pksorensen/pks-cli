using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Masks credentials out of session content. Spec: docs/specs/asf/02-levels.md.
///
/// There is no ASF level at which secrets are transported: `--level all` means
/// "all event kinds and all fields", not "unfiltered". This runs on every field
/// that can carry copied-in content, at every level, before hashing — so a hash
/// never confirms a guess at an unmasked secret.
///
/// The built-in pattern set is a port of
/// src/apps/www-site/src/lib/masking.ts in the agentic-live-www workspace, with
/// the same MASK token so client and server produce identical output. The server
/// re-runs the same rules on ingest as defence in depth; keeping the two in sync
/// is what makes that check meaningful rather than noisy.
public interface ISecretMasker
{
    /// Masked copy of `text`. Null/empty passes through unchanged.
    string? Mask(string? text);

    /// Masked copy plus the number of distinct replacements made. The count is
    /// what the server reports back as `masked` in a commit response.
    MaskOutcome MaskWithCount(string? text);

    /// Masks a JSON value: structural masking of credential-named members first,
    /// then text masking of every remaining string leaf.
    JsonNode? MaskJson(JsonNode? node);

    (JsonNode? Node, int Hits) MaskJsonWithCount(JsonNode? node);
}

public readonly record struct MaskOutcome(string? Text, int Hits)
{
    public bool Changed => Hits > 0;
}

public sealed class SecretMasker : ISecretMasker
{
    public const string Mask4 = "****";

    /// Ported verbatim from masking.ts. Order matters only in that the more
    /// specific prefixes come first; every pattern replaces its whole match.
    private static readonly Regex[] BuiltIn =
    {
        // API keys with known prefixes
        new(@"\b(sk-ant-api\d{2}-[\w-]{20,})\b", Opts),
        new(@"\b(sk-ant-[\w-]{20,})\b", Opts),
        new(@"\b(sk-proj-[\w-]{20,})\b", Opts),
        new(@"\b(sk-[\w-]{32,})\b", Opts),
        new(@"\b(xai-[\w-]{20,})\b", Opts),
        new(@"\b(gsk_[\w]{20,})\b", Opts),
        new(@"\b(ghp_[\w]{36,})\b", Opts),
        new(@"\b(github_pat_[\w]{20,})\b", Opts),
        new(@"\b(glpat-[\w-]{20,})\b", Opts),
        new(@"\b(pypi-[\w]{20,})\b", Opts),

        // Runner tokens (agentic runner registration) and brain upload tokens
        new(@"\b(rnt_[\w]{20,})\b", Opts),
        new(@"\b(bkt_[\w]{20,})\b", Opts),

        // Generic secret-looking tokens
        new(@"\b(Bearer\s+[\w.-]{20,})", OptsIgnoreCase),
        new(@"\b(token[=:]\s*[\w.-]{20,})", OptsIgnoreCase),

        // Environment variable assignments with secret-sounding names
        new(@"\b(ANTHROPIC_API_KEY=\S+)", Opts),
        new(@"\b(OPENAI_API_KEY=\S+)", Opts),
        new(@"\b(GOOGLE_API_KEY=\S+)", Opts),
        new(@"\b(AWS_SECRET_ACCESS_KEY=\S+)", Opts),
        new(@"\b(AWS_SESSION_TOKEN=\S+)", Opts),
        new(@"\b(GITHUB_TOKEN=\S+)", Opts),
        new(@"\b(GITLAB_TOKEN=\S+)", Opts),
        new(@"\b(DATABASE_URL=\S+)", Opts),
        new(@"\b(REDIS_URL=\S+)", Opts),
        new(@"\b(SECRET_KEY=\S+)", Opts),
        new(@"\b(PRIVATE_KEY=\S+)", Opts),
        new(@"\b(API_KEY=\S+)", Opts),
        new(@"\b(API_SECRET=\S+)", Opts),
        new(@"\b(ACCESS_TOKEN=\S+)", Opts),
        new(@"\b(REFRESH_TOKEN=\S+)", Opts),
        new(@"\b(NPM_TOKEN=\S+)", Opts),
        new(@"\b(SLACK_TOKEN=\S+)", Opts),
        new(@"\b(SLACK_WEBHOOK=\S+)", Opts),
        new(@"\b(DISCORD_TOKEN=\S+)", Opts),
        new(@"\b(SENDGRID_API_KEY=\S+)", Opts),
        new(@"\b(STRIPE_SECRET_KEY=\S+)", Opts),
        new(@"\b(TWILIO_AUTH_TOKEN=\S+)", Opts),

        // PEM blocks — the body is what matters, and it survives every other rule.
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Opts),
    };

    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions OptsIgnoreCase = Opts | RegexOptions.IgnoreCase;

    /// High-entropy values only get masked in a credential-ish context. An
    /// unconditional entropy rule eats git SHAs, base64 images and minified JS,
    /// which would gut the usefulness of `full` exports for no security gain.
    private static readonly Regex EntropyContext = new(
        @"(?<key>[A-Za-z0-9_.\-]*(?:secret|token|passwd|password|pwd|credential|apikey|api_key|access_key|auth)[A-Za-z0-9_.\-]*)"
        + @"\s*[=:]\s*""?(?<val>[A-Za-z0-9+/=_\-\.]{32,})""?",
        OptsIgnoreCase);

    /// Member names whose value is masked wholesale regardless of its shape.
    /// Matched against the JSON key, not its value.
    private static readonly Regex SensitiveKey = new(
        @"(^|[_\-\.])(secret|secrets|token|tokens|password|passwd|pwd|credential|credentials|apikey|api_key|access_key|private_key|authorization|auth_token|session_token|client_secret)([_\-\.]|$)",
        OptsIgnoreCase);

    private const double EntropyThreshold = 3.5;

    private readonly Regex[] _custom;

    /// <param name="customPatternsFile">
    /// Optional `.pks/brain/mask-patterns.txt` — one regex per line, `#` comments,
    /// blank lines ignored. This is where a customer's own key format goes.
    /// </param>
    /// <param name="literalPatterns">
    /// Literal strings to mask (not regexes). Mirrors the web module's
    /// MASK_PATTERNS env var, which escapes its entries.
    /// </param>
    public SecretMasker(string? customPatternsFile = null, IEnumerable<string>? literalPatterns = null)
    {
        var custom = new List<Regex>();

        if (customPatternsFile is { Length: > 0 } && File.Exists(customPatternsFile))
        {
            foreach (var raw in File.ReadLines(customPatternsFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                try
                {
                    custom.Add(new Regex(line, Opts));
                }
                catch (ArgumentException)
                {
                    // A malformed user pattern must not take the export down. It
                    // is skipped; `brain export` surfaces the count separately.
                }
            }
        }

        foreach (var literal in literalPatterns ?? Array.Empty<string>())
        {
            var value = literal.Trim();
            if (value.Length == 0) continue;
            custom.Add(new Regex(Regex.Escape(value), Opts));
        }

        _custom = custom.ToArray();
    }

    /// Builds a masker for a project, honoring `.pks/brain/mask-patterns.txt` and
    /// the PKS_BRAIN_MASK_PATTERNS env var (comma-separated literals).
    public static SecretMasker ForProject(string? projectRoot)
    {
        var file = projectRoot is { Length: > 0 }
            ? System.IO.Path.Combine(projectRoot, ".pks", "brain", "mask-patterns.txt")
            : null;
        var env = Environment.GetEnvironmentVariable("PKS_BRAIN_MASK_PATTERNS");
        var literals = env?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new SecretMasker(file, literals);
    }

    public string? Mask(string? text) => MaskWithCount(text).Text;

    public MaskOutcome MaskWithCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new MaskOutcome(text, 0);

        var hits = 0;
        var result = text;

        foreach (var pattern in BuiltIn)
        {
            result = pattern.Replace(result, m => { hits++; return Mask4; });
        }

        foreach (var pattern in _custom)
        {
            result = pattern.Replace(result, m => { hits++; return Mask4; });
        }

        result = EntropyContext.Replace(result, m =>
        {
            var value = m.Groups["val"].Value;
            if (ShannonEntropy(value) < EntropyThreshold) return m.Value;
            hits++;

            return $"{m.Groups["key"].Value}={Mask4}";
        });

        return new MaskOutcome(result, hits);
    }

    public JsonNode? MaskJson(JsonNode? node) => MaskJsonWithCount(node).Node;

    /// Structural masking first (a credential-named member is masked whatever its
    /// value looks like), then text masking of every remaining string leaf.
    public (JsonNode? Node, int Hits) MaskJsonWithCount(JsonNode? node)
    {
        var hits = 0;
        var masked = MaskNode(node, sensitiveParent: false, ref hits);

        return (masked, hits);
    }

    private JsonNode? MaskNode(JsonNode? node, bool sensitiveParent, ref int hits)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var sensitive = SensitiveKey.IsMatch(key);
                    result[key] = MaskNode(value?.DeepClone(), sensitive, ref hits);
                }

                return result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    result.Add(MaskNode(item?.DeepClone(), sensitiveParent, ref hits));
                }

                return result;
            }

            case JsonValue value:
            {
                if (value.TryGetValue<string>(out var s))
                {
                    // Wholesale for credential-named members: a short or oddly
                    // shaped value under `password` is still a password.
                    if (sensitiveParent && s.Length > 0)
                    {
                        hits++;

                        return JsonValue.Create(Mask4);
                    }

                    var outcome = MaskWithCount(s);
                    hits += outcome.Hits;

                    return JsonValue.Create(outcome.Text);
                }

                return value.DeepClone();
            }

            default:
                return node.DeepClone();
        }
    }

    /// Shannon entropy in bits per character.
    internal static double ShannonEntropy(string value)
    {
        if (value.Length == 0) return 0;

        Span<int> counts = stackalloc int[128];
        var wide = 0;
        foreach (var c in value)
        {
            if (c < 128) counts[c]++;
            else wide++;
        }

        double entropy = 0;
        double length = value.Length;
        foreach (var count in counts)
        {
            if (count == 0) continue;
            var p = count / length;
            entropy -= p * Math.Log2(p);
        }
        if (wide > 0)
        {
            var p = wide / length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}

/// Convenience for callers that mask many fields of one event.
public static class SecretMaskerExtensions
{
    /// Masks and returns (masked text, length, sha256 of the MASKED text).
    /// Hashing after masking is normative — see docs/specs/asf/02-levels.md.
    public static (string? Text, int? Len, string? Hash) MaskAndHash(this ISecretMasker masker, string? text)
    {
        if (text is null) return (null, null, null);
        var masked = masker.Mask(text) ?? "";

        return (masked, masked.Length, CanonicalJson.Sha256HexOfString(masked));
    }

    /// Masks a JSON payload and returns it with its canonical hash and byte size.
    public static (JsonNode? Args, string? Hash, long? Bytes) MaskAndHashJson(this ISecretMasker masker, JsonNode? node)
    {
        if (node is null) return (null, null, null);
        var masked = masker.MaskJson(node);
        var bytes = CanonicalJson.SerializeToUtf8(masked);

        return (masked, CanonicalJson.Hex(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.LongLength);
    }

    public static string Sha256(this string value) => CanonicalJson.Sha256HexOfString(value);
}
