using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// ARM resource listings keyed by tenant rather than by a bearer token the caller had to obtain
/// first. The token comes from <see cref="IAzureTenantCredentialStore"/> for the management scope,
/// so a command asks for "the workspaces in subscription S of tenant T" and never holds a token.
/// </summary>
public interface IAzureArmDiscovery
{
    Task<List<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default);
    Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string tenantId, string subscriptionId, CancellationToken ct = default);
    Task<List<AppInsightsComponent>> ListAppInsightsResourcesAsync(string tenantId, string subscriptionId, CancellationToken ct = default);
    Task<List<StorageAccountInfo>> ListStorageAccountsAsync(string tenantId, string subscriptionId, CancellationToken ct = default);
    Task<List<CommunicationServiceResource>> ListCommunicationServicesAsync(string tenantId, string subscriptionId, CancellationToken ct = default);

    /// <summary>The phone numbers purchased on one Communication Services resource, from its data
    /// plane (<c>https://{endpoint}/phoneNumbers</c>). Needs an RBAC role on the resource — a 401/403
    /// here means "no role", not "sign in again".</summary>
    Task<List<AcsPhoneNumber>> ListAcsPhoneNumbersAsync(string tenantId, string endpoint, CancellationToken ct = default);
}

public sealed class AzureArmDiscovery : IAzureArmDiscovery
{
    public const string ManagementScope = "https://management.azure.com/.default";

    /// <summary>Entra scope for the Communication Services data plane (SMS, phone numbers). The
    /// double slash is Microsoft's, not a typo.</summary>
    public const string CommunicationScope = "https://communication.azure.com//.default";

    private readonly HttpClient _httpClient;
    private readonly IAzureTenantCredentialStore _tenants;

    public AzureArmDiscovery(HttpClient httpClient, IAzureTenantCredentialStore tenants)
    {
        _httpClient = httpClient;
        _tenants = tenants;
    }

    private Task<string> TokenAsync(string tenantId, CancellationToken ct)
        => _tenants.GetAccessTokenAsync(tenantId, ManagementScope, ct);

    public async Task<List<AzureSubscription>> ListSubscriptionsAsync(string tenantId, CancellationToken ct = default)
        => await AzureArmRequests.ListSubscriptionsAsync(_httpClient, await TokenAsync(tenantId, ct), ct);

    public async Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
        => await AzureArmRequests.ListLogAnalyticsWorkspacesAsync(_httpClient, await TokenAsync(tenantId, ct), subscriptionId, ct);

    public async Task<List<AppInsightsComponent>> ListAppInsightsResourcesAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
        => await AzureArmRequests.ListAppInsightsComponentsAsync(_httpClient, await TokenAsync(tenantId, ct), subscriptionId, ct);

    public async Task<List<StorageAccountInfo>> ListStorageAccountsAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
        => await AzureArmRequests.ListStorageAccountsAsync(_httpClient, await TokenAsync(tenantId, ct), subscriptionId, ct);

    public async Task<List<CommunicationServiceResource>> ListCommunicationServicesAsync(string tenantId, string subscriptionId, CancellationToken ct = default)
        => await AzureArmRequests.ListCommunicationServicesAsync(_httpClient, await TokenAsync(tenantId, ct), subscriptionId, ct);

    public async Task<List<AcsPhoneNumber>> ListAcsPhoneNumbersAsync(string tenantId, string endpoint, CancellationToken ct = default)
    {
        var token = await _tenants.GetAccessTokenAsync(tenantId, CommunicationScope, ct);
        return await AzureArmRequests.ListAcsPhoneNumbersAsync(_httpClient, token, endpoint, ct);
    }
}

/// <summary>
/// The ARM GET-and-deserialize calls, shared by <see cref="AzureArmDiscovery"/>,
/// <c>AzureFoundryAuthService</c> and <c>AzureFileShareProvider</c> so each endpoint, API version
/// and filter is written once.
/// </summary>
internal static class AzureArmRequests
{
    private const string ArmBase = "https://management.azure.com";

