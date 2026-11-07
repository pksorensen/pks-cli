using System.Text;
using PKS.Infrastructure.Services.Agent.Setup;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <param name="MakeActive">
/// Also put this profile's tables in <c>config.toml</c>, so a bare <c>codex</c> gets it.
/// Off by default: it is the only way to have a default profile, and it is also the only way to
/// leak one profile's servers into every other <c>-p</c>, because Codex merges the base config
/// into all of them.
/// </param>
public sealed record CodexApplyRequest(
    string Origin,
    string SourceId,
    string? SourceDisplayName,
    string? Issuer,
    string? ClientId,
    AspProfile Profile,
    bool MakeActive);

public sealed record CodexApplyResult(string Namespace, IReadOnlyList<string> Notes);

/// <summary>
/// Writes profiles to disk and keeps <see cref="CodexProfileLedger"/> in step.
///
/// <para>
/// Every write is the same three edits (<see cref="CodexTomlEditor"/>): replace owned top-level
/// keys in place, remove owned tables by name, append the new ones. Nothing else in the file is
/// touched, because <c>~/.codex/config.toml</c> is co-owned by Codex Desktop, <c>codex plugin</c>,
/// <c>codex mcp</c> and the reader.
/// </para>
/// </summary>
public static class CodexProfileWriter
{
    /// <summary>
    /// <c>$CODEX_HOME</c>, or <c>~/.codex</c>. Honouring the variable is what lets the probe and
    /// the tests work on a throwaway directory rather than the reader's real config.
    /// </summary>
    public static string CodexHome()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CODEX_HOME");

        return !string.IsNullOrWhiteSpace(fromEnv)
            ? fromEnv
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public static string ConfigPath() => Path.Combine(CodexHome(), "config.toml");

    public static CodexApplyResult Apply(CodexApplyRequest request, CodexProfileLedger ledger)
    {
        var notes = new List<string>();
        var profile = request.Profile;

        Directory.CreateDirectory(CodexHome());

        var source = ledger.Sources.TryGetValue(request.Origin, out var existing) ? existing : new CodexLedgerSource();

        // Uniqueness is decided here, not taken from the document: see CodexLedgerSource.EffectiveId.
        var sourceId = source.EffectiveId ?? ResolveSourceId(request.SourceId, request.Origin, ledger);
        if (source.EffectiveId is null && sourceId != request.SourceId)
            notes.Add($"Another platform already uses '{request.SourceId}' here, so this one is '{sourceId}'.");

        var ns = CodexProfileApplier.NamespaceFor(sourceId, profile.Id);
        source.EffectiveId = sourceId;
        source.Id = request.SourceId;
        source.DisplayName = request.SourceDisplayName;
        source.Issuer = request.Issuer;
        source.ClientId = request.ClientId;
        source.LastConnected = DateTimeOffset.UtcNow;
        ledger.Sources[request.Origin] = source;

        var entry = source.Profiles.TryGetValue(profile.Id, out var prior) ? prior : new CodexLedgerProfile();
        var previousBaseTables = entry.BaseTables.ToList();
        var previousBaseKeys = entry.BaseKeys.ToList();

        entry.Id = profile.Id;
        entry.Namespace = ns;
        entry.DisplayName = profile.DisplayName;

        // ---- the layer file: ours alone, so it is written whole ----
        var layerToml = CodexProfileApplier.BuildLayerToml(profile, sourceId, out var layerTables);
        var layerPath = Path.Combine(CodexHome(), CodexProfileApplier.LayerFileName(ns));
        WriteUtf8NoBom(layerPath, layerToml);
        entry.LayerFile = CodexProfileApplier.LayerFileName(ns);
        entry.LayerTables = layerTables;
        notes.Add($"Wrote {entry.LayerFile}");

        // ---- config.toml: edited, never rebuilt ----
        var configPath = ConfigPath();
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var originalConfig = config;

        // Two repairs first, both of which cost the reader every profile if left in place.
        config = CodexTomlEditor.RemoveLegacyProfileSelector(config, out var removedSelector);
        if (removedSelector)
            notes.Add("Removed a legacy `profile`/`[profiles.*]` setting — it makes `-p` fail for every profile.");

        config = CodexTomlEditor.RemoveLegacyManagedBlock(config, out var removedBlock);
        if (removedBlock)
            notes.Add("Removed the old `pks codex init` managed block; its provider is superseded.");

        // Marketplaces live here and nowhere else: `codex plugin` refuses `-p`, so a marketplace
        // in a layer file would never be read.
        config = CodexProfileApplier.RemoveOwned(config, previousBaseTables.Where(IsMarketplaceTable));
        var baseTables = new List<string>();
        foreach (var marketplace in profile.Marketplaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(marketplace.Url))
            {
                notes.Add($"Skipped marketplace '{marketplace.Name}': the platform published no URL.");
                continue;
            }

            var toml = CodexProfileApplier.BuildMarketplaceToml(marketplace, sourceId, out var table);
            config = CodexTomlEditor.RemoveTable(config, table);
            config = CodexTomlEditor.AppendTable(config, toml);
            baseTables.Add(table);
        }

