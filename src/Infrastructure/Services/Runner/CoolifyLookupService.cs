using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

public class CoolifyAppMatch
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Fqdn { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string InstanceUrl { get; set; } = "";
    public string Token { get; set; } = "";
}

public interface ICoolifyLookupService
{
    /// <summary>
    /// Find the Coolify application matching a GitHub repository and branch.
    /// When <paramref name="environment"/> is provided, prefer applications whose
    /// Coolify environment name matches the GitHub environment name.
    /// Returns null if no match found, throws if multiple matches found.
    /// </summary>
    Task<CoolifyAppMatch?> FindAppAsync(string owner, string repo, string branch, string? environment = null);

    /// <summary>
    /// Find ALL Coolify applications matching a GitHub repository and branch,
    /// across all environments, without filtering or disambiguation.
    /// </summary>
    Task<List<CoolifyAppMatch>> FindAllAppsAsync(string owner, string repo, string branch);
}

public class CoolifyLookupService : ICoolifyLookupService
{
    private readonly ICoolifyConfigurationService _configService;
    private readonly ICoolifyApiService _apiService;
    private readonly ILogger<CoolifyLookupService> _logger;

    public CoolifyLookupService(
        ICoolifyConfigurationService configService,
        ICoolifyApiService apiService,
        ILogger<CoolifyLookupService> logger)
    {
        _configService = configService;
        _apiService = apiService;
        _logger = logger;
    }

