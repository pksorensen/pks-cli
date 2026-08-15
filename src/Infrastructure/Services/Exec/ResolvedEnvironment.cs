using System.Diagnostics;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Exec;

/// <summary>
/// The answers to a manifest, on their way to a child process — and nowhere else.
///
/// A resolved manifest is mostly harmless strings and one or two that are not: an endpoint URL beside
/// a Foundry key, a model name beside an IMDS header secret. Returning that as a
/// <c>Dictionary&lt;string, string&gt;</c> would put a live credential in the hands of a command, which
/// is exactly the shape the quarantine exists to prevent — so the resolver returns this instead. A
/// command can count the entries, print the descriptions and hand the whole thing to a
/// <see cref="ProcessStartInfo"/>; it cannot read a secret out of it, because there is no method that
/// gives one back.
/// </summary>
public sealed class ResolvedEnvironment
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly record struct Entry(SecretValue Value, bool Secret);

    /// <summary>Records one answer. <paramref name="secret"/> decides only whether the value may be
    /// shown — it lands in the child's environment either way.</summary>
    public void Set(string name, string value, bool secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _entries[name] = new Entry(SecretValue.From(value), secret);
    }

    public int Count => _entries.Count;

    public bool Contains(string name) => _entries.ContainsKey(name);

    /// <summary>The variable names, in order. Names are not credentials.</summary>
    public IReadOnlyCollection<string> Names => _entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// What to print. A secret is reported by presence — <c>(set, hidden)</c> — because the useful
    /// question at a terminal is whether the value arrived, and the value itself answers a question
    /// nobody asked.
    /// </summary>
    public IEnumerable<(string Name, string Display)> Describe()
    {
        foreach (var name in Names)
        {
            var entry = _entries[name];
            var display = entry.Secret
                ? (entry.Value.HasValue ? "(set, hidden)" : "(not set)")
                : (entry.Value.Reveal() ?? "");
            yield return (name, display);
        }
    }

    /// <summary>Puts every answer into a child process's environment — the sanctioned exit, via
    /// <see cref="SecretSink"/>, so the plaintext is never a value any caller holds.</summary>
    public void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        foreach (var (name, entry) in _entries)
        {
            SecretSink.SetEnvironmentVariable(startInfo, name, entry.Value);
        }
    }

    /// <summary>Merges another set of answers on top of this one. Later wins, which is what makes a
    /// second capability able to override the first's endpoint.</summary>
    public void MergeFrom(ResolvedEnvironment other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (name, entry) in other._entries)
        {
            _entries[name] = entry;
        }
    }
}
