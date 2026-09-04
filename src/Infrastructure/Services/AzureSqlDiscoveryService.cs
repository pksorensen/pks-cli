using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services;

/// <summary>One Azure SQL logical server as ARM reports it.</summary>
public class AzureSqlServerRef
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string FullyQualifiedDomainName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>Resource group, pulled out of the ARM resource id.</summary>
    public string ResourceGroup
    {
        get
        {
            var parts = Id.Split('/');
            var index = Array.FindIndex(parts, p => string.Equals(p, "resourceGroups", StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : string.Empty;
        }
    }
}

/// <summary>
/// Lists the SQL servers and databases an account can see through ARM, so `pks sqlserver init` can
/// offer them instead of asking the user to type a host name from memory.
/// </summary>
public interface IAzureSqlDiscoveryService
{
    Task<List<AzureSqlServerRef>> ListServersAsync(string managementToken, string subscriptionId, string subscriptionName, CancellationToken cancellationToken = default);
    Task<List<string>> ListDatabasesAsync(string managementToken, string serverResourceId, CancellationToken cancellationToken = default);

    /// <summary>Opens the server to a single IP address. Returns null on success, the failure text otherwise.</summary>
    Task<string?> SetFirewallRuleAsync(string managementToken, string serverResourceId, string ruleName, string ipAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes a firewall rule by name. Returns null on success, the failure text otherwise.</summary>
    Task<string?> DeleteFirewallRuleAsync(string managementToken, string serverResourceId, string ruleName, CancellationToken cancellationToken = default);

    /// <summary>The address a firewall rule has to name — the one Azure sees, not the one on the NIC.</summary>
    Task<string?> DetectPublicIpAsync(CancellationToken cancellationToken = default);
}

public class AzureSqlDiscoveryService : IAzureSqlDiscoveryService
{
    private const string ApiVersion = "2021-11-01";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureSqlDiscoveryService> _logger;

    public AzureSqlDiscoveryService(HttpClient httpClient, ILogger<AzureSqlDiscoveryService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<AzureSqlServerRef>> ListServersAsync(string managementToken, string subscriptionId, string subscriptionName, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Sql/servers?api-version={ApiVersion}";
        var response = await GetAsync<ArmList<ArmSqlServer>>(url, managementToken, cancellationToken);

        return response?.Value
            .Select(server => new AzureSqlServerRef
            {
                Id = server.Id,
                Name = server.Name,
                Location = server.Location,
                FullyQualifiedDomainName = server.Properties?.FullyQualifiedDomainName ?? $"{server.Name}.database.windows.net",
                SubscriptionId = subscriptionId,
                SubscriptionName = subscriptionName,
            })
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<AzureSqlServerRef>();
    }

    public async Task<List<string>> ListDatabasesAsync(string managementToken, string serverResourceId, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com{serverResourceId}/databases?api-version={ApiVersion}";
        var response = await GetAsync<ArmList<ArmNamed>>(url, managementToken, cancellationToken);

        return response?.Value
            .Select(database => database.Name)
            // 'master' is the server's own system database and never what you meant to query.
            .Where(name => !string.Equals(name, "master", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();
    }

    public async Task<string?> SetFirewallRuleAsync(string managementToken, string serverResourceId, string ruleName, string ipAddress, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com{serverResourceId}/firewallRules/{Uri.EscapeDataString(ruleName)}?api-version={ApiVersion}";
        var body = JsonSerializer.Serialize(new
        {
            properties = new { startIpAddress = ipAddress, endIpAddress = ipAddress }
        });

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
            return null;

        _logger.LogDebug("Firewall rule write failed: {StatusCode} {Body}", response.StatusCode, content);
        return $"{(int)response.StatusCode} {response.ReasonPhrase}: {content}";
    }

    public async Task<string?> DeleteFirewallRuleAsync(string managementToken, string serverResourceId, string ruleName, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com{serverResourceId}/firewallRules/{Uri.EscapeDataString(ruleName)}?api-version={ApiVersion}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", managementToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
            return null;

        _logger.LogDebug("Firewall rule delete failed: {StatusCode} {Body}", response.StatusCode, content);
        return $"{(int)response.StatusCode} {response.ReasonPhrase}: {content}";
    }

    public async Task<string?> DetectPublicIpAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _httpClient.GetStringAsync("https://api.ipify.org", cancellationToken);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Public IP lookup failed");
            return null;
        }
    }

    private async Task<T?> GetAsync<T>(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // A subscription the account cannot read is normal when several are visible — say so in
            // the log and carry on with the ones that answered.
            _logger.LogDebug("ARM call failed: {StatusCode} {Url} {Body}", response.StatusCode, url, content);
            return default;
        }

        return JsonSerializer.Deserialize<T>(content);
    }

    private class ArmList<T>
    {
        [JsonPropertyName("value")]
        public List<T> Value { get; set; } = new();
    }

    private class ArmNamed
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private class ArmSqlServer : ArmNamed
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("properties")]
        public ArmSqlServerProperties? Properties { get; set; }
    }

    private class ArmSqlServerProperties
    {
        [JsonPropertyName("fullyQualifiedDomainName")]
        public string? FullyQualifiedDomainName { get; set; }
    }
}
