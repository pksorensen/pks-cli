using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Brain.Asf;

/// Builds full-fidelity ASF events for one session: masks content, derives every
/// hash/length/size the lower levels depend on, assigns gap-free `seq` in source
/// order, and stamps the level-independent `id`.
///
/// The three parsers (Claude, Codex, opencode) all go through this so the derived
/// fields cannot drift between sources — a `textHash` must mean the same thing
/// whichever tool produced the turn, or cross-source dedupe and cross-source
/// charts both quietly break.
///
/// `seq` comes from call order, never from wall-clock. Re-parsing the same source
/// must reproduce the same sequence, or every id changes and the whole export
/// re-uploads.
public sealed class AsfEventBuilder
{
    private readonly ISecretMasker _masker;
    private readonly string _src;
    private readonly string _srcVersion;
    private readonly string _session;
    private readonly string _project;
    private int _seq;

    public AsfEventBuilder(
        ISecretMasker masker,
        string src,
        string? srcVersion,
        string nativeSessionId,
        string projectRoot)
    {
        _masker = masker;
        _src = src;
        _srcVersion = srcVersion ?? "unknown";
        _session = AsfEventId.SessionHandle(src, nativeSessionId);
        _project = AsfEventId.ProjectHandle(projectRoot);
    }

    /// The hashed session handle, for cursors and manifests.
    public string SessionHandle => _session;

    public string ProjectHandle => _project;

    /// Number of events built so far — also the next `seq`.
    public int Count => _seq;

    /// Total masker hits across this session. Surfaced by `brain export` so a
    /// user can see that masking is actually doing something.
    public int MaskHits { get; private set; }

    /// Creates an event with the envelope filled in and `seq` advanced. Callers
    /// mutate the returned instance and then pass it to <see cref="Seal"/>.
    public AsfEvent Begin(string kind, DateTimeOffset ts) => new()
    {
        V = 1,
        Seq = _seq++,
        Ts = ts.ToUniversalTime(),
        Src = _src,
        SrcVersion = _srcVersion,
        Session = _session,
        Project = _project,
        Kind = kind,
        Level = AsfLevel.Full,
    };

    /// Masks `text` and fills Text/TextLen/TextHash. Hashing after masking is
    /// normative — otherwise a hash could confirm a guess at an unmasked secret.
    public AsfEvent SetText(AsfEvent e, string? text)
    {
        if (text is null) return e;
        var outcome = _masker.MaskWithCount(text);
        MaskHits += outcome.Hits;
        e.Text = outcome.Text;
        e.TextLen = outcome.Text?.Length ?? 0;
        e.TextHash = CanonicalJson.Sha256HexOfString(outcome.Text ?? "");

        return e;
    }

    /// Same, for the compaction summary fields.
    public AsfEvent SetSummary(AsfEvent e, string? text)
    {
        if (text is null) return e;
        var outcome = _masker.MaskWithCount(text);
        MaskHits += outcome.Hits;
        e.Text = outcome.Text;
        e.SummaryLen = outcome.Text?.Length ?? 0;
        e.SummaryHash = CanonicalJson.Sha256HexOfString(outcome.Text ?? "");

        return e;
    }

    /// Records reasoning without its text. Kept as an explicit method so a parser
    /// cannot accidentally route thinking through SetText.
    public AsfEvent SetThinking(AsfEvent e, string? text)
    {
        var length = text?.Length ?? 0;
        e.TextLen = length;
        e.TextHash = CanonicalJson.Sha256HexOfString(text ?? "");
        e.Text = null;

        return e;
    }

    /// Masks tool arguments and fills Args/ArgsHash/ArgsBytes.
    public AsfEvent SetArgs(AsfEvent e, JsonNode? args)
    {
        if (args is null) return e;
        var (masked, hits) = _masker.MaskJsonWithCount(args);
        MaskHits += hits;
        var bytes = CanonicalJson.SerializeToUtf8(masked);
        e.Args = masked;
        e.ArgsHash = CanonicalJson.Sha256HexOfString(System.Text.Encoding.UTF8.GetString(bytes));
        e.ArgsBytes = bytes.LongLength;

        return e;
    }

