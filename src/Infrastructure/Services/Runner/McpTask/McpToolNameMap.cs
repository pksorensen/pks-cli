using System.Text;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>
/// Maps MCP tools to names the Anthropic Messages API accepts (<c>^[a-zA-Z0-9_-]{1,64}$</c>):
/// <c>&lt;server&gt;__&lt;tool&gt;</c> with <c>.</c> (and any other disallowed char) turned into <c>_</c>.
/// Keeps the reverse map so a model-emitted name resolves back to the original server + tool, and
/// de-duplicates collisions (e.g. <c>a.b</c> and <c>a_b</c>) with a numeric suffix.
/// </summary>
public sealed class McpToolNameMap
{
    public const int MaxNameLength = 64;

    private readonly Dictionary<string, (string Server, string Tool)> _byExposed = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, (string Server, string Tool)> Entries => _byExposed;

    public static string Sanitize(string server, string tool)
    {
        var raw = $"{server}__{tool}";
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        var s = sb.ToString();
        return s.Length > MaxNameLength ? s[..MaxNameLength] : s;
    }

    /// <summary>Registers a tool and returns the name exposed to the model.</summary>
    public string Add(string server, string tool)
    {
        var baseName = Sanitize(server, tool);
        var name = baseName;
        for (var i = 2; _byExposed.ContainsKey(name); i++)
        {
            var suffix = $"_{i}";
            name = (baseName.Length + suffix.Length > MaxNameLength ? baseName[..(MaxNameLength - suffix.Length)] : baseName) + suffix;
        }
        _byExposed[name] = (server, tool);
        return name;
    }

    public bool TryResolve(string exposedName, out string server, out string tool)
    {
        if (_byExposed.TryGetValue(exposedName, out var e))
        {
            server = e.Server;
            tool = e.Tool;
            return true;
        }
        server = tool = "";
        return false;
    }
}
