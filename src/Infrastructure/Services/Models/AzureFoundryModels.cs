using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Configuration for Azure AI Foundry OAuth2 authentication
/// </summary>
public class AzureFoundryAuthConfig
{
    /// <summary>
    /// Azure CLI well-known public client ID
    /// </summary>
    public string ClientId { get; set; } = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>
    /// Default tenant ID, overridden at runtime
    /// </summary>
    public string TenantId { get; set; } = "common";

    /// <summary>
    /// OAuth2 authorize URL template with tenant placeholder
    /// </summary>
    public string AuthorizeUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";

    /// <summary>
    /// OAuth2 token URL template with tenant placeholder
    /// </summary>
    public string TokenUrl { get; set; } = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";

    /// <summary>
    /// Azure Resource Management scope
    /// </summary>
    public string ManagementScope { get; set; } = "https://management.azure.com/.default";

    /// <summary>
    /// Azure Cognitive Services scope
    /// </summary>
    public string CognitiveScope { get; set; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Initial scope for first authentication including offline_access for refresh tokens
    /// </summary>
    public string InitialScope { get; set; } = "https://cognitiveservices.azure.com/.default offline_access";

    public int CallbackTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Gets the authorize URL for a specific tenant
    /// </summary>
    public string GetAuthorizeUrl(string tenantId) => string.Format(AuthorizeUrl, tenantId);

    /// <summary>
    /// Gets the token URL for a specific tenant
    /// </summary>
    public string GetTokenUrl(string tenantId) => string.Format(TokenUrl, tenantId);
}

/// <summary>
/// Token response from Azure AI Foundry OAuth2 token endpoint
/// </summary>
public class FoundryTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// Intermediate result from Azure AI Foundry auth flow
/// </summary>
public class FoundryAuthResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Persisted Azure AI Foundry credentials
/// </summary>
public class FoundryStoredCredentials
{
    public string TenantId { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string SelectedSubscriptionId { get; set; } = string.Empty;
    public string SelectedSubscriptionName { get; set; } = string.Empty;
    public string SelectedResourceEndpoint { get; set; } = string.Empty;
    public string SelectedResourceName { get; set; } = string.Empty;
    public string SelectedResourceGroup { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}

/// <summary>
/// Azure subscription from ARM API
/// </summary>
public class AzureSubscription
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Response from ARM subscription list API
/// </summary>
public class AzureSubscriptionListResponse
{
    [JsonPropertyName("value")]
    public List<AzureSubscription> Value { get; set; } = new();
}

/// <summary>
/// Azure Cognitive Services account from ARM API
/// </summary>
public class CognitiveServicesAccount
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public CognitiveServicesAccountProperties Properties { get; set; } = new();
}

/// <summary>
/// Properties of an Azure Cognitive Services account
/// </summary>
public class CognitiveServicesAccountProperties
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>
/// Response from ARM Cognitive Services account list API
/// </summary>
public class CognitiveServicesAccountListResponse
{
    [JsonPropertyName("value")]
    public List<CognitiveServicesAccount> Value { get; set; } = new();
}

/// <summary>
/// Azure AI Foundry model deployment
/// </summary>
public class FoundryDeployment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public FoundryDeploymentProperties Properties { get; set; } = new();
}

/// <summary>
/// Properties of an Azure AI Foundry deployment
/// </summary>
public class FoundryDeploymentProperties
{
    [JsonPropertyName("provisioningState")]
    public string ProvisioningState { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public FoundryDeploymentModel Model { get; set; } = new();
}

/// <summary>
/// Model information for an Azure AI Foundry deployment
/// </summary>
public class FoundryDeploymentModel
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Response from Azure AI Foundry deployment list API
/// </summary>
public class FoundryDeploymentListResponse
{
    [JsonPropertyName("value")]
    public List<FoundryDeployment> Value { get; set; } = new();
}
