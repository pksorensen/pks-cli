using System.Text.Json;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>Which runner population a container belongs to. See <see cref="RunnerReaperParsing.Classify"/>.</summary>
public enum RunnerContainerKind
{
    /// <summary>Not ours — no recognised label. The VS Code devcontainer lands here, and is never touched.</summary>
    Foreign,

    /// <summary>A per-job container spawned by the runner. Cleaned up automatically.</summary>
    Ephemeral,

    /// <summary>A long-lived named runner container. Persistent by design; only reaped on an explicit opt-in.</summary>
    Named,

    /// <summary>
    /// A devcontainer this CLI spawned, but not one a runner owns — <c>pks claude spawn</c> and
    /// <c>pks vibecast</c> land here. Identified by the <see cref="RunnerReaperParsing.LocalVolumeLabel"/>
    /// that every spawn writes, with none of the runner labels on top. Reported by the plan but only
    /// removed on an explicit opt-in, because an exited one may be a developer's session they intend
    /// to come back to.
    /// </summary>
    Unattributed,
}

/// <summary>One container as seen by the reaper, with the volumes it owns.</summary>
public record RunnerContainerInfo(
    string Id,
    string Name,
    bool IsRunning,
    RunnerContainerKind Kind,
    string DevcontainerId,
    IReadOnlyList<string> MountedVolumes);

/// <summary>What a reap run is allowed to touch.</summary>
public record ReapOptions
{
    /// <summary>Report only; perform no removals.</summary>
    public bool DryRun { get; init; }

    /// <summary>Also reap exited <see cref="RunnerContainerKind.Named"/> containers. Off by default:
    /// their persistence is deliberate, so the startup sweep must not silently change that contract.</summary>
    public bool IncludeNamed { get; init; }

    /// <summary>Also reap exited <see cref="RunnerContainerKind.Unattributed"/> devcontainers — ones
    /// this CLI spawned outside a runner. Off by default: they may be a developer's session.</summary>
    public bool IncludeUnattributed { get; init; }

    /// <summary>Also remove <c>claude-code-config-*</c> session transcripts, which Brain/ASF ingests.</summary>
    public bool IncludeTranscripts { get; init; }

    /// <summary>Also remove dangling <c>devcontainer-*</c> workspace volumes, which can hold unpushed
    /// work. Never enabled by the startup sweep.</summary>
    public bool IncludeWorkspaces { get; init; }
}

/// <summary>What a reap run intends to do, resolved before anything is removed.</summary>
public record ReapPlan
{
    public IReadOnlyList<RunnerContainerInfo> Containers { get; init; } = Array.Empty<RunnerContainerInfo>();

    /// <summary>Volumes owned by the containers in <see cref="Containers"/>.</summary>
    public IReadOnlyList<string> AttachedVolumes { get; init; } = Array.Empty<string>();

