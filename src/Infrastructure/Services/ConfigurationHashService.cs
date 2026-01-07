using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for computing configuration hashes to detect devcontainer configuration changes
/// </summary>
public class ConfigurationHashService : IConfigurationHashService
{
    private readonly ILogger<ConfigurationHashService> _logger;

    public ConfigurationHashService(ILogger<ConfigurationHashService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<string> ComputeConfigurationHashAsync(string projectPath, string devcontainerPath)
    {
        var result = await ComputeConfigurationHashWithDetailsAsync(projectPath, devcontainerPath);
        return result.Hash;
    }

    /// <inheritdoc/>
    public async Task<ConfigurationHashResult> ComputeConfigurationHashWithDetailsAsync(string projectPath, string devcontainerPath)
    {
        _logger.LogDebug("Computing configuration hash for: {ProjectPath}", projectPath);

        var result = new ConfigurationHashResult
        {
            Timestamp = DateTime.UtcNow,
            Version = 1
        };

        var hashInputs = new List<string>();

        try
        {
            // 1. Read and normalize devcontainer.json (required)
            var devcontainerJsonPath = Path.Combine(devcontainerPath, "devcontainer.json");
            if (!File.Exists(devcontainerJsonPath))
            {
                throw new FileNotFoundException($"devcontainer.json not found at: {devcontainerJsonPath}");
            }

            var devcontainerJson = await File.ReadAllTextAsync(devcontainerJsonPath);
            var normalizedJson = NormalizeJson(devcontainerJson);

            var fileHash = ComputeStringHash(normalizedJson);
            result.FileHashes["devcontainer.json"] = fileHash;
            result.IncludedFiles.Add("devcontainer.json");
            hashInputs.Add($"devcontainer.json:{normalizedJson}");

            _logger.LogDebug("Included devcontainer.json (hash: {Hash})", fileHash.Substring(0, 8));

            // 2. Parse devcontainer.json to find referenced files
            DevcontainerConfiguration? config = null;
            try
            {
                config = JsonSerializer.Deserialize<DevcontainerConfiguration>(normalizedJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse devcontainer.json, will only hash the raw content");
            }

            // 3. Check for Dockerfile reference
            if (config?.Build?.Dockerfile != null)
            {
                var dockerfilePath = Path.Combine(devcontainerPath, config.Build.Dockerfile);
                if (File.Exists(dockerfilePath))
                {
                    var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath);
                    var dockerfileHash = ComputeStringHash(dockerfileContent);

                    result.FileHashes[config.Build.Dockerfile] = dockerfileHash;
                    result.IncludedFiles.Add(config.Build.Dockerfile);
                    hashInputs.Add($"dockerfile:{dockerfileContent}");

                    _logger.LogDebug("Included Dockerfile: {Path} (hash: {Hash})",
                        config.Build.Dockerfile, dockerfileHash.Substring(0, 8));
                }
                else
                {
                    _logger.LogWarning("Dockerfile referenced but not found: {Path}", dockerfilePath);
                }
            }

            // 4. Check for docker-compose reference
            if (config?.DockerComposeFile != null)
            {
                var composePath = Path.Combine(devcontainerPath, config.DockerComposeFile);
                if (File.Exists(composePath))
                {
                    var composeContent = await File.ReadAllTextAsync(composePath);
                    var composeHash = ComputeStringHash(composeContent);

                    result.FileHashes[config.DockerComposeFile] = composeHash;
                    result.IncludedFiles.Add(config.DockerComposeFile);
                    hashInputs.Add($"compose:{composeContent}");

                    _logger.LogDebug("Included docker-compose: {Path} (hash: {Hash})",
                        config.DockerComposeFile, composeHash.Substring(0, 8));
                }
                else
                {
                    _logger.LogWarning("docker-compose file referenced but not found: {Path}", composePath);
                }
            }

            // 5. Include feature references (names + versions)
            if (config?.Features != null && config.Features.Count > 0)
            {
                var sortedFeatures = config.Features.OrderBy(f => f.Key).ToList();
                foreach (var feature in sortedFeatures)
                {
                    var featureString = JsonSerializer.Serialize(feature);
                    hashInputs.Add($"feature:{feature.Key}:{featureString}");
                }

                _logger.LogDebug("Included {Count} features in hash", config.Features.Count);
            }

            // 6. Compute final hash
            var combined = string.Join("\n", hashInputs);
            result.Hash = ComputeStringHash(combined);

            _logger.LogInformation("Configuration hash computed: {Hash} (from {FileCount} files)",
                result.Hash.Substring(0, 16) + "...", result.IncludedFiles.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute configuration hash");
            throw;
        }
    }

    /// <inheritdoc/>
    public string NormalizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        // First, try to parse directly without comment removal
        // This handles standard JSON and JSONC with escaped newlines correctly
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = false // Minified output
            });

