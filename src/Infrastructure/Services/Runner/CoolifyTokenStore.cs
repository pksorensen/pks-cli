using System.Collections.Concurrent;

namespace PKS.Infrastructure.Services.Runner;

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

    public CoolifyAppMatch? GetByJobId(string jobId)
    {
        return _store.TryGetValue(jobId, out var list) && list.Count > 0 ? list[0] : null;
    }

    public CoolifyAppMatch? GetByJobIdAndEnvironment(string jobId, string environment)
    {
        if (!_store.TryGetValue(jobId, out var list) || list.Count == 0)
            return null;

        // Strict match only — no fallback to prevent accidental deployments to wrong environment
        return list.FirstOrDefault(a => string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase));
    }

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
    /// </summary>
    public CoolifyAppMatch? GetByJobIdAndApp(string jobId, string app, string? environment = null)
    {
        if (string.IsNullOrWhiteSpace(app))
            return null;

        if (!_store.TryGetValue(jobId, out var list) || list.Count == 0)
            return null;

        var candidates = list.Where(a =>
            string.Equals(a.Uuid, app, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.Name, app, StringComparison.OrdinalIgnoreCase));

        // The environment narrows the named app; it never selects a different one. An app named
        // uniquely across environments stays resolvable without the caller knowing its environment.
        if (!string.IsNullOrEmpty(environment))
        {
            var narrowed = candidates
                .Where(a => string.Equals(a.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (narrowed.Count > 0)
                return narrowed[0];
        }

        return candidates.FirstOrDefault();
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
}
