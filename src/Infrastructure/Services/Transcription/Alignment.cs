using System.Text;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// Our own diarisation merge: transfer speaker labels from one engine's output onto
/// another engine's words, by aligning the two word sequences.
///
/// The problem it solves. Azure fast transcription answers *who spoke when* (speaker
/// labels, per-word timings) at roughly 15 % word error. MAI-1.5 answers *what was
/// said* at roughly 2.5 %, but has no diarisation and returns no timings at all — one
/// blob of text per request. Neither is the transcript the customer asked for; the
/// transcript is the second one's words carrying the first one's labels.
///
/// The obvious way to get there is to slice the audio at every diarised turn and
/// re-transcribe each slice, which is what the Python prototype in the meeting folders
/// did. It costs an ffmpeg dependency and one API call per turn — four hundred calls
/// for a ninety-minute meeting. This class gets the same result from two calls and no
/// audio processing at all, by treating it as a text-alignment problem: the two
/// transcripts are ~85 % identical word-for-word, so the shared words are anchors that
/// carry a timestamp and a speaker across.
///
/// Alignment is patience-style, the algorithm git uses for diffs: match the words that
/// occur exactly once in both sequences, keep the longest strictly increasing run of
/// those matches (anything else would imply the two transcripts crossed over), then
/// recurse into the gaps between them. It is O(n log n) in practice, and unlike a plain
/// LCS it does not need a 20 000 × 20 000 matrix for a long meeting.
///
/// Pure functions, no I/O — this is the piece worth unit-testing (AlignmentTests).
///
/// Ported from <c>pks-agent-meeting/src/meeting-server/lib/recordings/align.mjs</c>,
/// whose own test suite is ported alongside it. Where JavaScript semantics differ from
/// .NET's defaults — rounding, sort stability, map iteration order — this file follows
/// the JavaScript, because the two implementations must agree word for word until the
/// server drops its copy.
/// </summary>
public static class Alignment
{
    /// <summary>
    /// Danish keeps æ ø å; the Unicode ranges also cover the accented letters that turn
    /// up in names. Everything else — punctuation, quotes, ellipses — is noise for
    /// matching purposes and would otherwise split a word in two.
    /// </summary>
    public static string Normalise(string? word)
    {
        if (string.IsNullOrEmpty(word)) return string.Empty;

        var lowered = word.ToLowerInvariant().Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            // The JavaScript class is [^0-9a-zà-öø-ÿ]; note the gap at U+00F7 (÷),
            // which sits between ö and ø and is arithmetic, not a letter.
            var keep = ch is >= '0' and <= '9'
                || ch is >= 'a' and <= 'z'
                || ch is >= 'à' and <= 'ö'
                || ch is >= 'ø' and <= 'ÿ';
            if (keep) sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Flatten the diarising engine's phrases into words. <paramref name="offsetMs"/> is
    /// the part's position in the whole recording, so parts share one timeline.
    /// </summary>
    public static List<DiarisedWord> DiarisedWords(SpeechResult result, long offsetMs = 0)
    {
        var output = new List<DiarisedWord>();
        foreach (var phrase in result.Phrases ?? [])
        {
            var words = phrase.Words is { Count: > 0 } ? phrase.Words : SynthesiseWords(phrase);
            foreach (var w in words)
            {
                var key = Normalise(w.Text);
                if (key.Length == 0) continue;
                output.Add(new DiarisedWord
                {
                    Key = key,
                    Speaker = phrase.Speaker,
                    StartMs = offsetMs + w.OffsetMilliseconds,
                    EndMs = offsetMs + w.OffsetMilliseconds + w.DurationMilliseconds,
                });
            }
        }

        return output;
    }

    /// <summary>
    /// Some locales come back without per-word timings. Spreading the phrase's duration
    /// evenly over its words is wrong in detail and right enough for alignment, which
    /// only needs monotone timestamps.
    /// </summary>
    private static List<SpeechWord> SynthesiseWords(SpeechPhrase phrase)
    {
        var parts = (phrase.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var each = parts.Length > 0 ? (double)phrase.DurationMilliseconds / parts.Length : 0d;

        var words = new List<SpeechWord>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            words.Add(new SpeechWord
            {
                Text = parts[i],
                OffsetMilliseconds = JsRound(phrase.OffsetMilliseconds + i * each),
                DurationMilliseconds = JsRound(each),
            });
        }

        return words;
    }

    /// <summary>JavaScript's Math.round: half away from zero towards +∞, not .NET's banker's rounding.</summary>
    private static long JsRound(double value) => (long)Math.Floor(value + 0.5);

    /// <summary>The verbatim engine returns one blob per request; this is its word stream.</summary>
    public static List<string> VerbatimWords(SpeechResult result)
    {
        var text = string.Join(" ", (result.CombinedPhrases ?? []).Select(c => c.Text ?? ""));

        return [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// Matching pairs between two word-key sequences, as (diarisedIndex, verbatimIndex).
    /// Recurses into the gaps between anchors; below a small size, or once the recursion
    /// has gone deep enough, falls back to matching equal words greedily in order — which
    /// in a gap of a handful of words is exactly as good.
    /// </summary>
    public static List<(int A, int B)> MatchWords(IReadOnlyList<string> a, IReadOnlyList<string> b, int depth = 0)
    {
        var result = new List<(int, int)>();
        MatchRange(a, 0, a.Count, b, 0, b.Count, depth, result);

        return result;
    }

    private static void MatchRange(
        IReadOnlyList<string> a, int aFrom, int aTo,
        IReadOnlyList<string> b, int bFrom, int bTo,
        int depth, List<(int, int)> result)
    {
        if (aFrom >= aTo || bFrom >= bTo) return;

        var aLen = aTo - aFrom;
        var bLen = bTo - bFrom;
        if (depth > 12 || (aLen <= 4 && bLen <= 4))
        {
            GreedyMatch(a, aFrom, aTo, b, bFrom, bTo, result);
            return;
        }

        var anchors = UniqueAnchors(a, aFrom, aTo, b, bFrom, bTo);
        if (anchors.Count == 0)
        {
            GreedyMatch(a, aFrom, aTo, b, bFrom, bTo, result);
            return;
        }

        var ai = aFrom;
        var bi = bFrom;
        foreach (var (x, y) in anchors)
        {
            if (x > ai && y > bi) MatchRange(a, ai, x, b, bi, y, depth + 1, result);
            result.Add((x, y));
            ai = x + 1;
            bi = y + 1;
        }
        if (ai < aTo && bi < bTo) MatchRange(a, ai, aTo, b, bi, bTo, depth + 1, result);
    }

    /// <summary>
    /// Longest strictly increasing run of unique-common word positions.
    ///
    /// "Unique-common" is the patience trick: a word occurring exactly once on each side
    /// can only match one way, so it is a trustworthy anchor. Frequent words ("og", "det")
    /// are skipped here precisely because they would match anywhere.
    /// </summary>
    private static List<(int A, int B)> UniqueAnchors(
        IReadOnlyList<string> a, int aFrom, int aTo,
        IReadOnlyList<string> b, int bFrom, int bTo)
    {
        var (countA, firstA, orderA) = Index(a, aFrom, aTo);
        var (countB, firstB, _) = Index(b, bFrom, bTo);

        var pairs = new List<(int A, int B)>();
        foreach (var key in orderA)
        {
            if (countA[key] != 1) continue;
            if (!countB.TryGetValue(key, out var nb) || nb != 1) continue;
            pairs.Add((firstA[key], firstB[key]));
        }
        pairs.Sort((x, y) => x.A.CompareTo(y.A));

        return LongestIncreasing(pairs);
    }

    private static (Dictionary<string, int> Count, Dictionary<string, int> First, List<string> Order) Index(
        IReadOnlyList<string> words, int from, int to)
    {
        var count = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var i = from; i < to; i++)
        {
            var key = words[i];
            if (count.TryGetValue(key, out var n))
            {
                count[key] = n + 1;
            }
            else
            {
                count[key] = 1;
                first[key] = i;
                order.Add(key);
            }
        }

        return (count, first, order);
    }

    /// <summary>Patience sorting over the b-coordinate: O(k log k).</summary>
    private static List<(int A, int B)> LongestIncreasing(List<(int A, int B)> pairs)
    {
        if (pairs.Count == 0) return [];

        var tails = new List<int>();
        var tailIndex = new List<int>();
        var prev = new int[pairs.Count];
        Array.Fill(prev, -1);

        for (var i = 0; i < pairs.Count; i++)
        {
            var b = pairs[i].B;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (tails[mid] < b) lo = mid + 1;
                else hi = mid;
            }
            if (lo == tails.Count)
            {
                tails.Add(b);
                tailIndex.Add(i);
            }
            else
            {
                tails[lo] = b;
                tailIndex[lo] = i;
            }
            prev[i] = lo > 0 ? tailIndex[lo - 1] : -1;
        }

        var output = new List<(int A, int B)>();
        for (var i = tailIndex[^1]; i >= 0; i = prev[i]) output.Add(pairs[i]);
        output.Reverse();

        return output;
    }

    private static void GreedyMatch(
        IReadOnlyList<string> a, int aFrom, int aTo,
        IReadOnlyList<string> b, int bFrom, int bTo,
        List<(int, int)> result)
    {
        var j = bFrom;
        for (var i = aFrom; i < aTo && j < bTo; i++)
        {
            // Allow a short look-ahead so one inserted word does not desynchronise the
            // rest of the gap.
            var limit = Math.Min(bTo, j + 3);
            for (var k = j; k < limit; k++)
            {
                if (!string.Equals(a[i], b[k], StringComparison.Ordinal)) continue;
                result.Add((i, k));
                j = k + 1;
                break;
            }
        }
    }

    /// <summary>
    /// The merge. Verbatim words in, verbatim words out — each carrying the speaker and
    /// timestamp of the diarised word it aligned to.
    /// </summary>
    public static List<LabelledWord> LabelWords(IReadOnlyList<string> verbatim, IReadOnlyList<DiarisedWord> diarised)
    {
        var words = new List<LabelledWord>(verbatim.Count);
        foreach (var raw in verbatim)
        {
            var key = Normalise(raw);
            if (key.Length == 0) continue;
            words.Add(new LabelledWord { Raw = raw, Key = key });
        }

        if (diarised.Count == 0) return words;

        var diarisedKeys = diarised.Select(w => w.Key).ToList();
        var verbatimKeys = words.Select(w => w.Key).ToList();
        foreach (var (d, v) in MatchWords(diarisedKeys, verbatimKeys))
        {
            var src = diarised[d];
            words[v].Speaker = src.Speaker;
            words[v].StartMs = src.StartMs;
            words[v].EndMs = src.EndMs;
            words[v].Anchored = true;
        }

        // Unanchored words inherit forwards from the previous anchor — a word the verbatim
        // engine heard and the diariser missed belongs to whoever was speaking — and
        // backwards only for the run before the first anchor.
        LabelledWord? last = null;
        foreach (var w in words)
        {
            if (w.Anchored)
            {
                last = w;
            }
            else if (last is not null)
            {
                w.Speaker = last.Speaker;
                w.StartMs = last.EndMs;
                w.EndMs = last.EndMs;
            }
        }

        LabelledWord? next = null;
        for (var i = words.Count - 1; i >= 0; i--)
        {
            if (words[i].Anchored)
            {
                next = words[i];
            }
            else if (next is not null && string.IsNullOrEmpty(words[i].Speaker))
            {
                words[i].Speaker = next.Speaker;
                words[i].StartMs = next.StartMs;
                words[i].EndMs = next.StartMs;
            }
        }

        return words;
    }

    /* ── sentence-edge snapping ─────────────────────────────────────────────────
     *
     * The failure this fixes, in the words of the person who found it: David asked
     * "Hvad sagde du?", and the transcript reads
     *
     *     David  Hvad sagde
     *     Poul   Du? Hvad laver du til daglig?
     *
     * One word crossed the turn boundary. It is the characteristic error of the whole
     * approach and it has two compounding causes. Frequent words ("du", "og", "det") can
     * never be unique anchors, so they are matched greedily and can bind to the wrong
     * occurrence; and the diariser's own turn boundary is placed from acoustics, which
     * puts it within a word or two of the truth rather than on it. TurnsFromWords then
     * glues the stray word into the neighbouring paragraph, where it reads as something
     * the other person said.
     *
     * The fix uses the one signal the diariser does not have and the verbatim engine
     * does: punctuation. Raw keeps it, so the word stream can be cut into sentences, and
     * a sentence is a much better guess at "one person speaking" than a run of
     * acoustically similar milliseconds. Within a sentence, a one- or two-word run of a
     * different speaker *at either edge* is boundary noise and gets snapped to the
     * sentence's majority speaker.
     *
     * Deliberately narrow, in three ways:
     *
     *  - Only the edges. A run in the *middle* of a sentence is left exactly as it is,
     *    because that is where genuine back-channels live ("og så tænkte jeg — ja — at vi
     *    skulle…"). Those are real, and merging them away would be the same class of
     *    error in the other direction.
     *  - Only short runs. Two words is boundary noise; five is a person talking.
     *  - Only labels. Not one word of the record changes.
     *
     * What it costs: when one speaker genuinely finishes the other's sentence ("…og så
     * bliver det" / "dyrere.") the completion is snapped back to the first speaker. That
     * is a real loss, accepted because it is far rarer than the boundary noise it
     * removes — and it is the reason the threshold is a parameter and every moved word is
     * marked Snapped rather than being erased.
     */

    private static readonly Regex SentenceEnd = new(@"[.?!…]+[""'»«’”)\]]?$", RegexOptions.Compiled);
    private static readonly Regex StrongTerminator = new(@"[?!…]", RegexOptions.Compiled);

    /// <summary>
    /// Does this word end a sentence? Abbreviation-aware, and shared with the subtitle cue
    /// cutter so a cue never breaks after "f.eks." while the merge treats it as mid-sentence.
    /// </summary>
    public static bool EndsSentence(LabelledWord word, LabelledWord? next)
    {
        if (!SentenceEnd.IsMatch(word.Raw)) return false;

        // "?", "!" and "…" are never abbreviations. A bare full stop is: Danish runs on
        // "bl.a.", "f.eks.", "kl.", and cutting a sentence there would invent boundaries
        // in the middle of a phrase. Requiring the next word to look like the start of a
        // sentence misses a few real boundaries and invents none.
        if (StrongTerminator.IsMatch(word.Raw)) return true;
        if (next is null || next.Raw.Length == 0) return true;

        var first = next.Raw[0];

        return char.ToUpperInvariant(first) == first && char.ToLowerInvariant(first) != first;
    }

    /// <summary>
    /// Snap one- or two-word runs at a sentence's edges to that sentence's majority
    /// speaker. Returns how many labels moved. Mutates <paramref name="words"/> in place;
    /// no word's text is changed.
    /// </summary>
    public static int SnapSentenceEdges(IList<LabelledWord> words, int maxEdgeWords = 2)
    {
        var moved = 0;
        var from = 0;
        for (var i = 0; i < words.Count; i++)
        {
            if (i != words.Count - 1 && !EndsSentence(words[i], words[i + 1])) continue;
            moved += SnapOneSentence(words, from, i, maxEdgeWords);
            from = i + 1;
        }

        return moved;
    }

    private static int SnapOneSentence(IList<LabelledWord> words, int from, int to, int maxEdgeWords)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        for (var i = from; i <= to; i++)
        {
            // An unlabelled word means the words-only fallback or a gap before the first
            // anchor. There is no majority to snap towards, and inventing one would be
            // exactly the guessing this module refuses to do.
            var speaker = words[i].Speaker;
            if (speaker is null) return 0;
            if (counts.TryGetValue(speaker, out var n))
            {
                counts[speaker] = n + 1;
            }
            else
            {
                counts[speaker] = 1;
                order.Add(speaker);
            }
        }
        if (counts.Count < 2) return 0;

        string? majority = null;
        var best = 0;
        var tied = false;
        foreach (var speaker in order)
        {
            var n = counts[speaker];
            if (n > best)
            {
                majority = speaker;
                best = n;
                tied = false;
            }
            else if (n == best)
            {
                tied = true;
            }
        }

        // A tie is a sentence two people genuinely share; a majority of one is not a
        // majority. Both are left alone.
        if (tied || best < 2) return 0;

        var moved = 0;

        var head = from;
        while (!string.Equals(words[head].Speaker, majority, StringComparison.Ordinal)) head++;
        if (head - from > 0 && head - from <= maxEdgeWords && head - from < best)
        {
            for (var i = from; i < head; i++) moved += Assign(words[i], majority!);
        }

        var tail = to;
        while (!string.Equals(words[tail].Speaker, majority, StringComparison.Ordinal)) tail--;
        if (to - tail > 0 && to - tail <= maxEdgeWords && to - tail < best)
        {
            for (var i = tail + 1; i <= to; i++) moved += Assign(words[i], majority!);
        }

        return moved;
    }

