using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

public class AzureAuthConfig
{
    public string ClientId { get; set; } = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public string AuthorizeUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    public string TokenUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";
    public string ManagementScope { get; set; } = "https://management.azure.com/.default offline_access";
    public int CallbackTimeoutSeconds { get; set; } = 120;
    public string GetAuthorizeUrl(string tenantId) => string.Format(AuthorizeUrl, tenantId);
    public string GetTokenUrl(string tenantId) => string.Format(TokenUrl, tenantId);
}

public class AzureStoredCredentials
{
    public string TenantId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string SubscriptionName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}
