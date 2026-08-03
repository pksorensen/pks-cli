using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Canonical JSON, normative form. Spec: docs/specs/asf/03-chunks-and-hashing.md.
///
///   1. Object keys sorted ascending by UTF-16 code unit.
///   2. No whitespace between tokens.
///   3. Minimal escaping — only ", \ and C0 controls. '/' is NOT escaped;
///      non-ASCII is emitted as literal UTF-8, never \uXXXX.
///   4. Numbers in shortest round-trip form.
///   5. Absent members omitted. Explicit null retained.
///   6. Arrays keep source order.
///
/// Two implementations that disagree here produce different event ids for
/// identical events, which would break dedupe across clients — hence the test
/// vectors in the spec, asserted by CanonicalJsonTests.
public static class CanonicalJson
{
    /// Ordinal comparison is UTF-16 code-unit order, which is what the spec says.
    private static readonly StringComparer KeyOrder = StringComparer.Ordinal;

    /// UnsafeRelaxedJsonEscaping is what gives us rules 3: it leaves '/', '+',
    /// '&lt;', '&amp;' and all non-ASCII alone, escaping only what JSON itself requires.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = Encoder,
        WriteIndented = false,
    };

    /// Canonical UTF-8 bytes for any JSON node. Null input yields `null`.
    public static byte[] SerializeToUtf8(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = Encoder,
            Indented = false,
            // Values are already validated JSON nodes; skipping validation keeps
            // the hot path (one call per event) cheap.
            SkipValidation = true,
        }))
        {
            Write(writer, node);
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string Serialize(JsonNode? node) => Encoding.UTF8.GetString(SerializeToUtf8(node));

    /// Canonical bytes for an arbitrary object, via its default STJ shape.
    public static byte[] SerializeToUtf8<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, SerializerOptions);

        return SerializeToUtf8(node);
    }

    public static string Serialize<T>(T value) => Encoding.UTF8.GetString(SerializeToUtf8(value));

    /// sha256 hex of the canonical form.
    public static string Sha256Hex(JsonNode? node) => Hex(SHA256.HashData(SerializeToUtf8(node)));

    public static string Sha256Hex<T>(T value) => Hex(SHA256.HashData(SerializeToUtf8(value)));

    /// sha256 hex of a UTF-8 string. Used for textHash/pathHash/argsHash, which
    /// hash the masked *value* rather than a JSON wrapper around it.
    public static string Sha256HexOfString(string value) =>
        Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();

                return;

            case JsonObject obj:
                writer.WriteStartObject();
                // Materialize before sorting: JsonObject enumeration order is
                // insertion order, and we need deterministic key order instead.
                foreach (var property in obj.OrderBy(p => p.Key, KeyOrder))
                {
                    writer.WritePropertyName(property.Key);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();

                return;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) Write(writer, item);
                writer.WriteEndArray();

                return;

            case JsonValue value:
                WriteValue(writer, value);

                return;

            default:
                throw new NotSupportedException($"Unsupported JsonNode type {node.GetType().Name}.");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        // JsonValue wraps either a JsonElement (parsed input) or a CLR value
        // (constructed input). Both paths must produce identical bytes.
        if (value.TryGetValue<JsonElement>(out var element))
        {
            WriteElement(writer, element);

            return;
        }

        if (value.TryGetValue<string>(out var s)) { writer.WriteStringValue(s); return; }
        if (value.TryGetValue<bool>(out var b)) { writer.WriteBooleanValue(b); return; }
        if (value.TryGetValue<long>(out var l)) { writer.WriteNumberValue(l); return; }
        if (value.TryGetValue<int>(out var i)) { writer.WriteNumberValue(i); return; }
        if (value.TryGetValue<decimal>(out var m)) { writer.WriteNumberValue(m); return; }
        if (value.TryGetValue<double>(out var d)) { writer.WriteNumberValue(d); return; }

        // Fall back to the node's own serialization for anything exotic.
        value.WriteTo(writer, SerializerOptions);
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());

                return;

            case JsonValueKind.Number:
                // GetRawText preserves the shortest round-trip form the parser
                // saw; re-parsing through double would lose precision on large
                // integers and add a ".0" on whole doubles.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);

                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());

                return;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();

                return;

            default:
                // Objects/arrays inside a JsonValue can only appear via odd
                // construction paths; recurse through the node model so key
                // sorting still applies.
                Write(writer, JsonNode.Parse(element.GetRawText()));

                return;
        }
    }
}
