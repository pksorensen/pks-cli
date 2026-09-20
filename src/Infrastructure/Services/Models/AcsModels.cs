using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

// ── ARM: Microsoft.Communication/communicationServices ───────────────────────

public class CommunicationServiceResource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public CommunicationServiceProperties Properties { get; set; } = new();

    /// <summary>Where it was found. Filled by the caller, never by ARM.</summary>
    [JsonIgnore]
    public string? TenantId { get; set; }

    [JsonIgnore]
    public AzureSubscription? Subscription { get; set; }
}

public class CommunicationServiceProperties
{
    /// <summary>The data-plane host (<c>{name}.{region}.communication.azure.com</c>); the SMS and
    /// phone-number APIs live at <c>https://{hostName}</c>.</summary>
    [JsonPropertyName("hostName")]
    public string HostName { get; set; } = string.Empty;

    [JsonPropertyName("dataLocation")]
    public string DataLocation { get; set; } = string.Empty;
}

public class CommunicationServiceListResponse
{
    [JsonPropertyName("value")]
    public List<CommunicationServiceResource> Value { get; set; } = new();

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

// ── Data plane: GET {endpoint}/phoneNumbers ──────────────────────────────────

public class AcsPhoneNumber
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>E.164, e.g. <c>+4566339237</c>.</summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    /// <summary><c>geographic</c>, <c>tollFree</c> or <c>mobile</c>.</summary>
    [JsonPropertyName("phoneNumberType")]
    public string PhoneNumberType { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public AcsPhoneNumberCapabilities Capabilities { get; set; } = new();
}

public class AcsPhoneNumberCapabilities
{
    /// <summary><c>none</c>, <c>inbound</c>, <c>outbound</c> or <c>inbound+outbound</c>.</summary>
    [JsonPropertyName("sms")]
    public string Sms { get; set; } = "none";

    [JsonPropertyName("calling")]
    public string Calling { get; set; } = "none";

    public bool CanSendSms =>
        Sms.Equals("outbound", StringComparison.OrdinalIgnoreCase) ||
        Sms.Equals("inbound+outbound", StringComparison.OrdinalIgnoreCase);
}

public class AcsPhoneNumberListResponse
{
    [JsonPropertyName("phoneNumbers")]
    public List<AcsPhoneNumber> PhoneNumbers { get; set; } = new();

    [JsonPropertyName("nextLink")]
    public string? NextLink { get; set; }
}

// ── Data plane: POST {endpoint}/sms ──────────────────────────────────────────

public class AcsSmsSendResponse
{
    [JsonPropertyName("value")]
    public List<AcsSmsSendResponseItem> Value { get; set; } = new();
}

public class AcsSmsSendResponseItem
{
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("httpStatusCode")]
    public int HttpStatusCode { get; set; }

    [JsonPropertyName("successful")]
    public bool Successful { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
