using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

public class AzureFileShareAuthConfig
{
    public string ClientId { get; set; } = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public string AuthorizeUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    public string TokenUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";
    public string ManagementScope { get; set; } = "https://management.azure.com/.default";
    public string StorageScope { get; set; } = "https://storage.azure.com/.default";
    public string InitialScope { get; set; } = "https://management.azure.com/.default offline_access";
    public int CallbackTimeoutSeconds { get; set; } = 120;

    public string GetAuthorizeUrl(string tenantId) => string.Format(AuthorizeUrl, tenantId);
    public string GetTokenUrl(string tenantId) => string.Format(TokenUrl, tenantId);
}

public class FileShareTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

public class FileShareStoredCredentials
{
    public string TenantId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string SelectedSubscriptionId { get; set; } = string.Empty;
    public string SelectedSubscriptionName { get; set; } = string.Empty;
    public string SelectedStorageAccountName { get; set; } = string.Empty;
    public string SelectedStorageAccountResourceGroup { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}

public class StorageAccountInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;
}

public class StorageAccountListResponse
{
    [JsonPropertyName("value")]
    public List<StorageAccountInfo> Value { get; set; } = new();
}

public class AzureFileShareInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public AzureFileShareProperties Properties { get; set; } = new();
}

public class AzureFileShareProperties
{
    [JsonPropertyName("shareQuota")]
    public int ShareQuota { get; set; }

    [JsonPropertyName("enabledProtocols")]
    public string EnabledProtocols { get; set; } = "SMB";
}

public class AzureFileShareListResponse
{
    [JsonPropertyName("value")]
    public List<AzureFileShareInfo> Value { get; set; } = new();
}
