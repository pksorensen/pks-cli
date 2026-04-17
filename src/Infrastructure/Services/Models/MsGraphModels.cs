using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

// === Microsoft Graph API Authentication Models ===

/// <summary>
/// Comprehensive Microsoft Graph authentication configuration using Entra ID
/// </summary>
public class MsGraphAuthConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = "common";
    public string[] DefaultScopes { get; set; } = { "https://graph.microsoft.com/Mail.Read", "https://graph.microsoft.com/Mail.ReadBasic", "https://graph.microsoft.com/User.Read", "offline_access" };
    public string DeviceCodeUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/devicecode";
    public string TokenUrl => $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token";
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int MaxPollingAttempts { get; set; } = 120;
    public string UserAgent { get; set; } = "PKS-CLI/1.0.0";
}

/// <summary>
/// Stored Microsoft Graph authentication token with metadata
/// </summary>
public class MsGraphStoredToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public string ClientId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsValid { get; set; }
    public DateTime LastValidated { get; set; }
    public string? UserPrincipalName { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>
/// Device code response from Microsoft Entra ID
/// </summary>
public class MsGraphDeviceCodeResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Token response from Microsoft Entra ID OAuth 2.0 endpoint
/// </summary>
public class MsGraphTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int? ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
}

/// <summary>
/// Device code authentication status for Microsoft Graph
/// </summary>
public class MsGraphDeviceAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTime? ExpiresAt { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public DateTime CheckedAt { get; set; }
}

/// <summary>
/// Authentication flow progress information for Microsoft Graph
/// </summary>
public class MsGraphAuthProgress
{
    public MsGraphAuthStep CurrentStep { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUrl { get; set; }
    public TimeSpan? TimeRemaining { get; set; }
    public string? StatusMessage { get; set; }
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Authentication flow step enumeration for Microsoft Graph
/// </summary>
public enum MsGraphAuthStep
{
    Initializing,
    RequestingDeviceCode,
    WaitingForUserAuthorization,
    PollingForToken,
    ValidatingToken,
    Complete,
    Error
}

// === Microsoft Graph Mail Models ===

/// <summary>
/// Represents a Microsoft Graph email message
/// </summary>
public class MsGraphMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("subject")]
    public string Subject { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public MsGraphRecipient? From { get; set; }

    [JsonPropertyName("toRecipients")]
    public List<MsGraphRecipient> ToRecipients { get; set; } = new();

    [JsonPropertyName("ccRecipients")]
    public List<MsGraphRecipient> CcRecipients { get; set; } = new();

    [JsonPropertyName("bccRecipients")]
    public List<MsGraphRecipient> BccRecipients { get; set; } = new();

    [JsonPropertyName("body")]
    public MsGraphBody? Body { get; set; }

    [JsonPropertyName("receivedDateTime")]
    public DateTime? ReceivedDateTime { get; set; }

    [JsonPropertyName("sentDateTime")]
    public DateTime? SentDateTime { get; set; }

    [JsonPropertyName("hasAttachments")]
    public bool HasAttachments { get; set; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("internetMessageId")]
    public string? InternetMessageId { get; set; }

    [JsonPropertyName("importance")]
    public string Importance { get; set; } = "normal";

    [JsonPropertyName("isRead")]
    public bool IsRead { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = new();

    [JsonPropertyName("webLink")]
    public string? WebLink { get; set; }
}

/// <summary>
/// Represents a Microsoft Graph email recipient
/// </summary>
public class MsGraphRecipient
{
    [JsonPropertyName("emailAddress")]
    public MsGraphEmailAddress? EmailAddress { get; set; }
}

/// <summary>
/// Represents a Microsoft Graph email address
/// </summary>
public class MsGraphEmailAddress
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

/// <summary>
/// Represents a Microsoft Graph message body
/// </summary>
public class MsGraphBody
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Represents a Microsoft Graph email attachment
/// </summary>
public class MsGraphAttachment
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("contentBytes")]
    public string? ContentBytes { get; set; }

    [JsonPropertyName("isInline")]
    public bool IsInline { get; set; }
}

/// <summary>
/// Represents a paginated list response of Microsoft Graph messages
/// </summary>
public class MsGraphMessageListResponse
{
    [JsonPropertyName("value")]
    public List<MsGraphMessage> Value { get; set; } = new();

    [JsonPropertyName("@odata.nextLink")]
    public string? ODataNextLink { get; set; }
}

/// <summary>
/// Represents a Microsoft Graph user profile
/// </summary>
public class MsGraphUserProfile
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("mail")]
    public string? Mail { get; set; }

    [JsonPropertyName("userPrincipalName")]
    public string? UserPrincipalName { get; set; }
}

// === Email Query and Export Models ===

/// <summary>
/// Query parameters for filtering Microsoft Graph email messages
/// </summary>
public class MsGraphEmailQuery
{
    public DateTime? After { get; set; }
    public DateTime? Before { get; set; }
    public string? From { get; set; }
    public string? Subject { get; set; }
    public string Folder { get; set; } = "inbox";
    public int? Top { get; set; } = 50;
    public int? MaxMessages { get; set; }
    public string? Filter { get; set; }
    public bool IncludeAttachments { get; set; } = true;
}

/// <summary>
/// Options for exporting Microsoft Graph email messages to disk
/// </summary>
public class MsGraphEmailExportOptions
{
    public string OutputDirectory { get; set; } = ".emails";
    public MsGraphEmailQuery Query { get; set; } = new();
    public bool DownloadAttachments { get; set; } = true;
    public bool OverwriteExisting { get; set; } = false;
}

/// <summary>
/// Result of an email export operation
/// </summary>
public class EmailExportResult
{
    public int TotalMessages { get; set; }
    public int ExportedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Progress information during email export
/// </summary>
public class EmailExportProgress
{
    public int CurrentMessage { get; set; }
    public int TotalMessages { get; set; }
    public string? CurrentSubject { get; set; }
    public string Phase { get; set; } = string.Empty;
}
