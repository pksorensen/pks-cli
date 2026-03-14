using System.Collections.Concurrent;

namespace PKS.Infrastructure.Services.Runner;

public interface ICoolifyTokenStore
{
    void Register(string jobId, CoolifyAppMatch appMatch);
    void RegisterAll(string jobId, IEnumerable<CoolifyAppMatch> apps);
    CoolifyAppMatch? GetByJobId(string jobId);
    CoolifyAppMatch? GetByJobIdAndEnvironment(string jobId, string environment);
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
