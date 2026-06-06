using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>pks-side launch defaults for <c>pks codex</c>, persisted to <c>~/.pks-cli/codex.json</c>.</summary>
public sealed class CodexLaunchConfig
{
    public string Deployment { get; set; } = "gpt-5-codex";
    public int Port { get; set; } = 8788;
    public string ReasoningEffort { get; set; } = "medium";
}

/// <summary>
/// Reads/writes the Codex CLI config. We never parse the user's TOML: the Foundry provider lives in
/// an idempotent, clearly-delimited managed block in <c>~/.codex/config.toml</c> so we can re-write
/// only our block and leave everything else (including the user's default <c>model_provider</c> and
/// their ChatGPT login) untouched. pks-side defaults live in JSON under <c>~/.pks-cli/</c>.
/// </summary>
public static class CodexCliConfig
{
    public const string BeginMarker = "# >>> pks-codex (managed) — edit via `pks codex init`";
    public const string EndMarker = "# <<< pks-codex";

    public const string ProviderName = "pks-foundry";

    /// <summary>Inserts or replaces the managed block in an existing config.toml, preserving all other content.</summary>
    public static string UpsertManagedBlock(string? existing, string blockBody)
    {
        var managed = BeginMarker + "\n" + blockBody.Trim() + "\n" + EndMarker;

        if (string.IsNullOrWhiteSpace(existing))
            return managed + "\n";

        var beginIdx = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIdx >= 0)
        {
            var endIdx = existing.IndexOf(EndMarker, beginIdx, StringComparison.Ordinal);
            if (endIdx >= 0)
            {
                endIdx += EndMarker.Length;
                var before = existing[..beginIdx].TrimEnd();
                var after = existing[endIdx..].TrimStart('\r', '\n');
                var sb = new StringBuilder();
                if (before.Length > 0) sb.Append(before).Append("\n\n");
                sb.Append(managed).Append('\n');
                if (after.Length > 0) sb.Append('\n').Append(after);
                return sb.ToString();
            }
        }

        // No existing managed block — append after the user's content.
        return existing.TrimEnd() + "\n\n" + managed + "\n";
    }

    /// <summary>The TOML body for the Foundry passthrough provider (loopback; auth is injected by pks).</summary>
    public static string BuildProviderBlock(int port) =>
$@"# Native Codex provider routed through the pks loopback passthrough, which injects fresh
# Azure AI Foundry auth on every request. Select it per-run via `pks codex` (do not flip your
# global default here). Requires the passthrough to be running, e.g. `pks codex`.
[model_providers.{ProviderName}]
name = ""PKS Foundry (Codex)""
base_url = ""http://127.0.0.1:{port}/openai/v1""
env_key = ""PKS_CODEX_TOKEN""
wire_api = ""responses""";

    public static string ConfigTomlPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    /// <summary>True if the managed Foundry provider block is present and targets <paramref name="port"/>.</summary>
    public static bool HasManagedBlockForPort(string? existing, int port)
    {
        if (string.IsNullOrEmpty(existing)) return false;
        var beginIdx = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (beginIdx < 0) return false;
        var endIdx = existing.IndexOf(EndMarker, beginIdx, StringComparison.Ordinal);
        if (endIdx < 0) return false;
        return existing[beginIdx..endIdx].Contains($"127.0.0.1:{port}/openai/v1", StringComparison.Ordinal);
    }

    /// <summary>Writes the managed Foundry provider block into <c>~/.codex/config.toml</c>, creating the file if needed.</summary>
    public static void WriteProviderBlock(int port)
    {
        var path = ConfigTomlPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        File.WriteAllText(path, UpsertManagedBlock(existing, BuildProviderBlock(port)));
    }

    // ---- pks-side launch defaults (~/.pks-cli/codex.json) ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static CodexLaunchConfig LoadLaunchConfig()
    {
        var path = SecurityFiles.PathFor("codex.json");
        if (!File.Exists(path)) return new CodexLaunchConfig();
        try
        {
            return JsonSerializer.Deserialize<CodexLaunchConfig>(File.ReadAllText(path)) ?? new CodexLaunchConfig();
        }
        catch
        {
            return new CodexLaunchConfig();
        }
    }

    public static void SaveLaunchConfig(CodexLaunchConfig cfg)
    {
        var path = SecurityFiles.PathFor("codex.json");
        SecurityFiles.EnsureDirectory(path);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts));
        SecurityFiles.Restrict(path);
    }

    /// <summary>Heuristic: does this deployment name look like a Codex/GPT model worth defaulting to?</summary>
    public static bool LooksLikeCodex(string? model) =>
        !string.IsNullOrEmpty(model) &&
        (model.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("codex", StringComparison.OrdinalIgnoreCase));
}