    /// <summary>
    /// The timestamps stay as they were. They came from the diariser, they are right to
    /// within the same word or two this is correcting, and TurnsFromWords only needs them
    /// to stay monotone — which they do, because nothing is reordered.
    /// </summary>
    private static int Assign(LabelledWord word, string speaker)
    {
        word.Speaker = speaker;
        word.Snapped = true;

        return 1;
    }

    /* ── engine comparison ──────────────────────────────────────────────────────
     *
     * Two diarisers labelling the same words never agree on the *names* of the speakers —
     * one says "1" and "2", the other "speaker_0" and "speaker_1", and which is which is
     * arbitrary per run. So agreement is measured after fitting the best one-to-one
     * mapping between the two label sets: build the confusion matrix over words both
     * engines labelled, take the largest cell, claim that row and column, repeat. Greedy
     * rather than Hungarian because meetings have a handful of speakers, not a hundred.
     */

    public static LabelAgreementResult LabelAgreement(
        IReadOnlyList<ISpeakerLabelled> left,
        IReadOnlyList<ISpeakerLabelled> right)
    {
        var counts = new Dictionary<(string L, string R), int>();
        var order = new List<(string L, string R)>();
        var compared = 0;
        var n = Math.Min(left.Count, right.Count);
        for (var i = 0; i < n; i++)
        {
            var l = left[i].Speaker;
            var r = right[i].Speaker;
            if (l is null || r is null) continue;
            var cell = (l, r);
            if (counts.TryGetValue(cell, out var c))
            {
                counts[cell] = c + 1;
            }
            else
            {
                counts[cell] = 1;
                order.Add(cell);
            }
            compared++;
        }
        if (compared == 0) return new LabelAgreementResult(0, 0, new Dictionary<string, string>());

        // OrderByDescending is a stable sort, which matters: JavaScript's Array.sort is
        // stable too, so equal-sized cells are claimed in the same order in both ports.
        var cells = order.OrderByDescending(cell => counts[cell]).ToList();

        var takenLeft = new HashSet<string>(StringComparer.Ordinal);
        var takenRight = new HashSet<string>(StringComparer.Ordinal);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var agreed = 0;
        foreach (var cell in cells)
        {
            if (takenLeft.Contains(cell.L) || takenRight.Contains(cell.R)) continue;
            takenLeft.Add(cell.L);
            takenRight.Add(cell.R);
            mapping[cell.L] = cell.R;
            agreed += counts[cell];
        }

        return new LabelAgreementResult((double)agreed / compared, compared, mapping);
    }

    /// <summary>Consecutive words by the same speaker become one turn.</summary>
    public static List<Turn> TurnsFromWords(IEnumerable<LabelledWord> words)
    {
        var turns = new List<Turn>();
        foreach (var w in words)
        {
            var current = turns.Count > 0 ? turns[^1] : null;
            if (current is not null && string.Equals(current.Speaker, w.Speaker, StringComparison.Ordinal))
            {
                current.Text += " " + w.Raw;
                current.EndMs = Math.Max(current.EndMs, w.EndMs);
            }
            else
            {
                turns.Add(new Turn { Speaker = w.Speaker, StartMs = w.StartMs, EndMs = w.EndMs, Text = w.Raw });
            }
        }

        return turns;
    }

    /// <summary>
    /// The share of words that matched a diarised word directly. Surfaced so a transcript
    /// that aligned badly can say so rather than quietly guess.
    /// </summary>
    public static double AnchoredRatio(IReadOnlyList<LabelledWord> words)
    {
        if (words.Count == 0) return 0;

        return (double)words.Count(w => w.Anchored) / words.Count;
    }
}
