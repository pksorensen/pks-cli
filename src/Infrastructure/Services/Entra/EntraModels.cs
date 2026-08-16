using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Entra;

/// <summary>
/// An app registration as Microsoft Graph describes it. Only the fields anything here acts on —
/// Graph returns some forty more and none of them belong in a status table.
/// </summary>
public sealed class EntraApplication
{
    /// <summary>The directory object id. What every write goes to.</summary>
    [JsonPropertyName("id")]
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>The client id. What an app signs in with, and the only one of the two an operator sees.</summary>
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("signInAudience")]
    public string SignInAudience { get; set; } = string.Empty;

    [JsonPropertyName("web")]
    public EntraWebSection? Web { get; set; }

    [JsonPropertyName("spa")]
    public EntraWebSection? Spa { get; set; }
}

public sealed class EntraWebSection
{
    [JsonPropertyName("redirectUris")]
    public List<string> RedirectUris { get; set; } = new();
}

/// <summary>What Graph returns from <c>addPassword</c>. The secret is in here exactly once.</summary>
public sealed class EntraSecret
{
    public string KeyId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset ExpiresOn { get; set; }

    /// <summary>
    /// The client secret, as it comes off the wire. Graph shows it once and never again, which is why
    /// this goes straight into the encrypted store rather than through a console — and why it is a
    /// <see cref="SecretValue"/>, so a command that gets one back cannot print it by accident.
    /// </summary>
    public SecretValue Value { get; set; }
}

/// <summary>
/// What pks remembers about an app registration it provisioned, stored encrypted under
/// <c>entra.app.{alias}.credentials</c>.
///
/// The whole point of the alias is that everything downstream — a capability binding, a rotation, a
/// deployment — names the app by a word a person chose ("margin"), never by pasting a guid and a
/// secret around. The secret in here is the one that was minted; nothing reads it back except the
/// resolver, on its way into a child process's environment.
/// </summary>
public sealed class EntraStoredApp
{
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The credential's id in the directory, so a rotation can remove exactly the one it replaced.</summary>
    public string SecretKeyId { get; set; } = string.Empty;
    public DateTimeOffset SecretExpiresOn { get; set; }
    public SecretValue ClientSecret { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Whether the stored secret is past its end date. A run that fails with AADSTS7000222 is
    /// this, and the operator should be told before the run rather than after.</summary>
    [JsonIgnore]
    public bool IsExpired => SecretExpiresOn != default && SecretExpiresOn <= DateTimeOffset.UtcNow;
}

/// <summary>What `pks entra app init` was asked to end up with.</summary>
public sealed class EntraAppRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string? TenantId { get; set; }

    /// <summary>Graph's own vocabulary: AzureADMyOrg, AzureADMultipleOrgs, AzureADandPersonalMicrosoftAccount.</summary>
    public string SignInAudience { get; set; } = "AzureADMyOrg";

    public List<string> RedirectUris { get; set; } = new();
    public List<string> SpaRedirectUris { get; set; } = new();

    /// <summary>Adopt this exact app instead of searching by name.</summary>
    public string? AdoptAppId { get; set; }

    public int SecretDays { get; set; } = 180;

    /// <summary>Mint a new secret even when a usable one is already stored for this alias.</summary>
    public bool Rotate { get; set; }
}

/// <summary>
/// Credentials somebody else provisioned — read off the portal blade, or out of a password manager —
/// on their way into the same encrypted store a minted one lands in.
///
/// This is the mode for a directory pks has no business writing to: a customer's production tenant,
/// or one where creating an app registration is somebody's job and not yours. Everything downstream is
/// identical afterwards; the only difference is who made the registration.
/// </summary>
public sealed class EntraManualApp
{
    public string Alias { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public SecretValue ClientSecret { get; set; }

    /// <summary>
    /// When the portal says it expires, if the operator happened to know. Left unset it stays
    /// <c>default</c>, which <see cref="EntraStoredApp.IsExpired"/> reads as "not stated" — better
    /// than a guess that would colour a working credential red on the day it was entered.
    /// </summary>
    public DateTimeOffset? ExpiresOn { get; set; }
}

/// <summary>How an init call ended, so the command can say what it did rather than guess.</summary>
public sealed class EntraAppResult
{
    public EntraStoredApp App { get; set; } = new();
    public bool CreatedApplication { get; set; }
    public bool CreatedServicePrincipal { get; set; }
    public bool AddedRedirectUris { get; set; }
    public bool MintedSecret { get; set; }
    public string? RemovedSecretKeyId { get; set; }
}