    /// <summary>Dangling volumes in one of our families whose container is already gone — the case
    /// that produced 319 GB of orphaned <c>dind-var-lib-docker-*</c> on the si14agents host.</summary>
    public IReadOnlyList<string> OrphanVolumes { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Containers.Count == 0 && AttachedVolumes.Count == 0 && OrphanVolumes.Count == 0;
}

/// <summary>What a reap run actually did.</summary>
public record ReapResult
{
    public int ContainersRemoved { get; init; }
    public int VolumesRemoved { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure parsing and classification for <see cref="IRunnerReaper"/>. Kept separate from the service so
/// the discovery rules — which labels mark our containers, which volumes we own — are unit-testable
/// against captured <c>docker inspect</c> output rather than a live daemon.
/// </summary>
public static class RunnerReaperParsing
{
    /// <summary>The label the agentics.dk runner surface writes on warm containers.</summary>
    public const string FingerprintLabel = "pks.agentics.fingerprint";

    /// <summary>The id-label the GitHub named-runner path writes. Its <i>value</i> is what separates a
    /// persistent named runner from an ephemeral job container, which also carries the key but leaves
    /// it empty.</summary>
    public const string NamedLabel = "pks.runner.name";

    /// <summary>
    /// The id-label <c>DevcontainerSpawnerService</c> puts on <b>every</b> devcontainer it creates,
    /// runner-owned or not. It is the only key that covers the runner path whose <c>IdLabels</c> are
    /// null, which otherwise carries no pks marking at all.
    ///
    /// <para>This is why unifying discovery needed no new label. Adding one looked obvious and would
    /// have been a mistake: these are <c>--id-label</c> values, and the devcontainer CLI both hashes
    /// them into <c>${devcontainerId}</c> and matches on them to find a container to reuse. A new key
    /// would therefore change every existing container's id, break warm reuse on the agentics path,
    /// and orphan the very containers it was added to track.</para>
    ///
    /// <para>The dot matters. VS Code's own devcontainers are labelled <c>devcontainer.config_file</c>
    /// and <c>devcontainer.local_folder</c> (underscore); the dotted <c>local.volume</c> form is ours.
    /// Verified on the si14agents host 2026-08-24: 26 containers matched this filter and the VS Code
    /// devcontainer <c>vigorous_sanderson</c> — 171.6 GB, the single largest container on the box —
    /// was not one of them.</para>
    /// </summary>
    public const string LocalVolumeLabel = "devcontainer.local.volume";

    /// <summary>
    /// Every label key the reaper searches on. Docker ANDs multiple <c>--filter label=</c> arguments,
    /// so discovery runs one <c>docker ps</c> per key and unions the results.
    ///
    /// <para>The two runner keys are disjoint populations, not synonyms — measured on the si14agents
    /// host 2026-08-24: 10 containers carried the fingerprint label, 10 carried the named label, and
    /// no container carried both. A reaper filtering on only one of them, which is what shipped, was
    /// blind to half the fleet. <see cref="LocalVolumeLabel"/> covered all 25.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DiscoveryLabels = new[]
    {
        FingerprintLabel,
        NamedLabel,
        LocalVolumeLabel,
    };

    /// <summary>
    /// <c>docker inspect</c> format string. Labels come last so a <c>|</c> inside a label value cannot
    /// shift a field, and the template contains no inner double quotes so it can be quoted whole.
    ///
    /// <para>It does contain a space, in <c>{{json .Config.Labels}}</c>, and that space is a trap:
    /// <see cref="ProcessRunner"/> assigns the argument string to <c>ProcessStartInfo.Arguments</c>,
    /// which .NET splits on whitespace, so an unquoted template reaches docker as two arguments and
    /// dies with <c>template parsing error: unclosed action</c> — exit 64, empty stdout, and a reaper
    /// that silently finds no containers at all. Always build the argument string with
    /// <see cref="InspectArguments"/> rather than interpolating this constant directly.</para>
    /// </summary>
    public const string InspectFormat =
        "{{.Id}}|{{.State.Running}}|{{.Name}}|{{range .Mounts}}{{.Name}};{{end}}|{{json .Config.Labels}}";

    /// <summary>
    /// Builds the full <c>docker inspect</c> argument string for <paramref name="containerIds"/>,
    /// with <see cref="InspectFormat"/> quoted so it survives .NET argument splitting.
    /// </summary>
    public static string InspectArguments(params string[] containerIds) =>
        $"inspect --format \"{InspectFormat}\" {string.Join(' ', containerIds)}";

    /// <inheritdoc cref="InspectArguments(string[])"/>
    public static string InspectArguments(IEnumerable<string> containerIds) =>
        InspectArguments(containerIds.ToArray());

    /// <summary>
    /// Parses one line of <see cref="InspectFormat"/> output. Returns null for blank or malformed
    /// lines — a garbled line must drop the container from the plan, never fall through to a removal
    /// with half-parsed fields.
    /// </summary>
    public static RunnerContainerInfo? ParseInspectLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var parts = line.Split('|', 5);
        if (parts.Length < 5)
        {
            return null;
        }

        var id = parts[0].Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var isRunning = bool.TryParse(parts[1].Trim(), out var running) && running;
        var name = parts[2].Trim().TrimStart('/');

        var volumes = parts[3]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var labels = ParseLabels(parts[4]);

        return new RunnerContainerInfo(
            id,
            name,
            isRunning,
            Classify(labels),
            DevcontainerVolumes.DevcontainerIdFromMounts(volumes),
            volumes);
    }

    private static Dictionary<string, string> ParseLabels(string json)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = json.Trim();
        if (trimmed.Length == 0 || trimmed == "null")
        {
            return labels;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return labels;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                labels[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();
            }
        }
        catch (JsonException)
        {
            // A container whose labels we cannot read is treated as unlabelled, i.e. Foreign.
        }

        return labels;
    }

