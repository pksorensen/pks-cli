namespace PKS.Infrastructure.Services.Discovery;

/// Turns a host the user typed into the endpoints a command needs.
///
/// `pks brain push --server https://brain.example.com` must not require the
/// user to also know that the API lives at `/api/brain/v1`, that its limits are
/// at `/capabilities`, and that its authorization metadata is under
/// `/.well-known/oauth-protected-resource/...`. One fetch of
/// `/.well-known/agentic-platform` answers all three, and every service the
/// platform grows later shows up in the same document without a client release.
///
/// Discovery is strictly an optimisation. A host that serves no index still
/// works: the conventional paths are the fallback, which is what keeps
/// `--server` usable against a receiver that implemented the four upload routes
/// and stopped there.
public interface IAgenticPlatformDiscovery
{
    /// <param name="server">Host or absolute URL as the user typed it.</param>
    Task<BrainEndpoint> BrainAsync(string server, CancellationToken ct = default);

    /// Receiver limits. Never throws — an unreachable or absent document yields
    /// <see cref="ReceiverCapabilities.Conservative"/>, whose numbers are the
    /// smallest thing any receiver is likely to accept.
    Task<ReceiverCapabilities> CapabilitiesAsync(BrainEndpoint endpoint, CancellationToken ct = default);
}

/// <param name="Origin">Scheme + host of the server the user named.</param>
/// <param name="ApiBase">Where the four upload routes live. `/sync` hangs off this.</param>
/// <param name="ClientId">
/// The OAuth client_id this host hosts for pks-cli, if it said. A CIMD document
/// is only valid when its client_id equals the URL it was served from, so the
/// receiver is the only party that can name a working one — assuming
/// agentics.dk's would present a document a self-hosted issuer must reject.
/// Same-origin-checked like every other URL from the index.
/// </param>
/// <param name="Discovered">
/// False when the platform index was absent and the conventional paths were
/// assumed. Worth printing: it is the difference between "this host says it
/// speaks brain" and "we are about to find out".
/// </param>
public sealed record BrainEndpoint(
    string Origin,
    string ApiBase,
    string CapabilitiesUrl,
    string ProtectedResourceMetadataUrl,
    string? PlatformName,
    string? ClientId,
    bool Discovered)
{
    public const string BrainRel = "https://agentics.dk/rel/brain/v1";
    public const string ConventionalPath = "/api/brain/v1";

    /// What a token for this endpoint is bound to (RFC 8707 resource
    /// indicator), and what the client compares a stored credential against.
    public string Resource => ApiBase;
}

public sealed record ReceiverCapabilities(
    int SpecVersion,
    long MaxChunkBytes,
    long MaxBlobBytes,
    int MaxManifestChunks,
    int MaxManifestBlobs,
    int MaxSyncsPerHour,
    IReadOnlyList<string> LevelsAccepted,
    bool BlobsAccepted,
    string? Implementation,
    long? QuotaBytes,
    long? QuotaUsedBytes,
    bool Discovered)
{
    /// The pre-capabilities defaults, kept as the floor rather than deleted:
    /// they are what every pks-cli built before this endpoint existed assumes,
    /// so a receiver that satisfies them satisfies every client in the wild.
    public static readonly ReceiverCapabilities Conservative = new(
        SpecVersion: 1,
        MaxChunkBytes: 32L * 1024 * 1024,
        MaxBlobBytes: 512L * 1024 * 1024,
        MaxManifestChunks: 2000,
        MaxManifestBlobs: 5000,
        MaxSyncsPerHour: 60,
        LevelsAccepted: ["metrics", "prompts", "full"],
        BlobsAccepted: true,
        Implementation: null,
        QuotaBytes: null,
        QuotaUsedBytes: null,
        Discovered: false);

    /// Chunks per sync session: a quarter of the manifest ceiling, so a failed
    /// batch is small and a receiver that halves its limit does not need a
    /// client change. Never below one.
    public int BatchSize(int preferred) => Math.Max(1, Math.Min(preferred, MaxManifestChunks / 4));

    public int BlobBatchSize(int preferred) => Math.Max(1, Math.Min(preferred, MaxManifestBlobs / 4));
}