    /// Masks tool output and fills Output/OutputHash/OutputBytes. `originalBytes`
    /// is the pre-truncation size when the source only kept a head/tail, so the
    /// size chart stays honest about what actually happened.
    public AsfEvent SetOutput(AsfEvent e, string? output, long? originalBytes = null)
    {
        if (output is null) return e;
        var outcome = _masker.MaskWithCount(output);
        MaskHits += outcome.Hits;
        var text = outcome.Text ?? "";
        e.Output = text;
        e.OutputHash = CanonicalJson.Sha256HexOfString(text);
        e.OutputBytes = originalBytes ?? System.Text.Encoding.UTF8.GetByteCount(text);

        return e;
    }

    /// Fills Path/PathHash/Ext/Depth from a file path.
    public AsfEvent SetPath(AsfEvent e, string? path)
    {
        if (string.IsNullOrEmpty(path)) return e;
        var masked = _masker.Mask(path) ?? path;
        e.Path = masked;
        e.PathHash = AsfEventId.OpaqueHash(masked);
        var ext = System.IO.Path.GetExtension(masked);
        e.Ext = string.IsNullOrEmpty(ext) ? null : ext.TrimStart('.').ToLowerInvariant();
        e.Depth = masked.Count(c => c is '/' or '\\');

        return e;
    }

    /// Fills Cwd/CwdHash and GitBranch/GitBranchHash. Both survive to `metrics`
    /// as hashes, so "how many projects/branches" works without naming them.
    public AsfEvent SetContext(AsfEvent e, string? cwd, string? gitBranch)
    {
        if (cwd is { Length: > 0 })
        {
            e.Cwd = cwd;
            e.CwdHash = AsfEventId.OpaqueHash(cwd);
        }
        if (gitBranch is { Length: > 0 })
        {
            e.GitBranch = gitBranch;
            e.GitBranchHash = AsfEventId.OpaqueHash(gitBranch);
        }

        return e;
    }

    /// Masks plan items and fills Items/ItemCount/DoneCount.
    public AsfEvent SetPlan(AsfEvent e, IEnumerable<string> items, int doneCount)
    {
        var masked = new List<string>();
        foreach (var item in items)
        {
            var outcome = _masker.MaskWithCount(item);
            MaskHits += outcome.Hits;
            masked.Add(outcome.Text ?? "");
        }
        e.Items = masked;
        e.ItemCount = masked.Count;
        e.DoneCount = doneCount;

        return e;
    }

    /// Masks attachment names and fills Attachments/AttachmentCount.
    public AsfEvent SetAttachments(AsfEvent e, IReadOnlyList<string> names)
    {
        if (names.Count == 0) return e;
        e.Attachments = names.Select(n => _masker.Mask(n) ?? "").ToList();
        e.AttachmentCount = names.Count;

        return e;
    }

    /// Masks the failure detail and fills ErrorClass/Message.
    public AsfEvent SetError(AsfEvent e, string? errorClass, string? message, bool fatal = false)
    {
        e.ErrorClass = errorClass;
        if (message is not null)
        {
            var outcome = _masker.MaskWithCount(message);
            MaskHits += outcome.Hits;
            e.Message = outcome.Text;
        }
        e.Fatal = fatal ? true : null;

        return e;
    }

    /// Assigns the level-independent id. Call last, after every field is set —
    /// the id hashes the full event, so a field added afterwards changes identity
    /// and defeats dedupe.
    public AsfEvent Seal(AsfEvent e)
    {
        var missing = AsfLevelProjector.MissingDerivedFields(e).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"ASF event {_src}/{_session}#{e.Seq} ({e.Kind}) is missing derived fields " +
                $"{string.Join(", ", missing)}; the lower levels would be blind. " +
                "Use the AsfEventBuilder setters rather than assigning fields directly.");
        }

        return e.WithId();
    }
}
