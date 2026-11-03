using System.Text.RegularExpressions;

namespace PKS.Commands.Vm;

/// <summary>What <see cref="LocalSshConfig.Upsert"/> did, or refused to do.</summary>
public enum SshConfigOutcome
{
    /// <summary>The alias was not in the file; a managed block was appended.</summary>
    Added,

    /// <summary>A managed block for this alias existed and its body changed.</summary>
    Updated,

    /// <summary>A managed block for this alias existed and already said exactly this.</summary>
    Unchanged,

    /// <summary>
    /// The alias is already declared outside our markers. Nothing was written: ssh resolves an
    /// option from the <em>first</em> matching <c>Host</c> block, so appending a second one would be
    /// silently ignored, and rewriting someone's hand-written stanza is not ours to do.
    /// </summary>
    ConflictingAlias,
}

/// <summary>
/// Writes the <c>Host</c> stanza Codex Desktop needs into the user's own <c>~/.ssh/config</c>.
///
/// This exists because of where the two halves of the job run. The bootstrap half runs on the box,
/// over ssh; this half runs on the laptop, which is also where <c>pks vm init</c> is invoked and
/// where the private key it generated already lives. So the command can finish the job rather than
/// print instructions — and finishing it is the whole of "easy to use from the Codex app", since the
/// app offers no host that is not in that file.
///
/// The file belongs to the user and frequently contains hand-written entries (jump hosts, work
/// boxes, per-host key paths), so every write here is conservative: a delimited block that can be
/// found and removed again, replacement only of a block we wrote, and a refusal — never an
/// overwrite — when the alias is already spoken for.
/// </summary>
public static class LocalSshConfig
{
    private const string Begin = "# >>> pks vm codex";
    private const string End = "# <<< pks vm codex";

    public static string BeginMarker(string alias) => $"{Begin}: {alias} >>>";
    public static string EndMarker(string alias) => $"{End}: {alias} <<<";

    /// <summary>
    /// <c>~/.ssh/config</c> — on Windows that is <c>C:\Users\&lt;name&gt;\.ssh\config</c>, which is
    /// also where OpenSSH and therefore Codex look.
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>
    /// The pure half: given the current file contents, return what they should become. Separated
    /// from the file I/O so the conflict and idempotency rules can be tested without a home
    /// directory to write into.
    /// </summary>
    public static (string Content, SshConfigOutcome Outcome) Upsert(string existing, string alias, string stanza)
    {
        var begin = BeginMarker(alias);
        var end = EndMarker(alias);

        // A Windows-authored config is CRLF throughout, and this is a Windows-first feature. So our
        // block adopts the file's own line ending, and everything outside the block is spliced back
        // byte for byte rather than rebuilt from split lines -- rewriting the whole file to LF would
        // turn a two-line addition into a diff against every line the user wrote.
        var nl = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = string.Join(nl, new[] { begin }
            .Concat(stanza.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            .Append(end));

        var found = Locate(existing, begin, end);
        if (found is not null)
        {
            var (from, to) = found.Value;

            // A begin marker with no end means somebody edited inside our block. Rewriting from the
            // marker to end-of-file would eat whatever they put after it.
            if (to < 0) return (existing, SshConfigOutcome.ConflictingAlias);

            var current = existing[from..to];
            if (Normalize(current) == Normalize(block)) return (existing, SshConfigOutcome.Unchanged);

            return (existing[..from] + block + existing[to..], SshConfigOutcome.Updated);
        }

        // ssh takes the first matching Host block, so an alias declared anywhere else in the file
        // already wins and a second declaration would do nothing at all.
        if (DeclaresHost(existing.Replace("\r\n", "\n").Split('\n'), alias))
            return (existing, SshConfigOutcome.ConflictingAlias);

        var prefix = existing.Length == 0 ? string.Empty : existing.TrimEnd('\n', '\r') + nl + nl;
        return (prefix + block + nl, SshConfigOutcome.Added);
    }

    /// <summary>
    /// Character offsets of our block in the raw text: start of the begin-marker line, and the index
    /// just past the end-marker line. Null when there is no begin marker; a negative <c>To</c> when
    /// the begin marker has no matching end.
    /// </summary>
    private static (int From, int To)? Locate(string text, string begin, string end)
    {
        var beginMatch = LineMatch(begin).Match(text);
        if (!beginMatch.Success) return null;

        var endMatch = LineMatch(end).Match(text, beginMatch.Index + beginMatch.Length);
        return endMatch.Success
            ? (beginMatch.Index, endMatch.Index + endMatch.Length)
            : (beginMatch.Index, -1);
    }

    /// <summary>
    /// A whole line equal to <paramref name="marker"/>, tolerating indentation.
    ///
    /// The CR is matched by a lookahead so the match does not consume it. Consuming it put the
    /// splice point between the CR and the LF of a CRLF file, which both broke idempotency (the
    /// recovered block ended in a stray CR that the new one did not) and produced a file with mixed
    /// line endings on an update.
    /// </summary>
    private static Regex LineMatch(string marker)
        => new($"^[ \t]*{Regex.Escape(marker)}[ \t]*(?=\r?$)", RegexOptions.Multiline);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n', '\r');

    /// <summary>
    /// Whether any <c>Host</c> line names this alias exactly. Pattern entries (<c>Host *</c>,
    /// <c>Host *.example.com</c>) are deliberately not treated as a conflict: they are not what Codex
    /// offers, and one of them matching everything is the normal state of a real config file.
    /// </summary>
    private static bool DeclaresHost(IEnumerable<string> lines, string alias)
        => lines.Select(l => l.Trim())
            .Where(l => l.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            .SelectMany(l => l[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(h => string.Equals(h, alias, StringComparison.Ordinal));

    /// <summary>
    /// Applies <see cref="Upsert"/> to the file at <paramref name="path"/>, creating the directory
    /// and file if needed. Returns the outcome; a <see cref="SshConfigOutcome.ConflictingAlias"/>
    /// means the file was not touched and the caller should show the stanza instead.
    /// </summary>
    public static SshConfigOutcome Write(string path, string alias, string stanza)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var (content, outcome) = Upsert(existing, alias, stanza);
        if (outcome is SshConfigOutcome.Unchanged or SshConfigOutcome.ConflictingAlias) return outcome;

        // ssh refuses a group- or world-writable config, so a file we create has to be tightened or
        // every later ssh from this machine fails with "Bad owner or permissions".
        var fresh = !File.Exists(path);
        File.WriteAllText(path, content);
        if (fresh && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return outcome;
    }
}