    public async Task<CoolifyAppMatch?> FindAppAsync(string owner, string repo, string branch, string? environment = null)
    {
        var instances = await _configService.ListInstancesAsync();
        if (!instances.Any())
        {
            _logger.LogDebug("No Coolify instances registered");
            return null;
        }

        var fullRepo = $"{owner}/{repo}";
        var matches = new List<(CoolifyAppMatch Match, string? EnvironmentName)>();

        foreach (var instance in instances)
        {
            try
            {
                // Always use projects API to get environment context for each match
                await FindAppsViaProjectsAsync(instance, fullRepo, branch, environment, matches);

                // Fallback to flat list if projects API found nothing (backward compat)
                if (matches.Count == 0)
                {
                    await FindAppsViaFlatListAsync(instance, fullRepo, branch, matches);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query Coolify instance {Url}", instance.Url);
            }
        }

        // Filter by environment if specified
        if (!string.IsNullOrEmpty(environment) && matches.Count > 1)
        {
            var envFiltered = matches.Where(m =>
                string.Equals(m.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase)).ToList();
            if (envFiltered.Count > 0)
            {
                _logger.LogInformation("Filtered {Total} matches to {Filtered} by environment '{Env}'",
                    matches.Count, envFiltered.Count, environment);
                matches = envFiltered;
            }
            else
            {
                _logger.LogWarning("No matches for environment '{Env}', falling back to all {Count} matches",
                    environment, matches.Count);
            }
        }

        if (matches.Count == 0)
        {
            _logger.LogDebug("No Coolify app found for {Repo}@{Branch} (environment={Environment})",
                fullRepo, branch, environment ?? "(any)");
            return null;
        }

        // When environment is provided and we have multiple matches, prefer the environment-matched one
        if (matches.Count > 1 && !string.IsNullOrEmpty(environment))
        {
            var envMatches = matches
                .Where(m => string.Equals(m.EnvironmentName, environment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (envMatches.Count == 1)
            {
                _logger.LogInformation(
                    "Resolved ambiguity: selected app {Name} in environment '{Env}' (out of {Total} matches)",
                    envMatches[0].Match.Name, environment, matches.Count);
                return envMatches[0].Match;
            }

            if (envMatches.Count > 1)
            {
                _logger.LogError(
                    "Multiple Coolify apps match {Repo}@{Branch} in environment '{Env}': {Apps}",
                    fullRepo, branch, environment,
                    string.Join(", ", envMatches.Select(m => $"{m.Match.Name} ({m.Match.Uuid})")));
                throw new InvalidOperationException(
                    $"Ambiguous: {envMatches.Count} Coolify apps match {fullRepo}@{branch} in environment '{environment}'. " +
                    "Ensure only one application points at this repository and branch per environment.");
            }

            // No exact environment match found among multiple matches — fall through to original error
        }

        if (matches.Count > 1)
        {
            // No environment from GitHub — try defaulting to "production"
            var prodMatches = matches
                .Where(m => string.Equals(m.EnvironmentName, "production", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (prodMatches.Count == 1)
            {
                _logger.LogInformation(
                    "Multiple matches for {Repo}@{Branch}, defaulting to production environment: {Name} ({Uuid})",
                    fullRepo, branch, prodMatches[0].Match.Name, prodMatches[0].Match.Uuid);
                return prodMatches[0].Match;
            }

            // Still ambiguous — warn and pick the first one instead of throwing
            _logger.LogWarning(
                "Multiple Coolify apps match {Repo}@{Branch}: {Apps}. Using first match. " +
                "Use GitHub Actions 'environment:' to disambiguate.",
                fullRepo, branch,
                string.Join(", ", matches.Select(m => $"{m.Match.Name} ({m.Match.Uuid}) env={m.EnvironmentName}")));
            return matches[0].Match;
        }

        return matches[0].Match;
    }

    public async Task<List<CoolifyAppMatch>> FindAllAppsAsync(string owner, string repo, string branch)
    {
        var instances = await _configService.ListInstancesAsync();
        if (!instances.Any())
            return new List<CoolifyAppMatch>();

        var fullRepo = $"{owner}/{repo}";
        var matches = new List<(CoolifyAppMatch Match, string? EnvironmentName)>();

        foreach (var instance in instances)
        {
            try
            {
                await FindAppsViaProjectsAsync(instance, fullRepo, branch, null, matches);
                if (matches.Count == 0)
                    await FindAppsViaFlatListAsync(instance, fullRepo, branch, matches);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query Coolify instance {Url}", instance.Url);
            }
        }

        // Set EnvironmentName on each match
        foreach (var (match, envName) in matches)
        {
            match.EnvironmentName = envName ?? "";
        }

        return matches.Select(m => m.Match).ToList();
    }

    /// <summary>
    /// Environment-aware lookup using the projects API to resolve applications within
    /// Coolify environments that match the GitHub environment name.
    /// </summary>
    private async Task FindAppsViaProjectsAsync(
        CoolifyInstance instance,
        string fullRepo,
        string branch,
        string environment,
        List<(CoolifyAppMatch Match, string? EnvironmentName)> matches)
    {
        var projects = await _apiService.GetProjectsWithResourcesAsync(instance);

        // Also fetch the flat applications list to get git_repository/git_branch details
        // since the projects API resources don't include git info
        var apps = await FetchApplicationsAsync(instance);
        var appsByUuid = new Dictionary<string, JsonElement>();
        foreach (var app in apps)
        {
            var uuid = app.GetProperty("uuid").GetString() ?? "";
            if (!string.IsNullOrEmpty(uuid))
                appsByUuid[uuid] = app;
        }

        // Now walk projects -> environments -> applications to find matches with environment context
        foreach (var project in projects)
        {
            try
            {
                // Fetch project detail to get environments with their resources
                var rawJson = await _apiService.GetRawProjectsJsonAsync(instance, project.Uuid);
                var doc = JsonDocument.Parse(rawJson);

                if (doc.RootElement.TryGetProperty("environments", out var envsProp) &&
                    envsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var env in envsProp.EnumerateArray())
                    {
                        var envName = env.TryGetProperty("name", out var envNameProp)
                            ? envNameProp.GetString() ?? "" : "";

                        // Extract application UUIDs from this environment
                        if (!env.TryGetProperty("applications", out var envApps) ||
                            envApps.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var envApp in envApps.EnumerateArray())
                        {
                            var appUuid = envApp.TryGetProperty("uuid", out var uuidProp)
                                ? uuidProp.GetString() ?? "" : "";

                            if (string.IsNullOrEmpty(appUuid) || !appsByUuid.TryGetValue(appUuid, out var fullApp))
                                continue;

                            var gitRepo = fullApp.GetProperty("git_repository").GetString() ?? "";
                            var gitBranch = fullApp.GetProperty("git_branch").GetString() ?? "";

                            var repoMatch = gitRepo.Contains(fullRepo, StringComparison.OrdinalIgnoreCase);
                            var branchMatch = string.Equals(gitBranch, branch, StringComparison.OrdinalIgnoreCase);

                            if (repoMatch && branchMatch)
                            {
                                var match = BuildAppMatch(fullApp, instance, appUuid);
                                matches.Add((match, envName));

                                _logger.LogInformation(
                                    "Coolify match: {Name} (uuid={Uuid}) in environment '{Env}' on {Instance} fqdn={Fqdn}",
                                    match.Name, match.Uuid, envName, instance.Url, match.Fqdn);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch environments for project {Uuid}", project.Uuid);
            }
        }
    }

    /// <summary>
    /// Original flat lookup using GET /api/v1/applications (no environment context).
    /// </summary>
    private async Task FindAppsViaFlatListAsync(
        CoolifyInstance instance,
        string fullRepo,
        string branch,
        List<(CoolifyAppMatch Match, string? EnvironmentName)> matches)
    {
        var apps = await FetchApplicationsAsync(instance);

        foreach (var app in apps)
        {
            var gitRepo = app.GetProperty("git_repository").GetString() ?? "";
            var gitBranch = app.GetProperty("git_branch").GetString() ?? "";
            var uuid = app.GetProperty("uuid").GetString() ?? "";

            var repoMatch = gitRepo.Contains(fullRepo, StringComparison.OrdinalIgnoreCase);
            var branchMatch = string.Equals(gitBranch, branch, StringComparison.OrdinalIgnoreCase);

            if (repoMatch && branchMatch)
            {
                var match = BuildAppMatch(app, instance, uuid);
                matches.Add((match, null));

                _logger.LogInformation(
                    "Coolify match: {Name} (uuid={Uuid}) on {Instance} fqdn={Fqdn}",
                    match.Name, match.Uuid, instance.Url, match.Fqdn);
            }
        }
    }

    private static CoolifyAppMatch BuildAppMatch(JsonElement app, CoolifyInstance instance, string uuid)
    {
        var fqdn = "";
        if (app.TryGetProperty("fqdn", out var fqdnProp))
            fqdn = fqdnProp.GetString() ?? "";

        var name = "";
        if (app.TryGetProperty("name", out var nameProp))
            name = nameProp.GetString() ?? "";

        return new CoolifyAppMatch
        {
            Uuid = uuid,
            Name = name,
            Fqdn = fqdn,
            WebhookUrl = $"{instance.Url}/api/v1/deploy?uuid={uuid}&force=false",
            InstanceUrl = instance.Url,
            Token = instance.Token
        };
    }

    private async Task<List<JsonElement>> FetchApplicationsAsync(CoolifyInstance instance)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);

        var response = await http.GetAsync($"{instance.Url}/api/v1/applications");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        // API may return array directly or wrapped in a data property
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().ToList();
        }
        else if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToList();
        }

        return new List<JsonElement>();
    }
}
