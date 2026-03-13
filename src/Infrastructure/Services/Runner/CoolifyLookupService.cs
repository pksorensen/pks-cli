using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

public class CoolifyAppMatch
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Fqdn { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string InstanceUrl { get; set; } = "";
    public string Token { get; set; } = "";
}

public interface ICoolifyLookupService
{
    /// <summary>
    /// Find the Coolify application matching a GitHub repository and branch.
    /// Returns null if no match found, throws if multiple matches found.
    /// </summary>
    Task<CoolifyAppMatch?> FindAppAsync(string owner, string repo, string branch);
}

public class CoolifyLookupService : ICoolifyLookupService
{
    private readonly ICoolifyConfigurationService _configService;
    private readonly ILogger<CoolifyLookupService> _logger;

    public CoolifyLookupService(
        ICoolifyConfigurationService configService,
        ILogger<CoolifyLookupService> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task<CoolifyAppMatch?> FindAppAsync(string owner, string repo, string branch)
    {
        var instances = await _configService.ListInstancesAsync();
        if (!instances.Any())
        {
            _logger.LogDebug("No Coolify instances registered");
            return null;
        }

        var fullRepo = $"{owner}/{repo}";
        var matches = new List<CoolifyAppMatch>();

        foreach (var instance in instances)
        {
            try
            {
                var apps = await FetchApplicationsAsync(instance);

                foreach (var app in apps)
                {
                    var gitRepo = app.GetProperty("git_repository").GetString() ?? "";
                    var gitBranch = app.GetProperty("git_branch").GetString() ?? "";
                    var uuid = app.GetProperty("uuid").GetString() ?? "";

                    // Match by repo (could be full URL or owner/repo format)
                    var repoMatch = gitRepo.Contains(fullRepo, StringComparison.OrdinalIgnoreCase);
                    var branchMatch = string.Equals(gitBranch, branch, StringComparison.OrdinalIgnoreCase);

                    if (repoMatch && branchMatch)
                    {
                        var fqdn = "";
                        if (app.TryGetProperty("fqdn", out var fqdnProp))
                            fqdn = fqdnProp.GetString() ?? "";

                        var name = "";
                        if (app.TryGetProperty("name", out var nameProp))
                            name = nameProp.GetString() ?? "";

                        matches.Add(new CoolifyAppMatch
                        {
                            Uuid = uuid,
                            Name = name,
                            Fqdn = fqdn,
                            WebhookUrl = $"{instance.Url}/api/v1/deploy?uuid={uuid}&force=false",
                            InstanceUrl = instance.Url,
                            Token = instance.Token
                        });

                        _logger.LogInformation(
                            "Coolify match: {Name} (uuid={Uuid}) on {Instance} fqdn={Fqdn}",
                            name, uuid, instance.Url, fqdn);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query Coolify instance {Url}", instance.Url);
            }
        }

        if (matches.Count == 0)
        {
            _logger.LogDebug("No Coolify app found for {Repo}@{Branch}", fullRepo, branch);
            return null;
        }

        if (matches.Count > 1)
        {
            _logger.LogError(
                "Multiple Coolify apps match {Repo}@{Branch}: {Apps}",
                fullRepo, branch,
                string.Join(", ", matches.Select(m => $"{m.Name} ({m.Uuid})")));
            throw new InvalidOperationException(
                $"Ambiguous: {matches.Count} Coolify apps match {fullRepo}@{branch}. " +
                "Ensure only one application points at this repository and branch.");
        }

        return matches[0];
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
