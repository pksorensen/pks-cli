using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Configuration for Azure DevOps OAuth2 authentication
/// </summary>
public class AzureDevOpsAuthConfig
{
    public string ClientId { get; set; } = "872cd9fa-d31f-45e0-9eab-6e460a02d1f1";
    public string AuthorityBase { get; set; } = "https://login.microsoftonline.com";
    public string ProfileUrl { get; set; } = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1";
    public string AccountsUrl { get; set; } = "https://app.vssps.visualstudio.com/_apis/accounts?api-version=7.1";
    /// <summary>
    /// 499b84ac-1321-427f-aa17-267ca6975798 is the Azure DevOps resource ID.
    /// offline_access gives us a refresh token.
    /// </summary>
    public string Scope { get; set; } = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";
    public int CallbackTimeoutSeconds { get; set; } = 120;

    public string GetAuthorizeUrl(string tenantId) => $"{AuthorityBase}/{tenantId}/oauth2/v2.0/authorize";
    public string GetTokenUrl(string tenantId) => $"{AuthorityBase}/{tenantId}/oauth2/v2.0/token";
}

/// <summary>
/// PKCE challenge pair for OAuth2 authorization code flow
/// </summary>
public class PkceChallenge
{
    public string CodeVerifier { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
}

/// <summary>
/// Token response from Azure DevOps OAuth2 token endpoint
/// </summary>
public class AdoTokenResponse
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

/// <summary>
/// User profile from VSSPS profile API
/// </summary>
public class AdoUserProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("emailAddress")]
    public string EmailAddress { get; set; } = string.Empty;
}

/// <summary>
/// Response from VSSPS accounts API
/// </summary>
public class AdoAccountsResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("value")]
    public List<AdoAccount> Value { get; set; } = new();
}

/// <summary>
/// Azure DevOps organization/account
/// </summary>
public class AdoAccount
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; set; } = string.Empty;

    [JsonPropertyName("accountName")]
    public string AccountName { get; set; } = string.Empty;

    [JsonPropertyName("accountUri")]
    public string AccountUri { get; set; } = string.Empty;
}

/// <summary>
/// Intermediate result from auth initiation before org selection
/// </summary>
public class AdoAuthResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public AdoUserProfile Profile { get; set; } = new();
    public List<AdoAccount> Accounts { get; set; } = new();
}

/// <summary>
/// Persisted Azure DevOps credentials
/// </summary>
public class AdoStoredCredentials
{
    public string RefreshToken { get; set; } = string.Empty;
    public string SelectedOrg { get; set; } = string.Empty;
    public string TenantId { get; set; } = "common";
    public AdoUserProfile Profile { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}