    /// <summary>
    /// Decides which population a container belongs to from its labels.
    ///
    /// <para><see cref="NamedLabel"/> with a non-empty value means the named path. The value matters:
    /// ephemeral spawns can carry the key with an empty value, and an empty-valued key is what made
    /// four ephemeral containers look persistent on the si14agents host.</para>
    /// </summary>
    public static RunnerContainerKind Classify(IReadOnlyDictionary<string, string> labels)
    {
        if (labels is null)
        {
            return RunnerContainerKind.Foreign;
        }

        if (labels.TryGetValue(NamedLabel, out var named) && !string.IsNullOrWhiteSpace(named))
        {
            return RunnerContainerKind.Named;
        }

        if (labels.ContainsKey(FingerprintLabel) || labels.ContainsKey(NamedLabel))
        {
            return RunnerContainerKind.Ephemeral;
        }

        if (labels.ContainsKey(LocalVolumeLabel))
        {
            return RunnerContainerKind.Unattributed;
        }

        return RunnerContainerKind.Foreign;
    }

    /// <summary>
    /// Decides whether a discovered container is in scope for reaping. Running containers are always
    /// spared — the reaper runs at <c>runner start</c>, concurrently with other runners on the same
    /// host — and so is anything <see cref="RunnerContainerKind.Foreign"/>, which is what keeps a
    /// developer's VS Code devcontainer out of the plan.
    /// </summary>
    public static bool ShouldReap(RunnerContainerInfo container, ReapOptions options)
    {
        if (container.IsRunning)
        {
            return false;
        }

        return container.Kind switch
        {
            RunnerContainerKind.Ephemeral => true,
            RunnerContainerKind.Named => options.IncludeNamed,
            RunnerContainerKind.Unattributed => options.IncludeUnattributed,
            _ => false,
        };
    }

    /// <summary>
    /// Filters a container's mounts down to the volumes the reaper owns and may remove. The workspace
    /// volume is included only under <see cref="ReapOptions.IncludeWorkspaces"/>, and transcripts only
    /// under <see cref="ReapOptions.IncludeTranscripts"/>.
    /// </summary>
    public static IReadOnlyList<string> ReapableVolumes(RunnerContainerInfo container, ReapOptions options)
    {
        var result = new List<string>();

        foreach (var volume in container.MountedVolumes)
        {
            if (DevcontainerVolumes.IsTranscript(volume))
            {
                if (options.IncludeTranscripts)
                {
                    result.Add(volume);
                }
                continue;
            }

            if (DevcontainerVolumes.IsReapable(volume))
            {
                result.Add(volume);
                continue;
            }

            if (DevcontainerVolumes.IsWorkspace(volume) && options.IncludeWorkspaces)
            {
                result.Add(volume);
            }
        }

        // The container may not have mounted every sibling its id implies (config drift between the
        // image that created it and the one running now), so add the derived names too. Removing a
        // volume that does not exist is a no-op the caller tolerates.
        if (!string.IsNullOrEmpty(container.DevcontainerId))
        {
            foreach (var sibling in DevcontainerVolumes.SiblingsFor(container.DevcontainerId, options.IncludeTranscripts))
            {
                if (!result.Contains(sibling, StringComparer.Ordinal))
                {
                    result.Add(sibling);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// From a <c>docker volume ls -qf dangling=true</c> listing, the volumes in one of our families
    /// that no container owns any more. Workspace volumes are excluded unless explicitly opted in,
    /// because that is the family that can hold unpushed work.
    /// </summary>
    public static IReadOnlyList<string> SelectOrphanVolumes(IEnumerable<string> danglingVolumes, ReapOptions options)
    {
        var result = new List<string>();

        foreach (var raw in danglingVolumes ?? Array.Empty<string>())
        {
            var volume = raw?.Trim();
            if (string.IsNullOrEmpty(volume))
            {
                continue;
            }

            if (DevcontainerVolumes.IsTranscript(volume))
            {
                if (options.IncludeTranscripts)
                {
                    result.Add(volume);
                }
                continue;
            }

            if (DevcontainerVolumes.IsReapable(volume))
            {
                result.Add(volume);
                continue;
            }

            if (DevcontainerVolumes.IsWorkspace(volume) && options.IncludeWorkspaces)
            {
                result.Add(volume);
            }
        }

        return result;
    }
}