        if ((profile.Marketplaces?.Count ?? 0) > 0)
            notes.Add($"{baseTables.Count} marketplace(s) in config.toml — `codex plugin` cannot read a profile layer.");

        // ---- optional: make it what a bare `codex` gets ----
        //
        // A profile that is ALREADY active stays active through its own refresh. Without the
        // second half of this condition, `connect` — which this command promotes as the update
        // path — silently empties config.toml for whoever had chosen a profile, and `--all` does
        // it on every run. The reader's choice is theirs to change, not ours to reset.
        var makeActive = request.MakeActive
            || string.Equals(ledger.Active, ns, StringComparison.OrdinalIgnoreCase);

        var baseKeys = new List<string>();
        if (makeActive)
        {
            // Take the previously active profile's tables out first, whoever it was.
            foreach (var (_, otherSource, otherProfile) in ledger.AllProfiles().ToList())
            {
                if (ReferenceEquals(otherProfile, entry)) continue;
                if (otherProfile.BaseTables.Count == 0 && otherProfile.BaseKeys.Count == 0) continue;

                config = CodexProfileApplier.RemoveOwned(config, otherProfile.BaseTables.Where(t => !IsMarketplaceTable(t)));
                otherProfile.BaseTables = otherProfile.BaseTables.Where(IsMarketplaceTable).ToList();
                otherProfile.BaseKeys = [];
                _ = otherSource;
            }

            foreach (var table in layerTables)
            {
                config = CodexTomlEditor.RemoveTable(config, table);
                config = CodexTomlEditor.AppendTable(config, ExtractTable(layerToml, table));
                baseTables.Add(table);
            }

            if (!string.IsNullOrEmpty(profile.Model?.Default))
            {
                config = CodexTomlEditor.SetTopLevelKey(config, "model", $"\"{profile.Model.Default}\"");
                baseKeys.Add("model");
            }

            if (profile.Provider is not null)
            {
                config = CodexTomlEditor.SetTopLevelKey(config, "model_provider", $"\"{ns}\"");
                baseKeys.Add("model_provider");
            }

            ledger.Active = ns;
            if (!request.MakeActive) notes.Add($"{ns} is still what a bare `codex` uses.");
            else notes.Add($"A bare `codex` now uses {ns}.");
        }
        else
        {
            // Not active: anything we previously put in config.toml for this profile comes out,
            // other than its marketplaces, which have to stay there to work at all.
            config = CodexProfileApplier.RemoveOwned(config, previousBaseTables.Where(t => !IsMarketplaceTable(t)));
            foreach (var key in previousBaseKeys) config = CodexTomlEditor.RemoveTopLevelKey(config, key);
            if (string.Equals(ledger.Active, ns, StringComparison.OrdinalIgnoreCase)) ledger.Active = null;
        }

        entry.BaseTables = baseTables;
        entry.BaseKeys = baseKeys;
        source.Profiles[profile.Id] = entry;

        if (!string.Equals(config, originalConfig, StringComparison.Ordinal))
        {
            BackUp(configPath, originalConfig);
            WriteUtf8NoBom(configPath, config);
            notes.Add("Updated ~/.codex/config.toml");
        }

        foreach (var repair in ReadBackAndRepair(configPath)) notes.Add(repair);

