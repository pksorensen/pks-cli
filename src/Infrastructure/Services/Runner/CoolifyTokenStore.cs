using System.Collections.Concurrent;

namespace PKS.Infrastructure.Services.Runner;

public enum CoolifyResolutionOutcome
{
    /// <summary>Exactly one application answers the request.</summary>
    Resolved,

    /// <summary>Nothing this job registered answers the request.</summary>
    NotFound,

    /// <summary>
    /// Several applications answer the request equally well and the instance roles did not break
    /// the tie. Deploying any of them would be a guess, so the caller must refuse instead.
    /// </summary>
    Ambiguous
}

/// <summary>
/// The answer to "which application is this job asking for" — with room to say "more than one",
/// which a bare <c>CoolifyAppMatch?</c> cannot express. That gap is what let a job that matched two
/// applications deploy to whichever one the registration order happened to put first.
/// </summary>
public class CoolifyAppResolution
{
    public CoolifyResolutionOutcome Outcome { get; private init; }

    /// <summary>The application, set only when <see cref="Outcome"/> is Resolved.</summary>
    public CoolifyAppMatch? App { get; private init; }

    /// <summary>
    /// Every application that matched. Populated when the answer was ambiguous, so the caller can
    /// name them all in its refusal rather than report a bare conflict.
    /// </summary>
    public List<CoolifyAppMatch> Candidates { get; private init; } = new();

    /// <summary>
    /// How a tie was broken, when one was — worth logging, because a deploy that silently went to
    /// one of two boxes is exactly the event that needs a trace afterwards.
    /// </summary>
    public string? TieBreak { get; private init; }

    public bool IsResolved => Outcome == CoolifyResolutionOutcome.Resolved;
    public bool IsAmbiguous => Outcome == CoolifyResolutionOutcome.Ambiguous;

    public static CoolifyAppResolution NotFound() => new() { Outcome = CoolifyResolutionOutcome.NotFound };

    public static CoolifyAppResolution Resolved(CoolifyAppMatch app, string? tieBreak = null) => new()
    {
        Outcome = CoolifyResolutionOutcome.Resolved,
        App = app,
        Candidates = new List<CoolifyAppMatch> { app },
        TieBreak = tieBreak
    };

    public static CoolifyAppResolution Ambiguous(IEnumerable<CoolifyAppMatch> candidates) => new()
    {
        Outcome = CoolifyResolutionOutcome.Ambiguous,
        Candidates = candidates.ToList()
    };
}

public interface ICoolifyTokenStore
{
    void Register(string jobId, CoolifyAppMatch appMatch);
    void RegisterAll(string jobId, IEnumerable<CoolifyAppMatch> apps);
    CoolifyAppMatch? GetByJobId(string jobId);
    CoolifyAppMatch? GetByJobIdAndEnvironment(string jobId, string environment);
    CoolifyAppMatch? GetByJobIdAndApp(string jobId, string app, string? environment = null);
    CoolifyAppMatch? GetByAppUuid(string appUuid);
    List<CoolifyAppMatch> GetAllByJobId(string jobId);
    void Remove(string jobId);

    /// <summary>Resolve by job alone — the fallback when the caller named neither app nor environment.</summary>
    CoolifyAppResolution ResolveByJobId(string jobId);

    /// <summary>Resolve by job and Coolify environment name.</summary>
    CoolifyAppResolution ResolveByJobIdAndEnvironment(string jobId, string environment);

    /// <summary>Resolve by the application the workflow named outright, optionally narrowed by environment.</summary>
    CoolifyAppResolution ResolveByJobIdAndApp(string jobId, string app, string? environment = null);
}

public class CoolifyTokenStore : ICoolifyTokenStore
{
    private readonly ConcurrentDictionary<string, List<CoolifyAppMatch>> _store = new();

    public void Register(string jobId, CoolifyAppMatch appMatch)
    {
        _store[jobId] = new List<CoolifyAppMatch> { appMatch };
    }

    public void RegisterAll(string jobId, IEnumerable<CoolifyAppMatch> apps)
    {
        _store[jobId] = new List<CoolifyAppMatch>(apps);
    }

    public CoolifyAppMatch? GetByJobId(string jobId) => ResolveByJobId(jobId).App;

    public CoolifyAppMatch? GetByJobIdAndEnvironment(string jobId, string environment)
        => ResolveByJobIdAndEnvironment(jobId, environment).App;

