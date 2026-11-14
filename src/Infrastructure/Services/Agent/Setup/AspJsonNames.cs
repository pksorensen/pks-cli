using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Setup;

/// <summary>
/// Reads an ASP document whose field names arrived in camelCase instead of the snake_case the
/// protocol specifies.
/// </summary>
/// <remarks>
/// <b>Why a client has to tolerate this.</b> The spec says snake_case, and a platform serving
/// camelCase is not conforming. But an adopter rarely writes the JSON by hand: it comes out of a
/// generated API layer with one global naming policy, and that policy renames every field in the
/// application. Context and Home is the measured case. Its BFF generates the DTOs from an OpenAPI
/// spec and serializes them camelCase, so <c>loopback_redirect_uris</c> reaches the wire as
/// <c>loopbackRedirectUris</c>.
///
/// <para>
/// Being strict here is worse than being lenient, and not by a little. An unmatched field does
/// not fail: it deserializes to null, so the one thing that must come from the platform, the
/// redirect host, silently becomes whatever the client would have guessed. That is the exact
/// failure this protocol exists to prevent, and it would surface as a sign-in rejected by an
/// issuer with no indication that a field had been dropped.
/// </para>
///
/// <para>
/// <b>What is deliberately not renamed.</b> Only keys that look like field names, so no <c>:</c>,
/// <c>/</c>, <c>.</c> or space. Some objects in these documents are keyed by data rather than by
/// field name, and the index is the one that matters: its <c>services</c> keys are rel URIs. A
/// blind rewrite would mangle them. A key whose snake_case form is already present is left alone
/// too, so a conforming document passes through untouched.
/// </para>
/// </remarks>
internal static class AspJsonNames
{
    /// <summary>
    /// Returns <paramref name="json"/> with camelCase field names rewritten to snake_case.
    /// Returns it unchanged when nothing needed rewriting, or when it does not parse.
    /// </summary>
    internal static string Normalize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is null) return json;

        return Walk(root) ? root.ToJsonString() : json;
    }

    private static bool Walk(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                {
                    var changed = false;
                    foreach (var item in array)
                        if (item is not null) changed |= Walk(item);

                    return changed;
                }

            case JsonObject obj:
                {
                    var renames = new List<(string From, string To)>();

                    // The recursion's answer has to be kept. Discarding it made Normalize a no-op for
                    // every document whose root keys happen to need no renaming — which is all of them
                    // except the service document: a profile list's roots are v, source, subject and
                    // profiles. The nested rewrites still happened in the tree, and were then thrown
                    // away with it when this level reported no change. Measured 13/11-2026.
                    var nested = false;

                    foreach (var (key, value) in obj)
                    {
                        if (value is not null) nested |= Walk(value);

                        var snake = ToSnake(key);
                        if (snake is not null && !obj.ContainsKey(snake)) renames.Add((key, snake));
                    }

                    foreach (var (from, to) in renames)
                    {
                        var value = obj[from];
                        obj.Remove(from);

                        // Detaching first: a JsonNode may have only one parent, and re-adding one that
                        // still believes it is owned elsewhere throws rather than moving it.
                        obj[to] = value?.DeepClone();
                    }

                    return nested || renames.Count > 0;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// The snake_case form of a camelCase field name, or null when the key is not one.
    /// </summary>
    private static string? ToSnake(string key)
    {
        if (key.Length == 0) return null;

        var hasUpper = false;
        foreach (var c in key)
        {
            // Not a field name: a URI, a dotted name, a sentence. Left alone.
            if (c is ':' or '/' or '.' or ' ' or '_' or '-') return null;
            if (char.IsUpper(c)) hasUpper = true;
        }

        if (!hasUpper) return null;

        var sb = new System.Text.StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
