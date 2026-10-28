using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Devcontainer;

/// <summary>One running devcontainer to probe, with the user the probe should run as (when known).</summary>
public sealed record ProbeTarget(string ContainerId, string? User);

/// <summary>A live Claude Code session found inside a devcontainer (from <c>~/.claude/sessions/&lt;pid&gt;.json</c>).</summary>
public sealed class ClaudeSessionInfo
{
    public string Source { get; init; } = "claude";
    public string? SessionId { get; init; }
    public long? Pid { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Kind { get; init; }
    public string? Cwd { get; init; }
    [JsonConverter(typeof(UtcSecondsDateTimeOffsetConverter))]
    public DateTimeOffset? StartedAt { get; init; }
    [JsonConverter(typeof(UtcSecondsDateTimeOffsetConverter))]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A tmux session found inside a devcontainer.</summary>
public sealed class TmuxSessionInfo
{
    public string Name { get; init; } = "";
    public int Windows { get; init; }
    public bool Attached { get; init; }
    [JsonConverter(typeof(UtcSecondsDateTimeOffsetConverter))]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>What the probe found in one container.</summary>
public sealed class ContainerProbeResult
{
    public List<ClaudeSessionInfo> Sessions { get; } = new();
    public List<TmuxSessionInfo> Tmux { get; } = new();
    /// <summary>Session files whose pid is gone (or was reused by another process).</summary>
    public int StaleSessionFiles { get; set; }
    public string? ProbeError { get; set; }
}

/// <summary>Serialises as <c>yyyy-MM-ddTHH:mm:ssZ</c> (UTC, whole seconds) — the shape the desktop consumer expects.</summary>
public sealed class UtcSecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The POSIX <c>sh</c> probe that runs inside a devcontainer and the host-side wrapper that runs it in
/// each container. Everything is delivered base64-encoded (<see cref="BuildShellCommand"/>) so no quoting
/// has to survive ssh, the login shell or <c>docker exec</c>.
/// </summary>
public static class SessionProbeScript
{
    /// <summary>
    /// Runs inside the container. Emits one <c>SESSION\t&lt;alive&gt;\t&lt;json&gt;</c> line per Claude Code
    /// session file and one <c>TMUX\t&lt;name&gt;\t&lt;windows&gt;\t&lt;created&gt;\t&lt;attached&gt;</c> line per tmux session.
    /// <para>
    /// Deliberately not <c>~</c>-relative: <c>docker exec</c> runs as the image's <c>USER</c>, which is often
    /// root while the devcontainer's <c>remoteUser</c> is <c>node</c>. Scanning every home and every
    /// per-user tmux socket makes the result independent of who the probe happens to run as.
    /// A session is alive only if <c>/proc/&lt;pid&gt;</c> exists and, when the file records
    /// <c>procStart</c>, it matches field 22 of <c>/proc/&lt;pid&gt;/stat</c> — a reused pid after a
    /// container restart must not resurrect a dead session.
    /// </para>
    /// </summary>
    public const string ContainerProbe =
        "# pks devcontainer sessions probe (POSIX sh)\n" +
        "for f in /home/*/.claude/sessions/*.json /root/.claude/sessions/*.json; do\n" +
        "  [ -f \"$f\" ] || continue\n" +
        "  line=$(head -c 65536 \"$f\" 2>/dev/null | tr -d '\\r\\n')\n" +
        "  [ -n \"$line\" ] || continue\n" +
        "  pid=$(printf '%s' \"$line\" | sed -n 's/.*[{,]\"pid\"[[:space:]]*:[[:space:]]*\\([0-9][0-9]*\\).*/\\1/p')\n" +
        "  alive=0\n" +
        "  if [ -n \"$pid\" ] && [ -d \"/proc/$pid\" ]; then\n" +
        "    alive=1\n" +
        "    ps=$(printf '%s' \"$line\" | sed -n 's/.*[{,]\"procStart\"[[:space:]]*:[[:space:]]*\"\\{0,1\\}\\([0-9][0-9]*\\).*/\\1/p')\n" +
        "    if [ -n \"$ps\" ]; then\n" +
        "      st=$(cat \"/proc/$pid/stat\" 2>/dev/null)\n" +
        "      if [ -n \"$st\" ]; then\n" +
        "        rest=${st##*) }\n" +
        "        set -- $rest\n" +
        "        [ \"${20}\" = \"$ps\" ] || alive=0\n" +
        "      fi\n" +
        "    fi\n" +
        "  fi\n" +
        "  printf 'SESSION\\t%s\\t%s\\n' \"$alive\" \"$line\"\n" +
        "done\n" +
        // tmux's client replaces control characters (tabs included) with '_' when stdout is not a
        // terminal (utf8_sanitize), so the fields are space-separated with the name LAST and the
        // probe reorders them into the tab protocol itself.
        "fmt='#{session_windows} #{session_created} #{session_attached} #{session_name}'\n" +
        "me=$(id -un 2>/dev/null)\n" +
        "for s in /tmp/tmux-*/default; do\n" +
        "  [ -S \"$s\" ] || continue\n" +
        "  out=$(tmux -S \"$s\" ls -F \"$fmt\" </dev/null 2>/dev/null)\n" +
        "  if [ $? -ne 0 ] && [ -z \"$out\" ]; then\n" +
        "    owner=$(stat -c %U \"$s\" 2>/dev/null)\n" +
        "    if [ -n \"$owner\" ] && [ \"$owner\" != \"$me\" ]; then\n" +
        "      out=$(su \"$owner\" -s /bin/sh -c \"tmux -S '$s' ls -F '$fmt'\" </dev/null 2>/dev/null)\n" +
        "    fi\n" +
        "  fi\n" +
        "  printf '%s\\n' \"$out\" | while IFS= read -r l; do\n" +
        "    [ -n \"$l\" ] || continue\n" +
        "    w=${l%% *}; r=${l#* }; c=${r%% *}; r=${r#* }; a=${r%% *}; n=${r#* }\n" +
        "    printf 'TMUX\\t%s\\t%s\\t%s\\t%s\\n' \"$n\" \"$w\" \"$c\" \"$a\"\n" +
        "  done\n" +
        "done\n" +
        "exit 0\n";

    private static readonly Regex SafeToken = new("^[A-Za-z0-9_.:-]+$", RegexOptions.Compiled);
    private static readonly Regex RemoteUserRegex = new("\"remoteUser\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// The host-side script: for every container, <c>BEGIN\t&lt;id&gt;</c>, the probe's output (stderr merged),
    /// then <c>END\t&lt;id&gt;\t&lt;exit&gt;</c>. When a user is given the probe first runs as that user and
    /// falls back to the image's default user if <c>docker exec -u</c> itself fails (user missing from
    /// <c>/etc/passwd</c>). The container probe is embedded as a heredoc and passed as one <c>sh -c</c>
    /// argument, so the container needs nothing beyond <c>sh</c>. Each exec is bounded by
    /// <c>timeout 30</c> when the host has it, so one wedged container cannot hang the poll.
    /// </summary>
    public static string BuildHostScript(IEnumerable<ProbeTarget> targets)
    {
        var sb = new StringBuilder();
        sb.Append("probe=$(cat <<'PKS_PROBE_EOF'\n");
        sb.Append(ContainerProbe);
        sb.Append("PKS_PROBE_EOF\n)\n");
        // A wedged container would hang `docker exec` and with it the whole poll; bound each probe.
        sb.Append("if command -v timeout >/dev/null 2>&1; then t='timeout 30'; else t=''; fi\n");

        foreach (var t in targets)
        {
            if (string.IsNullOrEmpty(t.ContainerId) || !SafeToken.IsMatch(t.ContainerId))
                throw new ArgumentException($"Container id '{t.ContainerId}' is not a safe shell token.", nameof(targets));
            if (t.User != null && !SafeToken.IsMatch(t.User))
                throw new ArgumentException($"User '{t.User}' is not a safe shell token.", nameof(targets));

            sb.Append($"printf 'BEGIN\\t{t.ContainerId}\\n'\n");
            if (t.User != null)
            {
                sb.Append($"$t docker exec -u {t.User} {t.ContainerId} sh -c \"$probe\" 2>&1\n");
                sb.Append("rc=$?\n");
                sb.Append("if [ $rc -ne 0 ]; then\n");
                sb.Append($"  $t docker exec {t.ContainerId} sh -c \"$probe\" 2>&1\n");
                sb.Append("  rc=$?\n");
                sb.Append("fi\n");
            }
            else
            {
                sb.Append($"$t docker exec {t.ContainerId} sh -c \"$probe\" 2>&1\n");
                sb.Append("rc=$?\n");
            }
            sb.Append($"printf 'END\\t{t.ContainerId}\\t%s\\n' \"$rc\"\n");
        }

        sb.Append("exit 0\n");
        return sb.ToString();
    }

    /// <summary>
    /// Wraps any script as a one-line command that is safe to hand to <see cref="ISshCommandRunner"/>
    /// (which quotes with bare double quotes) and to a login shell: only <c>[A-Za-z0-9+/=]</c> and spaces.
    /// </summary>
    public static string BuildShellCommand(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"echo {b64} | base64 -d | sh";
    }

    /// <summary>
    /// Reads <c>remoteUser</c> out of the raw <c>Labels</c> string from <c>docker ps --format json</c>.
    /// The <c>devcontainer.metadata</c> label is a JSON array of layered feature/config objects; the
    /// last <c>remoteUser</c> wins, mirroring how the devcontainer CLI merges metadata.
    /// </summary>
    public static string? ExtractRemoteUser(string? labels)
    {
        if (string.IsNullOrEmpty(labels)) return null;
        var matches = RemoteUserRegex.Matches(labels);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value;
    }
}

/// <summary>
/// What <c>docker ps</c> can tell about a devcontainer from its labels. Every container the
/// devcontainer CLI creates carries <c>devcontainer.metadata</c> (baked into the image), which is
/// why that is the label to filter on: on 2026-10-28 the hetzner host had 26 such containers and
/// not one with <c>devcontainer.local_folder</c>.
/// </summary>
public static class DevcontainerLabels
{
    /// <summary>The <c>docker ps</c> filter that catches every devcontainer-derived container.</summary>
    public const string Filter = "label=devcontainer.metadata";

    private static readonly Regex VscImage = new("^vsc-(.+)-[0-9a-f]{64}(-features|-uid)?$", RegexOptions.Compiled);

    /// <summary>
    /// Derives the project name and workspace folder: <c>pks.project</c> / <c>devcontainer.local_folder</c>
    /// first, then the directory holding <c>devcontainer.config_file</c>, then the VS Code image name
    /// (<c>vsc-&lt;project&gt;-&lt;hash&gt;</c>), then the container name.
    /// </summary>
    public static (string Project, string LocalFolder) Describe(string? labels, string containerName, string image)
    {
        var pksProject = "";
        var localFolder = "";
        var configFile = "";
        foreach (var label in (labels ?? "").Split(','))
        {
            var kv = label.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim())
            {
                case "pks.project": pksProject = kv[1].Trim(); break;
                case "devcontainer.local_folder": localFolder = kv[1].Trim(); break;
                case "devcontainer.config_file": configFile = kv[1].Trim(); break;
            }
        }

        if (localFolder.Length == 0 && configFile.Length > 0)
        {
            // /workspaces/x/.devcontainer/devcontainer.json -> /workspaces/x ; /workspaces/x/.devcontainer.json -> /workspaces/x
            var dir = Path.GetDirectoryName(configFile.Replace('\\', '/')) ?? "";
            if (Path.GetFileName(dir) == ".devcontainer") dir = Path.GetDirectoryName(dir) ?? "";
            localFolder = dir;
        }

        string project;
        if (pksProject.Length > 0) project = pksProject;
        else if (localFolder.Length > 0) project = Path.GetFileName(localFolder.TrimEnd('/'));
        else if (VscImage.Match(image) is { Success: true } m) project = m.Groups[1].Value;
        else project = containerName;

        return (project, localFolder);
    }
}

/// <summary>Docker's human timestamps (<c>2026-10-27 22:36:22 +0100 CET</c>) as ISO-8601 UTC.</summary>
public static class DockerTimestamps
{
    private static readonly Regex DockerDate = new(@"^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}) ([+-]\d{2})(\d{2})", RegexOptions.Compiled);

    /// <summary>Returns <c>yyyy-MM-ddTHH:mm:ssZ</c>, or the input unchanged when it is not a docker date.</summary>
    public static string ToIso(string? dockerDate)
    {
        if (string.IsNullOrWhiteSpace(dockerDate)) return dockerDate ?? "";
        var m = DockerDate.Match(dockerDate.Trim());
        if (!m.Success) return dockerDate;
        if (!DateTimeOffset.TryParseExact($"{m.Groups[1].Value}T{m.Groups[2].Value}{m.Groups[3].Value}:{m.Groups[4].Value}",
                "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dockerDate;
        return dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}

/// <summary>Parses the line protocol produced by <see cref="SessionProbeScript"/>.</summary>
public static class SessionProbeParser
{
    /// <summary>Parses one container's probe output.</summary>
    public static ContainerProbeResult Parse(string? stdout)
    {
        var result = new ContainerProbeResult();
        if (string.IsNullOrEmpty(stdout)) return result;
        ParseInto(result, stdout.Split('\n'), out _);
        return result;
    }

    /// <summary>
    /// Parses the host-level output, one chunk per container framed by <c>BEGIN\t&lt;id&gt;</c> and
    /// <c>END\t&lt;id&gt;\t&lt;exit&gt;</c>. A non-zero exit, or a missing END, becomes <see cref="ContainerProbeResult.ProbeError"/>.
    /// </summary>
    public static Dictionary<string, ContainerProbeResult> ParseHostOutput(string? stdout)
    {
        var results = new Dictionary<string, ContainerProbeResult>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(stdout)) return results;

        string? currentId = null;
        var chunk = new List<string>();

        void Flush(string? exitField)
        {
            if (currentId == null) return;
            var result = new ContainerProbeResult();
            ParseInto(result, chunk, out var noise);
            if (exitField == null)
            {
                result.ProbeError = "probe output was cut off before END" + (noise.Count > 0 ? ": " + string.Join(" | ", noise) : "");
            }
            else if (exitField != "0")
            {
                result.ProbeError = $"probe exited {exitField}" + (noise.Count > 0 ? ": " + string.Join(" | ", noise) : "");
            }
            results[currentId] = result;
            currentId = null;
            chunk = new List<string>();
        }

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("BEGIN\t", StringComparison.Ordinal))
            {
                Flush(null);
                currentId = line.Substring("BEGIN\t".Length).Trim();
                continue;
            }
            if (line.StartsWith("END\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                var id = parts.Length > 1 ? parts[1].Trim() : "";
                if (currentId != null && id == currentId)
                {
                    Flush(parts.Length > 2 ? parts[2].Trim() : "");
                    continue;
                }
            }
            if (currentId != null) chunk.Add(line);
        }
        Flush(null);
        return results;
    }

    private static void ParseInto(ContainerProbeResult result, IEnumerable<string> lines, out List<string> noise)
    {
        noise = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line.StartsWith("SESSION\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t', 3);
                if (parts.Length < 3 || (parts[1] != "0" && parts[1] != "1")) continue;
                if (parts[1] == "0")
                {
                    result.StaleSessionFiles++;
                    continue;
                }
                var session = ParseSession(parts[2]);
                if (session != null) result.Sessions.Add(session);
                continue;
            }

            if (line.StartsWith("TMUX\t", StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                if (parts.Length < 5 || parts[1].Length == 0) continue;
                result.Tmux.Add(new TmuxSessionInfo
                {
                    Name = parts[1],
                    Windows = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ? w : 0,
                    CreatedAt = long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? DateTimeOffset.FromUnixTimeSeconds(c) : null,
                    Attached = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) && a > 0,
                });
                continue;
            }

            noise.Add(line);
        }
    }

    private static ClaudeSessionInfo? ParseSession(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new ClaudeSessionInfo
            {
                SessionId = GetString(root, "sessionId"),
                Pid = GetLong(root, "pid"),
                Name = GetString(root, "name"),
                Status = GetString(root, "status"),
                Kind = GetString(root, "kind"),
                Cwd = GetString(root, "cwd"),
                StartedAt = GetEpochMs(root, "startedAt"),
                UpdatedAt = GetEpochMs(root, "updatedAt"),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static long? GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static DateTimeOffset? GetEpochMs(JsonElement root, string name)
    {
        var ms = GetLong(root, name);
        return ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);
    }
}
