namespace PKS.Infrastructure.Services.Brain.Asf;

/// Projects a full ASF event down to a level. Spec: docs/specs/asf/02-levels.md.
///
/// Two invariants this class exists to guarantee, both asserted by tests:
///
///   1. Redacted fields are OMITTED, never null. A reader written against
///      `prompts` works unmodified against `full`; a chart that counts tool usage
///      needs no level awareness at all.
///   2. Redaction is monotone. Any field present at level L is byte-identical at
///      every level above L. There is no field that exists only lower down, and
///      no field whose value changes with the level.
///
/// The projector never computes anything. Hashes, lengths and byte counts are
/// filled in by the parser at full fidelity and survive every level — computing
/// them here would break invariant 2 the moment a hash was taken over already-
/// redacted input.
public static class AsfLevelProjector
{
    /// Returns a copy of `fullEvent` reduced to `level`. The input must already
    /// be masked and must already carry its Id.
    public static AsfEvent Project(AsfEvent fullEvent, string level)
    {
        if (!AsfLevel.IsValid(level))
            throw new ArgumentException($"Unknown ASF level '{level}'.", nameof(level));

        var e = fullEvent.Clone();
        e.Level = level;

        // Reasoning text is never transported at any level: Claude ships an
        // opaque `signature` and Codex ships `encrypted_content`, both vendor
        // ciphertext of unbounded size and no analytical value. Length and hash
        // are enough to draw "how much did it think".
        if (e.Kind == AsfKind.Thinking) e.Text = null;

        if (level == AsfLevel.Full) return e;

        // ---- prompts and below ------------------------------------------------

        // Tool arguments and outputs go; the call itself stays fully countable
        // (tool, callId, isError, durationMs, and the hashes/sizes).
        e.Args = null;
        e.Output = null;

        // Paths go; pathHash/ext/depth and the line counts stay.
        e.Path = null;
        e.ProjectPath = null;

        // Free-text failure detail goes; errorClass and fatal stay.
        e.Message = null;

        // Plan bodies go; the counts stay.
        e.Items = null;

        // Attachment filenames go; the count stays.
        e.Attachments = null;

        // cwd and branch become hashes. Hashed, not dropped, so "how many
        // projects / branches" still works without naming any of them.
        e.Cwd = null;
        e.GitBranch = null;

        if (level == AsfLevel.Prompts) return e;

        // ---- metrics ----------------------------------------------------------

        // All remaining human-readable content goes. Everything countable —
        // kinds, tools, timings, tokens, cost, model, lengths and hashes —
        // survives, which is why the heartbeat renders identically at this level.
        e.Text = null;

        return e;
    }

    /// True when `stored` should be replaced/enriched by `incoming` — i.e. the
    /// incoming copy carries strictly more. Used by the receiver to make level
    /// upgrades enriching and level downgrades a no-op rather than a deletion.
    public static bool Enriches(string? storedLevel, string? incomingLevel) =>
        AsfLevel.Rank(incomingLevel) > AsfLevel.Rank(storedLevel);

    /// Fields that exist only to survive redaction. A parser that forgets to fill
    /// these leaves the lower levels blind, so the export path asserts them.
    public static IEnumerable<string> MissingDerivedFields(AsfEvent e)
    {
        // A compaction's text IS its summary, so summaryLen/summaryHash are the
        // fields that carry it down to `metrics` (01-event-envelope.md §kinds).
        if (e.Kind == AsfKind.Compaction)
        {
            if (e.Text is { Length: > 0 } && e.SummaryHash is null) yield return nameof(e.SummaryHash);
            if (e.Text is { Length: > 0 } && e.SummaryLen is null) yield return nameof(e.SummaryLen);
        }
        else
        {
            if (e.Text is { Length: > 0 } && e.TextHash is null) yield return nameof(e.TextHash);
            if (e.Text is { Length: > 0 } && e.TextLen is null) yield return nameof(e.TextLen);
        }

        if (e.Args is not null && e.ArgsHash is null) yield return nameof(e.ArgsHash);
        if (e.Output is { Length: > 0 } && e.OutputHash is null) yield return nameof(e.OutputHash);
        if (e.Path is { Length: > 0 } && e.PathHash is null) yield return nameof(e.PathHash);
        if (e.Cwd is { Length: > 0 } && e.CwdHash is null) yield return nameof(e.CwdHash);
        if (e.GitBranch is { Length: > 0 } && e.GitBranchHash is null) yield return nameof(e.GitBranchHash);
        if (e.Items is not null && e.ItemCount is null) yield return nameof(e.ItemCount);
        if (e.Attachments is not null && e.AttachmentCount is null) yield return nameof(e.AttachmentCount);
    }
}
