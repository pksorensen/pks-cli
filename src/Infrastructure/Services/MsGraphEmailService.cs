using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for Microsoft Graph email operations
/// </summary>
public interface IMsGraphEmailService
{
    /// <summary>
    /// Retrieves email messages matching the specified query
    /// </summary>
    Task<List<MsGraphMessage>> GetMessagesAsync(MsGraphEmailQuery query, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single email message by ID
    /// </summary>
    Task<MsGraphMessage?> GetMessageAsync(string messageId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves attachments for a specific email message
    /// </summary>
    Task<List<MsGraphAttachment>> GetAttachmentsAsync(string messageId, CancellationToken ct = default);
}

/// <summary>
/// Implementation of Microsoft Graph email operations using the Graph REST API
/// </summary>
public class MsGraphEmailService : IMsGraphEmailService
{
    private const string SelectFields = "id,subject,from,toRecipients,ccRecipients,bccRecipients,body,receivedDateTime,sentDateTime,hasAttachments,conversationId,internetMessageId,importance,isRead,categories,webLink";

    private readonly HttpClient _httpClient;
    private readonly IMsGraphAuthenticationService _authService;
    private readonly ILogger<MsGraphEmailService> _logger;
    private readonly MsGraphAuthConfig _config;
    private readonly JsonSerializerOptions _jsonOptions;

    public MsGraphEmailService(
        HttpClient httpClient,
        IMsGraphAuthenticationService authService,
        ILogger<MsGraphEmailService> logger,
        MsGraphAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _authService = authService;
        _logger = logger;
        _config = config ?? new MsGraphAuthConfig();

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    public async Task<List<MsGraphMessage>> GetMessagesAsync(MsGraphEmailQuery query, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = BuildMessagesUrl(query);
        var allMessages = new List<MsGraphMessage>();

        while (url != null)
        {
            ct.ThrowIfCancellationRequested();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuthHeader(request, token);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            var listResponse = JsonSerializer.Deserialize<MsGraphMessageListResponse>(content, _jsonOptions);

            if (listResponse?.Value != null)
            {
                allMessages.AddRange(listResponse.Value);
                progress?.Report($"Fetched {allMessages.Count} messages...");
            }

            if (query.MaxMessages.HasValue && allMessages.Count >= query.MaxMessages.Value)
            {
                allMessages = allMessages.Take(query.MaxMessages.Value).ToList();
                break;
            }

            url = listResponse?.ODataNextLink;
        }

        _logger.LogInformation("Retrieved {Count} messages from folder '{Folder}'", allMessages.Count, query.Folder);
        return allMessages;
    }

    public async Task<MsGraphMessage?> GetMessageAsync(string messageId, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = $"{_config.GraphBaseUrl}/me/messages/{messageId}?$select={SelectFields}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(request, token);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MsGraphMessage>(content, _jsonOptions);
    }

    public async Task<List<MsGraphAttachment>> GetAttachmentsAsync(string messageId, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = $"{_config.GraphBaseUrl}/me/messages/{messageId}/attachments";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(request, token);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(content);
        var valueElement = document.RootElement.GetProperty("value");
        var attachments = JsonSerializer.Deserialize<List<MsGraphAttachment>>(valueElement.GetRawText(), _jsonOptions);

        return attachments ?? new List<MsGraphAttachment>();
    }

    private string BuildMessagesUrl(MsGraphEmailQuery query)
    {
        var baseUrl = $"{_config.GraphBaseUrl}/me/mailFolders/{query.Folder}/messages";
        var parameters = new List<string>
        {
            $"$select={SelectFields}",
            "$orderby=receivedDateTime desc",
            $"$top={query.Top ?? 50}"
        };

        var filter = BuildFilter(query);
        if (!string.IsNullOrEmpty(filter))
        {
            parameters.Add($"$filter={filter}");
        }

        return $"{baseUrl}?{string.Join("&", parameters)}";
    }

    private static string BuildFilter(MsGraphEmailQuery query)
    {
        var filters = new List<string>();

        if (query.After.HasValue)
        {
            filters.Add($"receivedDateTime ge {query.After.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (query.Before.HasValue)
        {
            filters.Add($"receivedDateTime le {query.Before.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (!string.IsNullOrEmpty(query.From))
        {
            filters.Add($"from/emailAddress/address eq '{query.From}'");
        }

        if (!string.IsNullOrEmpty(query.Subject))
        {
            filters.Add($"contains(subject, '{query.Subject}')");
        }

        if (!string.IsNullOrEmpty(query.Filter))
        {
            filters.Add(query.Filter);
        }

        return string.Join(" and ", filters);
    }

    private static void SetAuthHeader(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}
