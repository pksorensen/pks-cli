using System.Text.Json;

namespace PKS.Infrastructure.Services.Discovery;

public sealed class AgenticPlatformDiscovery(HttpClient http) : IAgenticPlatformDiscovery
{
    public const string WellKnownPath = "/.well-known/agentic-platform";

    /// Our key in the index's `authorization.clients` map. A host that wants to
    /// admit some other client names it under its own key; this is the one we
    /// are allowed to answer to.
    public const string ClientKey = "pks-cli";

    public async Task<BrainEndpoint> BrainAsync(string server, CancellationToken ct = default)
    {
        var origin = NormalizeOrigin(server);
        var fallback = new BrainEndpoint(
            origin,
            $"{origin}{BrainEndpoint.ConventionalPath}",
            $"{origin}{BrainEndpoint.ConventionalPath}/capabilities",
            $"{origin}/.well-known/oauth-protected-resource{BrainEndpoint.ConventionalPath}",
            PlatformName: null,
            ClientId: null,
            Discovered: false);

        var doc = await GetJsonAsync($"{origin}{WellKnownPath}", ct);
        if (doc is null) return fallback;

        try
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("services", out var services) ||
                !services.TryGetProperty(BrainEndpoint.BrainRel, out var brain))
                return fallback;

            var href = Str(brain, "href");
            if (href is null) return fallback;

            // Same-origin or nothing. The index is unauthenticated and lives on
            // the host the user named; letting it point somewhere else would
            // turn one poisoned document into "upload your transcripts here
            // instead", which is the whole threat model of discovery.
            if (!SameOrigin(origin, href)) return fallback;

            var caps = Str(brain, "capabilities") is { } c && SameOrigin(origin, c) ? c : $"{href.TrimEnd('/')}/capabilities";
            var prm = Str(brain, "authorization") is { } a && SameOrigin(origin, a)
                ? a
                : $"{origin}/.well-known/oauth-protected-resource{new Uri(href).AbsolutePath.TrimEnd('/')}";

            var platform = root.TryGetProperty("platform", out var p) ? Str(p, "name") : null;

            // The client_id this host hosts for us. Same-origin for the same
            // reason as every other URL here, plus one of its own: a CIMD
            // document only validates when its client_id equals the URL it was
            // fetched from, so an off-origin value could not work even if the
            // index were honest.
            var clientId = root.TryGetProperty("authorization", out var authz) &&
                           authz.TryGetProperty("clients", out var clients)
                ? Str(clients, ClientKey) is { } cid && SameOrigin(origin, cid) ? cid : null
                : null;

            return new BrainEndpoint(origin, href.TrimEnd('/'), caps, prm, platform, clientId, Discovered: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or UriFormatException)
        {
            return fallback;
        }
        finally
        {
            doc.Dispose();
        }
    }

    public async Task<ReceiverCapabilities> CapabilitiesAsync(BrainEndpoint endpoint, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync(endpoint.CapabilitiesUrl, ct);
        if (doc is null) return ReceiverCapabilities.Conservative;

        try
        {
            var r = doc.RootElement;
            var d = ReceiverCapabilities.Conservative;

            return new ReceiverCapabilities(
                SpecVersion: Int(r, "specVersion") ?? d.SpecVersion,
                MaxChunkBytes: Long(r, "maxChunkBytes") ?? d.MaxChunkBytes,
                MaxBlobBytes: Long(r, "maxBlobBytes") ?? d.MaxBlobBytes,
                MaxManifestChunks: Int(r, "maxManifestChunks") ?? d.MaxManifestChunks,
                MaxManifestBlobs: Int(r, "maxManifestBlobs") ?? d.MaxManifestBlobs,
                MaxSyncsPerHour: Int(r, "maxSyncsPerHour") ?? d.MaxSyncsPerHour,
                LevelsAccepted: StrArray(r, "levelsAccepted") ?? d.LevelsAccepted,
                BlobsAccepted: Bool(r, "blobsAccepted") ?? d.BlobsAccepted,
                Implementation: Str(r, "implementation"),
                QuotaBytes: Long(r, "quotaBytes"),
                QuotaUsedBytes: Long(r, "quotaUsedBytes"),
                Discovered: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ReceiverCapabilities.Conservative;
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// `agentics.dk` → `https://agentics.dk`; anything with a scheme is kept,
    /// including `http://localhost:3000`, because that is how the receiver gets
    /// developed in the first place.
    public static string NormalizeOrigin(string server)
    {
        var s = server.Trim().TrimEnd('/');
        if (!s.Contains("://")) s = $"https://{s}";
        var uri = new Uri(s);

        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static bool SameOrigin(string origin, string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && string.Equals($"{u.Scheme}://{u.Authority}", origin, StringComparison.OrdinalIgnoreCase);

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static IReadOnlyList<string>? StrArray(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) list.Add(s);

        return list.Count > 0 ? list : null;
    }
}