        return new CodexApplyResult(ns, notes);
    }

    /// <summary>
    /// Reads back what we just wrote and repairs a config that Codex would refuse to load.
    ///
    /// <para>
    /// Checking before writing is not enough on its own: a check reads the file as it was, and
    /// this file has other authors — Codex Desktop, <c>codex plugin</c>, <c>codex mcp</c> and the
    /// reader. The one fatal shape is a top-level <c>model_provider</c> naming a table that is
    /// not present: it fails the whole config load on every invocation, not one profile, with
    /// "Model provider `x` not found". Somebody whose Codex will not start wants it started, so
    /// this repairs rather than reports — and says what it did, because silently deleting a key
    /// somebody else wrote would be worse than the error.
    /// </para>
    /// </summary>
    internal static IEnumerable<string> ReadBackAndRepair(string configPath)
    {
        if (!File.Exists(configPath)) yield break;

        string content;
        try { content = File.ReadAllText(configPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        var provider = CodexTomlEditor.GetTopLevelKey(content, "model_provider");
        if (provider is null) yield break;

        var name = provider.Trim().Trim('"', '\'');
        if (name.Length == 0 || CodexTomlEditor.HasTable(content, $"model_providers.{name}")) yield break;

        var repaired = CodexTomlEditor.RemoveTopLevelKey(content, "model_provider");
        if (string.Equals(repaired, content, StringComparison.Ordinal)) yield break;

        BackUp(configPath, content);
        WriteUtf8NoBom(configPath, repaired);

        yield return
            $"Removed `model_provider = \"{name}\"`: no [model_providers.{name}] table exists, " +
            "and Codex refuses to start at all with that combination.";
    }

    /// <summary>
    /// Picks a source slug that is free on this machine, preferring the one the platform claims.
    /// </summary>
    private static string ResolveSourceId(string claimed, string origin, CodexProfileLedger ledger)
    {
        var taken = ledger.Sources
            .Where(kv => !string.Equals(kv.Key, origin, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value.EffectiveId ?? kv.Value.Id)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(claimed)) return claimed;

        // Disambiguate by host, which is what the reader would use to tell the two apart, and
        // which is stable for the same deployment across runs.
        var host = Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;
        var slug = new string(host.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

        var candidate = $"{claimed}-{slug}";
        if (!taken.Contains(candidate)) return candidate;

        for (var n = 2; ; n++)
        {
            var numbered = $"{candidate}-{n}";
            if (!taken.Contains(numbered)) return numbered;
        }
    }

    /// <summary>Removes every profile of a source that the platform no longer publishes.</summary>
    public static IEnumerable<string> Prune(string origin, ISet<string> publishedIds, CodexProfileLedger ledger)
    {
        if (!ledger.Sources.TryGetValue(origin, out var source)) yield break;

        foreach (var id in source.Profiles.Keys.ToList())
        {
            if (publishedIds.Contains(id)) continue;

            var ns = source.Profiles[id].Namespace;
            Remove(ns, ledger);
            yield return ns;
        }
    }

    /// <summary>
    /// Takes one profile off this machine: its layer file, its tables, its ledger entry.
    /// Its credential's environment variable is left alone — removing a variable we may not have
    /// been the only writer of is not ours to do.
    /// </summary>
    public static bool Remove(string ns, CodexProfileLedger ledger)
    {
        foreach (var (origin, source, profile) in ledger.AllProfiles().ToList())
        {
            if (!string.Equals(profile.Namespace, ns, StringComparison.OrdinalIgnoreCase)) continue;

            var configPath = ConfigPath();
            if (File.Exists(configPath))
            {
                var original = File.ReadAllText(configPath);
                var updated = CodexProfileApplier.RemoveOwned(original, profile.BaseTables);
                foreach (var key in profile.BaseKeys) updated = CodexTomlEditor.RemoveTopLevelKey(updated, key);

                if (!string.Equals(updated, original, StringComparison.Ordinal))
                {
                    BackUp(configPath, original);
                    WriteUtf8NoBom(configPath, updated);
                }
            }

            if (profile.LayerFile is { Length: > 0 })
            {
                var layerPath = Path.Combine(CodexHome(), profile.LayerFile);
                if (File.Exists(layerPath)) File.Delete(layerPath);
            }

            source.Profiles.Remove(profile.Id);
            if (source.Profiles.Count == 0) ledger.Sources.Remove(origin);
            if (string.Equals(ledger.Active, ns, StringComparison.OrdinalIgnoreCase)) ledger.Active = null;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Makes a connected profile what a bare <c>codex</c> gets, with no network.
    /// <paramref name="ns"/> null clears it, which is the clean state for <c>-p</c> switching.
    /// </summary>
    public static (bool Ok, string Message) Use(string? ns, CodexProfileLedger ledger)
    {
        var configPath = ConfigPath();
        var original = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var config = original;

        // Everything any profile put in the base comes out, except marketplaces, which cannot
        // live anywhere else.
        foreach (var (_, _, profile) in ledger.AllProfiles().ToList())
        {
            config = CodexProfileApplier.RemoveOwned(config, profile.BaseTables.Where(t => !IsMarketplaceTable(t)));
            foreach (var key in profile.BaseKeys) config = CodexTomlEditor.RemoveTopLevelKey(config, key);
            profile.BaseTables = profile.BaseTables.Where(IsMarketplaceTable).ToList();
            profile.BaseKeys = [];
        }

        if (ns is null)
        {
            ledger.Active = null;
            Commit(configPath, original, config);

            return (true, "No profile is active. A bare `codex` uses only your own config; use `codex -p <name>`.");
        }

        var target = ledger.AllProfiles().FirstOrDefault(r =>
            string.Equals(r.Profile.Namespace, ns, StringComparison.OrdinalIgnoreCase));

        if (target.Profile is null)
            return (false, $"No connected profile called '{ns}'. `pks codex profiles` lists them.");

        var layerPath = Path.Combine(CodexHome(), target.Profile.LayerFile ?? string.Empty);
        if (!File.Exists(layerPath))
            return (false,
                $"'{ns}' has no layer file at {layerPath}. Re-run `pks codex connect {target.Origin}`.");

        var layerToml = File.ReadAllText(layerPath);
        var baseTables = target.Profile.BaseTables.Where(IsMarketplaceTable).ToList();

        foreach (var table in target.Profile.LayerTables)
        {
            config = CodexTomlEditor.RemoveTable(config, table);
            config = CodexTomlEditor.AppendTable(config, ExtractTable(layerToml, table));
            baseTables.Add(table);
        }

        var baseKeys = new List<string>();
        var model = CodexTomlEditor.GetTopLevelKey(layerToml, "model");
        if (model is not null)
        {
            config = CodexTomlEditor.SetTopLevelKey(config, "model", model);
            baseKeys.Add("model");
        }

        var provider = CodexTomlEditor.GetTopLevelKey(layerToml, "model_provider");
        if (provider is not null)
        {
            config = CodexTomlEditor.SetTopLevelKey(config, "model_provider", provider);
            baseKeys.Add("model_provider");
        }

        target.Profile.BaseTables = baseTables;
        target.Profile.BaseKeys = baseKeys;
        ledger.Active = ns;

        Commit(configPath, original, config);

        return (true,
            $"A bare `codex` now uses {ns}. Note its servers also appear under every other `-p`, " +
            "because Codex merges the base config into all of them — `pks codex use --none` undoes it.");
    }

    private static void Commit(string path, string original, string updated)
    {
        if (string.Equals(original, updated, StringComparison.Ordinal)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BackUp(path, original);
        WriteUtf8NoBom(path, updated);
    }

    /// <summary>
    /// A table and its body, lifted out of a file we wrote. Used to copy a layer table into
    /// <c>config.toml</c> so the two agree without rebuilding either.
    /// </summary>
    internal static string ExtractTable(string toml, string tableName)
    {
        var lines = toml.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder();
        var inside = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var isHeader = trimmed.Length >= 3 && trimmed[0] == '[' && trimmed[^1] == ']';

            if (isHeader)
            {
                var inner = trimmed[1..^1].Trim();
                var matches = inner.Equals(tableName, StringComparison.Ordinal)
                    || inner.StartsWith(tableName + ".", StringComparison.Ordinal);

                if (inside && !matches) break;
                inside = matches;
            }

            if (inside) sb.Append(line).Append('\n');
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static bool IsMarketplaceTable(string table) =>
        table.StartsWith("marketplaces.", StringComparison.Ordinal);

    /// <summary>
    /// UTF-8 with no byte-order mark, and <c>\n</c> endings.
    ///
    /// <para>
    /// The BOM is not cosmetic: a TOML parser reading a file that starts with one sees the mark as
    /// part of the first key's name, so the first setting silently does nothing. PowerShell 5's
    /// <c>Out-File</c> writes one by default, which is how Home learned this.
    /// </para>
    /// </summary>
    private static void WriteUtf8NoBom(string path, string content) =>
        File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Keeps a timestamped copy before the first change of a run.
    ///
    /// <para>
    /// Cheap insurance on a file the reader may have hand-tuned for months, and the thing that
    /// makes "it broke my config" recoverable rather than a reinstall.
    /// </para>
    /// </summary>
    private static void BackUp(string path, string original)
    {
        if (string.IsNullOrEmpty(original)) return;

        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            File.WriteAllText($"{path}.pks-{stamp}.bak", original);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A backup we cannot write is not a reason to refuse the edit the reader asked for.
        }
    }
}