    public static async Task<List<AzureSubscription>> ListSubscriptionsAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var response = await GetAsync<AzureSubscriptionListResponse>(http, accessToken, $"{ArmBase}/subscriptions?api-version=2022-12-01", ct);
        return response?.Value ?? new List<AzureSubscription>();
    }

    public static async Task<List<CognitiveServicesAccount>> ListCognitiveServicesAccountsAsync(HttpClient http, string accessToken, string subscriptionId, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.CognitiveServices/accounts?api-version=2023-05-01";
        var response = await GetAsync<CognitiveServicesAccountListResponse>(http, accessToken, url, ct);
        return response?.Value ?? new List<CognitiveServicesAccount>();
    }

    public static async Task<List<AppInsightsComponent>> ListAppInsightsComponentsAsync(HttpClient http, string accessToken, string subscriptionId, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Insights/components?api-version=2020-02-02";
        var response = await GetAsync<AppInsightsComponentListResponse>(http, accessToken, url, ct);
        return response?.Value ?? new List<AppInsightsComponent>();
    }

    public static async Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(HttpClient http, string accessToken, string subscriptionId, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.OperationalInsights/workspaces?api-version=2022-10-01";
        var response = await GetAsync<LogAnalyticsWorkspaceListResponse>(http, accessToken, url, ct);
        return response?.Value ?? new List<LogAnalyticsWorkspace>();
    }

    public static async Task<List<FoundryDeployment>> ListDeploymentsAsync(HttpClient http, string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CognitiveServices/accounts/{accountName}/deployments?api-version=2023-05-01";
        var response = await GetAsync<FoundryDeploymentListResponse>(http, accessToken, url, ct);
        return response?.Value ?? new List<FoundryDeployment>();
    }

    /// <summary>Storage accounts that can hold file shares — BlobStorage-kind accounts cannot.</summary>
    public static async Task<List<StorageAccountInfo>> ListStorageAccountsAsync(HttpClient http, string accessToken, string subscriptionId, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Storage/storageAccounts?api-version=2023-01-01";
        var response = await GetAsync<StorageAccountListResponse>(http, accessToken, url, ct);
        return (response?.Value ?? new List<StorageAccountInfo>())
            .Where(a => !a.Kind.Equals("BlobStorage", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static async Task<List<AzureFileShareInfo>> ListFileSharesAsync(HttpClient http, string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/{accountName}/fileServices/default/shares?api-version=2023-01-01";
        var response = await GetAsync<AzureFileShareListResponse>(http, accessToken, url, ct);
        return response?.Value ?? new List<AzureFileShareInfo>();
    }

    public static async Task<List<CommunicationServiceResource>> ListCommunicationServicesAsync(HttpClient http, string accessToken, string subscriptionId, CancellationToken ct)
    {
        var url = $"{ArmBase}/subscriptions/{subscriptionId}/providers/Microsoft.Communication/communicationServices?api-version=2023-04-01";
        var all = new List<CommunicationServiceResource>();
        while (url != null)
        {
            var response = await GetAsync<CommunicationServiceListResponse>(http, accessToken, url, ct);
            if (response == null) break;
            all.AddRange(response.Value);
            url = string.IsNullOrWhiteSpace(response.NextLink) ? null : response.NextLink;
        }
        return all;
    }

    /// <summary>Data plane, not ARM: <paramref name="endpoint"/> is the resource's <c>hostName</c>
    /// and <paramref name="accessToken"/> must carry the <c>communication.azure.com</c> scope.</summary>
    public static async Task<List<AcsPhoneNumber>> ListAcsPhoneNumbersAsync(HttpClient http, string accessToken, string endpoint, CancellationToken ct)
    {
        var url = $"https://{endpoint.TrimEnd('/')}/phoneNumbers?api-version=2022-12-01";
        var all = new List<AcsPhoneNumber>();
        while (url != null)
        {
            var response = await GetAsync<AcsPhoneNumberListResponse>(http, accessToken, url, ct);
            if (response == null) break;
            all.AddRange(response.PhoneNumbers);
            url = string.IsNullOrWhiteSpace(response.NextLink) ? null : response.NextLink;
        }
        return all;
    }

    private static async Task<T?> GetAsync<T>(HttpClient http, string accessToken, string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        return JsonSerializer.Deserialize<T>(content);
    }
}