    /// <summary>
    /// Resolve by the application the workflow named outright — <c>coolify-app:</c> on the
    /// deploy action, which arrives as <c>?app=</c>.
    ///
    /// This exists for repositories where matching on the git repository cannot reach the right
    /// answer. A coordination repository whose deployable lives in a submodule owns no Coolify
    /// application of its own; a repository with several applications in one environment can only
    /// be disambiguated by name. Both were previously left to <c>FirstOrDefault</c> over whatever
    /// order the Coolify API happened to return.
    ///
    /// <paramref name="app"/> matches either the uuid or the application name, exactly and
    /// case-insensitively. There is deliberately no fuzzy fallback and no "closest match": a
    /// workflow that names an application it cannot have gets a 404 listing what it *can* have,
    /// because the alternative — quietly deploying the neighbour — is the bug this was added for.
    ///
    /// Returns null both when nothing matched and when several did; callers that need to tell those
    /// apart — and they should, because the second is a refusal, not a miss — use
    /// <see cref="ResolveByJobIdAndApp"/>.
    /// </summary>
    public CoolifyAppMatch? GetByJobIdAndApp(string jobId, string app, string? environment = null)
        => ResolveByJobIdAndApp(jobId, app, environment).App;

    public CoolifyAppResolution ResolveByJobId(string jobId)
    {
        if (!_store.TryGetValue(jobId, out var list) || list.Count == 0)
            return CoolifyAppResolution.NotFound();

        return Pick(list);
    }

    public CoolifyAppResolution ResolveByJobIdAndEnvironment(string jobId, string environment)
    {
        if (!_store.TryGetValue(jobId, out var list) || list.Count == 0)
            return CoolifyAppResolution.NotFound();

        // Strict match only — no fallback to prevent accidental deployments to wrong environment
        return Pick(list
            .Where(a => string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    public CoolifyAppResolution ResolveByJobIdAndApp(string jobId, string app, string? environment = null)
    {
        if (string.IsNullOrWhiteSpace(app))
            return CoolifyAppResolution.NotFound();

        if (!_store.TryGetValue(jobId, out var list) || list.Count == 0)
            return CoolifyAppResolution.NotFound();

        var candidates = list.Where(a =>
            string.Equals(a.Uuid, app, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase)).ToList();

        // The environment narrows the named app; it never selects a different one. An app named
        // uniquely across environments stays resolvable without the caller knowing its environment.
        if (!string.IsNullOrEmpty(environment))
        {
            var narrowed = candidates
                .Where(a => string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (narrowed.Count > 0)
                candidates = narrowed;
        }

        return Pick(candidates);
    }

    public CoolifyAppMatch? GetByAppUuid(string appUuid)
    {
        foreach (var kvp in _store)
        {
            foreach (var app in kvp.Value)
            {
                if (app.Uuid == appUuid)
                    return app;
            }
        }
        return null;
    }

    public List<CoolifyAppMatch> GetAllByJobId(string jobId)
    {
        return _store.TryGetValue(jobId, out var list) ? new List<CoolifyAppMatch>(list) : new List<CoolifyAppMatch>();
    }

    public void Remove(string jobId)
    {
        _store.TryRemove(jobId, out _);
    }

    /// <summary>
    /// Turn the applications that matched into an answer — the one place that decides what a tie
    /// means.
    ///
    /// A single candidate wins outright, including one that lives on a standby instance: a standby
    /// is outranked, never excluded, so an application that exists only on the old box stays
    /// deployable. Several candidates are a tie, and the instance role is the only thing allowed to
    /// break it — if exactly one of them is on a primary instance, that is the answer and the skip
    /// is recorded. Anything still tied is <see cref="CoolifyResolutionOutcome.Ambiguous"/>: taking
    /// the first element here is how a deploy reaches the wrong box, and a refused deploy is
    /// recoverable in a way a deploy to the wrong host is not.
    /// </summary>
    private static CoolifyAppResolution Pick(List<CoolifyAppMatch> candidates)
    {
        if (candidates.Count == 0)
            return CoolifyAppResolution.NotFound();

        if (candidates.Count == 1)
            return CoolifyAppResolution.Resolved(candidates[0]);

        var primaries = candidates.Where(a => !a.IsStandby).ToList();
        if (primaries.Count == 1)
        {
            var skipped = string.Join(", ", candidates.Where(a => a.IsStandby).Select(a => $"{a.Uuid}@{a.InstanceUrl}"));
            return CoolifyAppResolution.Resolved(
                primaries[0],
                $"{candidates.Count} candidates; chose {primaries[0].Uuid}@{primaries[0].InstanceUrl} (primary), skipped standby: {skipped}");
        }

        return CoolifyAppResolution.Ambiguous(candidates);
    }
}
