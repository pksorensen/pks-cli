using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Exec;

/// <summary>
/// What a tool says it needs before it can run.
///
/// One shape, two ways in. A command-line tool prints this on stdout when it is invoked with
/// <c>PKS_DISCOVERY=1</c> (`pks exec`, FT-010); an Aspire AppHost writes it to a file from its
/// <c>pks-declare</c> pipeline step (`pks aspire run`). The difference is only the channel — what
/// arrives is the same document, so the resolver behind both is the same code.
///
/// Nothing in here is a credential. The manifest names the *kind* of thing that would satisfy each
/// binding, in the placeholder vocabulary below, and pks fills it in on its own side. A tool that ships
/// a manifest ships no token, which is the property the whole protocol exists for.
/// </summary>
public sealed class PksManifest
{
    public string ManifestVersion { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PksCapabilityManifest> Capabilities { get; set; } = new();

    /// <summary>
    /// Every parameter the composition declares, including the ones no capability binds. Only the
    /// Aspire side fills this in, and it is reporting rather than instruction: pks shows what it cannot
    /// supply so a run that is about to stop and ask says so up front.
    /// </summary>
    public List<PksParameterManifest> Parameters { get; set; } = new();

    /// <summary>The one version this pks speaks.</summary>
    public const string SupportedVersion = "v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Parses a manifest out of text that may have other things in it.
    ///
    /// Scanning from the first <c>{</c> is what tolerates a tool that logs a line before its manifest.
    /// It does not tolerate a line *after* it — everything from the brace on is handed to the JSON
    /// reader — which is why the Aspire side writes to a file instead of competing with MSBuild output.
    /// </summary>
    public static PksManifest Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("no manifest was produced");
        }

        var start = text.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException(
                "no JSON in output: " + (text.Length <= 200 ? text : text[..200] + "…"));
        }

        var manifest = JsonSerializer.Deserialize<PksManifest>(text[start..], JsonOptions)
            ?? throw new InvalidOperationException("manifest decoded to null");

        if (manifest.ManifestVersion != SupportedVersion)
        {
            throw new InvalidOperationException(
                $"unsupported manifestVersion={manifest.ManifestVersion} (this pks-cli speaks {SupportedVersion})");
        }

        return manifest;
    }
}

public sealed class PksCapabilityManifest
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public List<PksProviderManifest> Providers { get; set; } = new();
}

public sealed class PksProviderManifest
{
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PksModelManifest> Models { get; set; } = new();

    /// <summary>Environment variable name to placeholder. The placeholder vocabulary is
    /// <c>{endpoint}</c>, <c>{apikey}</c>, <c>{model:role}</c>, <c>{imds:endpoint}</c>,
    /// <c>{imds:header}</c>; anything else passes through as a literal.</summary>
    public Dictionary<string, string> Env { get; set; } = new();
}

public sealed class PksModelManifest
{
    public string Role { get; set; } = "";
    public string Description { get; set; } = "";
    public string Hint { get; set; } = "";
}

public sealed class PksParameterManifest
{
    public string Name { get; set; } = "";
    public string ConfigurationKey { get; set; } = "";
    public bool Secret { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Whether a capability binds this parameter — that is, whether pks can fill it.</summary>
    public bool Bound { get; set; }

    /// <summary>Whether it already has an answer from the environment, a user secret or a default.</summary>
    public bool Supplied { get; set; }
}