            WriteElementWithSortedKeys(writer, document.RootElement);
            writer.Flush();

            var normalized = Encoding.UTF8.GetString(stream.ToArray());
            return normalized;
        }
        catch (JsonException)
        {
            // If direct parsing fails, try removing comments and trailing commas
            _logger.LogDebug("Direct JSON parsing failed, attempting comment removal");
        }

        try
        {
            // Remove trailing commas (common in JSONC)
            var cleanedJson = Regex.Replace(json, @",(\s*[}\]])", "$1");

            // Try removing single-line comments (// ...) carefully
            // Only match at start of line or after whitespace to avoid breaking strings
            cleanedJson = Regex.Replace(cleanedJson, @"^\s*//.*$", "", RegexOptions.Multiline);

            // Remove multi-line comments (/* ... */)
            cleanedJson = Regex.Replace(cleanedJson, @"/\*.*?\*/", "", RegexOptions.Singleline);

            // Try parsing again
            using var document = JsonDocument.Parse(cleanedJson);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = false
            });

            WriteElementWithSortedKeys(writer, document.RootElement);
            writer.Flush();

            var normalized = Encoding.UTF8.GetString(stream.ToArray());
            return normalized;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to normalize JSON after comment removal, using raw content for hashing");
            // Last resort: use raw content (will still produce consistent hash for same content)
            return json;
        }
    }

    /// <summary>
    /// Writes JSON element with sorted object keys for consistent hashing
    /// </summary>
    private void WriteElementWithSortedKeys(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Sort properties by name for consistent ordering
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElementWithSortedKeys(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithSortedKeys(writer, item);
                }
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt32(out var intValue))
                {
                    writer.WriteNumberValue(intValue);
                }
                else if (element.TryGetInt64(out var longValue))
                {
                    writer.WriteNumberValue(longValue);
                }
                else
                {
                    writer.WriteNumberValue(element.GetDouble());
                }
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Removes comments from JSON without parsing (fallback for invalid JSON)
    /// </summary>
    private string RemoveCommentsOnly(string json)
    {
        // Remove single-line comments
        json = Regex.Replace(json, @"//.*$", "", RegexOptions.Multiline);

        // Remove multi-line comments
        json = Regex.Replace(json, @"/\*.*?\*/", "", RegexOptions.Singleline);

        return json.Trim();
    }

    /// <summary>
    /// Computes SHA256 hash of a string
    /// </summary>
    private string ComputeStringHash(string input)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <inheritdoc/>
    public async Task<ConfigurationChangeResult> CheckConfigurationChangedAsync(
        string projectPath,
        string devcontainerPath,
        string storedHash)
    {
        _logger.LogDebug("Checking configuration changes against stored hash: {Hash}",
            storedHash.Substring(0, 16) + "...");

        var result = new ConfigurationChangeResult
        {
            StoredHash = storedHash
        };

        try
        {
            // Compute current configuration hash
            var currentHashResult = await ComputeConfigurationHashWithDetailsAsync(projectPath, devcontainerPath);
            result.CurrentHash = currentHashResult.Hash;
            result.HashDetails = currentHashResult;

            // Compare hashes
            if (result.CurrentHash == storedHash)
            {
                result.Changed = false;
                result.Reason = "Configuration unchanged";
                _logger.LogDebug("Configuration unchanged (hash match)");
                return result;
            }

            // Hashes differ - determine what changed
            result.Changed = true;
            result.Reason = "Configuration files modified";

            // Try to determine which files changed by comparing individual file hashes
            // This requires reading the previous hash details, which we don't have stored
            // For now, we just report that something changed
            _logger.LogInformation("Configuration changed: current hash {CurrentHash} != stored hash {StoredHash}",
                result.CurrentHash.Substring(0, 16) + "...",
                storedHash.Substring(0, 16) + "...");

            // List all files that are currently included
            result.ChangedFiles = currentHashResult.IncludedFiles;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check configuration changes");
            result.Changed = true;
            result.Reason = $"Error checking configuration: {ex.Message}";
            return result;
        }
    }
}
