using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Security;

/// <summary>
/// A credential that a command can hold, compare and pass on, but cannot turn into a string.
///
/// The encrypted store closed the <c>cat ~/.pks-cli/settings.json</c> hole, but not the one next to it:
/// every auth service hands commands a DTO — <c>FoundryStoredCredentials</c>, <c>GitHubStoredToken</c>,
/// <c>ScalewayStoredCredentials</c> — with the refresh token or API key sitting on it as a plain
/// <c>string</c>. Nothing stopped a command from interpolating that into a log line, a status table or
/// an error message, and the gate test could not see it, because the gate only knows the names
/// <c>ISecretResolver</c> and <c>Reveal</c>.
///
/// This type removes the accident. There is deliberately **no implicit conversion to string** and no
/// <c>ToString()</c> that returns the content, so `$"token: {creds.RefreshToken}"`,
/// `string.IsNullOrEmpty(creds.ApiKey)` and `JsonSerializer.Serialize(new { creds.RefreshToken })` all
/// stop compiling rather than silently masking at runtime — a broken build is a much better failure
/// than a payload that quietly ships `***` to a remote host. Presence checks use
/// <see cref="HasValue"/>, display uses <see cref="ToString"/>, and the one method that yields
/// plaintext is <c>Reveal</c>, which the gate test forbids under <c>src/Commands/</c> and the MCP layer.
///
/// What this is not: protection against code that is *trying* to leak. Inside a single assembly a
/// determined caller can always route a value somewhere readable. The threat being engineered against
/// is the careless path — the agent that prints a whole DTO, the status command that shows a token
/// instead of its presence, the exception that carries a credential into a transcript.
/// </summary>
[JsonConverter(typeof(SecretValueJsonConverter))]
public readonly struct SecretValue : IEquatable<SecretValue>
{
    /// <summary>
    /// What a <see cref="SecretValue"/> serializes to outside <see cref="SecretJson.Persistence"/>,
    /// and the one string that never round-trips back into a credential — see the converter.
    /// </summary>
    internal const string Mask = "***";

    private readonly string? _value;

    private SecretValue(string? value) => _value = string.IsNullOrEmpty(value) ? null : value;

    /// <summary>No credential. Also what <c>default(SecretValue)</c> is, so an unset field is empty
    /// rather than a null-reference waiting to happen.</summary>
    public static SecretValue None => default;

    /// <summary>Wraps a plaintext credential. Null and empty both collapse to <see cref="None"/>, so
    /// "present but empty" cannot exist and every presence check has one answer.</summary>
    public static SecretValue From(string? value) => new(value);

    /// <summary>Whether a credential is present. The replacement for
    /// <c>!string.IsNullOrEmpty(creds.RefreshToken)</c>.</summary>
    public bool HasValue => _value is not null;

    /// <summary>Length in characters, 0 when absent. Safe to show: it tells an operator that a token
    /// looks truncated without telling anyone what it is.</summary>
    public int Length => _value?.Length ?? 0;

    /// <summary>
    /// The plaintext. Off-limits under <c>src/Commands/</c> and <c>src/Infrastructure/Services/MCP/</c>
    /// — the gate test fails the build if either layer names it. Everywhere else this is the normal way
    /// to use a credential for the thing it is for: signing a request, authenticating a proxy, handing
    /// a child process its environment.
    /// </summary>
    public string? Reveal() => _value;

    /// <summary>Masked. Deliberately not the content, so interpolating a credential into a log line,
    /// a table or an exception message prints nothing useful.</summary>
    public override string ToString() => _value is null ? "(none)" : Mask;

    public bool Equals(SecretValue other) => string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SecretValue other && Equals(other);

    /// <summary>Hashes the mask state, not the credential — a hash of the plaintext in a dump or a
    /// dictionary listing would be an offline oracle against a guessable token.</summary>
    public override int GetHashCode() => _value is null ? 0 : 1;

    public static bool operator ==(SecretValue left, SecretValue right) => left.Equals(right);

    public static bool operator !=(SecretValue left, SecretValue right) => !left.Equals(right);
}

/// <summary>
/// Masks on the way out, reads real content on the way in.
///
/// The asymmetry is the point. Writing is the exfiltration direction — a DTO serialized into a log, a
/// support bundle, an ssh payload or an MCP tool result is exactly how a credential escapes — so the
/// default is a mask, and only <see cref="SecretJson.Persistence"/> writes the real thing. Reading is
/// not a leak: the JSON is already either the encrypted store or a token endpoint's response, so
/// deserializing normally keeps every existing call site working instead of throwing at runtime.
///
/// The one thing reading refuses is <see cref="SecretValue.Mask"/> itself. If a masked DTO ever gets
/// persisted by mistake, the credential comes back as <see cref="SecretValue.None"/> and the user is
/// asked to log in again — the same fail-closed choice the migration makes when it finds the legacy
/// <c>***encrypted***</c> sentinel, and for the same reason: a token that is present but wrong is far
/// worse than one that is honestly missing.
/// </summary>
public sealed class SecretValueJsonConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return SecretValue.None;
        var value = reader.GetString();
        return value == SecretValue.Mask ? SecretValue.None : SecretValue.From(value);
    }

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
    {
        if (!value.HasValue) writer.WriteNullValue();
        else writer.WriteStringValue(SecretValue.Mask);
    }
}

/// <summary>The persistence half: writes the real credential. Only reachable through
/// <see cref="SecretJson"/>, so "this serialization keeps the plaintext" is a deliberate, greppable
/// choice at every call site rather than the default.</summary>
public sealed class SecretValuePersistenceConverter : JsonConverter<SecretValue>
{
    public override SecretValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return SecretValue.None;
        var value = reader.GetString();
        return value == SecretValue.Mask ? SecretValue.None : SecretValue.From(value);
    }

    public override void Write(Utf8JsonWriter writer, SecretValue value, JsonSerializerOptions options)
    {
        if (!value.HasValue) writer.WriteNullValue();
        else writer.WriteStringValue(value.Reveal());
    }
}

/// <summary>
/// The only way to serialize a credential-bearing DTO without masking it.
///
/// Auth services persist their stored-credential DTOs into the encrypted store, which means the one
/// place that legitimately needs real content in JSON. Everything else — logging, status output, ssh
/// payloads, MCP results, anything a person or an agent will read — uses ordinary options and gets
/// <c>***</c>. Naming this type at a call site is the declaration that the output is going somewhere
/// encrypted.
/// </summary>
public static class SecretJson
{
    /// <summary>Plain persistence options. Use <see cref="ForPersistence"/> when a service already has
    /// its own options object to preserve.</summary>
    public static JsonSerializerOptions Persistence { get; } = ForPersistence(null);

    /// <summary>Copies <paramref name="basedOn"/> and adds the persistence converter, so a service
    /// keeps its naming policy, indentation and enum handling while credentials survive the round
    /// trip. Options-level converters take precedence over the type's <c>[JsonConverter]</c>
    /// attribute, which is what lets this override the masking default.</summary>
    public static JsonSerializerOptions ForPersistence(JsonSerializerOptions? basedOn)
    {
        var options = basedOn is null ? new JsonSerializerOptions() : new JsonSerializerOptions(basedOn);
        options.Converters.Add(new SecretValuePersistenceConverter());
        return options;
    }
}
