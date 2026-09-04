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
    public string[] DefaultScopes { get; set; } = { "https://graph.microsoft.com/Mail.Read", "https://graph.microsoft.com/Mail.Read.Shared", "https://graph.microsoft.com/Mail.ReadBasic", "https://graph.microsoft.com/User.Read", "offline_access" };
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
    // Graph sends explicit JSON nulls for these — a message with no subject is
    // ordinary, and some newsletters have no importance either. System.Text.Json
    // writes that null straight over the initializer, so the non-nullable type is a
    // lie unless the setter defends it. Everything downstream renders these, so one
    // null subject in ten thousand messages otherwise takes the message out of the
    // export with a NullReferenceException.
    private string _id = string.Empty;
    private string _subject = string.Empty;
    private string _importance = "normal";
    private List<MsGraphRecipient> _to = new();
    private List<MsGraphRecipient> _cc = new();
    private List<MsGraphRecipient> _bcc = new();
    private List<string> _categories = new();

    [JsonPropertyName("id")]
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    [JsonPropertyName("subject")]
    public string Subject { get => _subject; set => _subject = value ?? string.Empty; }

    [JsonPropertyName("from")]
    public MsGraphRecipient? From { get; set; }

    [JsonPropertyName("toRecipients")]
    public List<MsGraphRecipient> ToRecipients { get => _to; set => _to = value ?? new(); }

    [JsonPropertyName("ccRecipients")]
    public List<MsGraphRecipient> CcRecipients { get => _cc; set => _cc = value ?? new(); }

    [JsonPropertyName("bccRecipients")]
    public List<MsGraphRecipient> BccRecipients { get => _bcc; set => _bcc = value ?? new(); }

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

    /// <summary>Id of the folder the message sits in. Resolved to a readable path on export.</summary>
    [JsonPropertyName("parentFolderId")]
    public string? ParentFolderId { get; set; }

    [JsonPropertyName("internetMessageId")]
    public string? InternetMessageId { get; set; }

    [JsonPropertyName("importance")]
    public string Importance { get => _importance; set => _importance = value ?? "normal"; }

    [JsonPropertyName("isRead")]
    public bool IsRead { get; set; }

    [JsonPropertyName("categories")]
    public List<string> Categories { get => _categories; set => _categories = value ?? new(); }

    [JsonPropertyName("webLink")]
    public string? WebLink { get; set; }

    /// <summary>
    /// Raw internet headers. Only populated when the query asks for them; they carry
    /// In-Reply-To and References, which are the only reliable way to stitch a thread
    /// together across two different mailboxes (conversationId is per-mailbox).
    /// </summary>
    [JsonPropertyName("internetMessageHeaders")]
    public List<MsGraphInternetMessageHeader>? InternetMessageHeaders { get; set; }

    /// <summary>Value of the In-Reply-To header, or null when headers were not fetched.</summary>
    public string? InReplyTo => FindHeader("In-Reply-To");

    /// <summary>Value of the References header, or null when headers were not fetched.</summary>
    public string? References => FindHeader("References");

    private string? FindHeader(string name) => InternetMessageHeaders?
        .FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>
/// A single RFC 5322 header on a Microsoft Graph message.
/// </summary>
public class MsGraphInternetMessageHeader
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
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
    private string _contentType = string.Empty;
    private string _content = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get => _contentType; set => _contentType = value ?? string.Empty; }

    [JsonPropertyName("content")]
    public string Content { get => _content; set => _content = value ?? string.Empty; }
}

/// <summary>
/// Represents a Microsoft Graph email attachment
/// </summary>
public class MsGraphAttachment
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _contentType = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get => _id; set => _id = value ?? string.Empty; }

    [JsonPropertyName("name")]
    public string Name { get => _name; set => _name = value ?? string.Empty; }

    [JsonPropertyName("contentType")]
    public string ContentType { get => _contentType; set => _contentType = value ?? string.Empty; }

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
    /// <summary>
    /// Mail folder to read. "all" (or empty) reads the whole mailbox across every
    /// folder — archive, user-created folders and their children, Deleted Items and
    /// Junk included — which is what an evidence export usually wants.
    /// </summary>
    public string Folder { get; set; } = "all";

    /// <summary>True when <see cref="Folder"/> asks for the whole mailbox rather than one folder.</summary>
    public bool IsWholeMailbox =>
        string.IsNullOrWhiteSpace(Folder) || Folder.Equals("all", StringComparison.OrdinalIgnoreCase);
    public int? Top { get; set; } = 50;
    public int? MaxMessages { get; set; }
    public string? Filter { get; set; }
    public bool IncludeAttachments { get; set; } = true;

    /// <summary>
    /// Mailbox to read, as a user principal name. Null reads the signed-in user's own
    /// mailbox (/me). Any other value requires FullAccess delegation on that mailbox.
    /// </summary>
    public string? Mailbox { get; set; }

    /// <summary>
    /// Request internetMessageHeaders alongside each message. Needed for cross-mailbox
    /// threading; costs payload size. Set false if a tenant rejects the $select.
    /// </summary>
    public bool IncludeMessageHeaders { get; set; } = true;
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

    /// <summary>How exported messages are laid out on disk.</summary>
    public EmailExportLayout Layout { get; set; } = EmailExportLayout.Thread;

    /// <summary>Write a .gitignore into the output directory so the export stays out of git. Default true.</summary>
    public bool WriteGitignore { get; set; } = true;

    /// <summary>
    /// Remember which message ids have been exported and skip them on a later run.
    /// Default true: an interrupted export of a large mailbox picks up where it left off.
    /// </summary>
    public bool Resume { get; set; } = true;

    /// <summary>
    /// Additionally narrow the Graph query to messages newer than the newest one seen
    /// in a previous run. Much faster, but blind to an older message filed into the
    /// mailbox since — off by default.
    /// </summary>
    public bool Incremental { get; set; }

    /// <summary>
    /// Drop attachments whose content type is not recognised instead of storing them
    /// as an inert .bin. Off by default: senders mislabel real documents constantly.
    /// </summary>
    public bool SkipUnknownTypes { get; set; }

    /// <summary>
    /// Re-fetch attachments for messages whose manifest records something held back,
    /// without re-exporting the rest of the mailbox.
    /// </summary>
    public bool RetryHeldBack { get; set; }

    /// <summary>
    /// Address of the mailbox being exported, used to decide whether a message is
    /// incoming or outgoing. Falls back to a folder-name heuristic when empty.
    /// </summary>
    public string? MailboxAddress { get; set; }

    /// <summary>Skip inline attachments — signature logos and the like.</summary>
    public bool SkipInlineAttachments { get; set; } = true;

    /// <summary>Attachments larger than this are recorded in the manifest but not written.</summary>
    public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Ceiling on the total bytes of attachments this run will write.</summary>
    public long MaxTotalAttachmentBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>
    /// Content types to write to disk. Empty means the built-in safe list. The single
    /// entry "all" writes every attachment (still under a content-addressed name).
    /// </summary>
    public List<string> AllowedContentTypes { get; set; } = new();
}

/// <summary>
/// Directory layout for an email export.
/// </summary>
public enum EmailExportLayout
{
    /// <summary>One directory per conversation — the unit of evidence. Default.</summary>
    Thread,

    /// <summary>raw/yyyy/MM/dd/HHmmss-slug/slug.md, the original layout.</summary>
    Date
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

    /// <summary>Attachments written to disk.</summary>
    public int AttachmentsWritten { get; set; }

    /// <summary>Attachments recorded in a manifest but deliberately not written.</summary>
    public int AttachmentsSkipped { get; set; }

    /// <summary>Total bytes of attachment payload written.</summary>
    public long AttachmentBytesWritten { get; set; }

    /// <summary>Distinct conversations touched by the export.</summary>
    public int ThreadCount { get; set; }
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

    /// <summary>Free text for phases that have no message count yet, such as paging through Graph.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// One mail folder. Used to turn the parentFolderId on a message into something a
/// human can read, so a whole-mailbox export still says where each message lived.
/// </summary>
public class MsGraphMailFolder
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ParentFolderId { get; set; }
    public int ChildFolderCount { get; set; }
}

public class MsGraphMailFolderListResponse
{
    public List<MsGraphMailFolder> Value { get; set; } = new();

    [System.Text.Json.Serialization.JsonPropertyName("@odata.nextLink")]
    public string? ODataNextLink { get; set; }
}
