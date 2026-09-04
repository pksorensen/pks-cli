using System.Runtime.CompilerServices;
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
    /// Yields messages as each Graph page arrives instead of buffering the mailbox.
    /// A whole-mailbox pull is thousands of messages with their bodies attached;
    /// holding all of them before writing the first file costs a lot of memory and
    /// throws away everything done so far if page 40 fails.
    /// </summary>
    IAsyncEnumerable<MsGraphMessage> StreamMessagesAsync(MsGraphEmailQuery query, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a single email message by ID
    /// </summary>
    Task<MsGraphMessage?> GetMessageAsync(string messageId, string? mailbox = null, CancellationToken ct = default);

    /// <summary>
    /// Retrieves attachments for a specific email message
    /// </summary>
    Task<List<MsGraphAttachment>> GetAttachmentsAsync(string messageId, string? mailbox = null, CancellationToken ct = default);

    /// <summary>
    /// Lists every mail folder in the mailbox, children included, so a whole-mailbox
    /// export can say which folder each message came from.
    /// </summary>
    Task<List<MsGraphMailFolder>> GetMailFoldersAsync(string? mailbox = null, CancellationToken ct = default);

    /// <summary>
    /// Composes a message into the mailbox's Drafts folder and returns its id. Nothing here
    /// sends: Graph only puts mail on the wire on POST /send, which this service does not do
    /// and the CLI's token is not scoped for.
    /// </summary>
    Task<MsGraphDraftResult> CreateDraftAsync(MsGraphDraftRequest draft, string? mailbox = null, CancellationToken ct = default);

    /// <summary>
    /// Subject, recipients and send time of everything in one well-known folder — "drafts" to
    /// see what has already been composed, "sentitems" to see what has actually left. Graph
    /// cannot $filter on custom internet headers, so subject plus recipient is the cheapest
    /// identity available; a stricter version would stamp a singleValueExtendedProperty and
    /// filter on that.
    /// </summary>
    Task<List<MsGraphFolderMessage>> ListFolderMessagesAsync(string folder, DateTime? after = null, string? mailbox = null, CancellationToken ct = default);
}

/// <summary>
/// Implementation of Microsoft Graph email operations using the Graph REST API
/// </summary>
public class MsGraphEmailService : IMsGraphEmailService
{
    private const string SelectFields = "id,subject,parentFolderId,from,toRecipients,ccRecipients,bccRecipients,body,receivedDateTime,sentDateTime,hasAttachments,conversationId,internetMessageId,importance,isRead,categories,webLink";
    private const string HeaderSelectField = "internetMessageHeaders";

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
        var all = new List<MsGraphMessage>();
        await foreach (var message in StreamMessagesAsync(query, progress, ct))
        {
            all.Add(message);
        }

        _logger.LogInformation("Retrieved {Count} messages from folder '{Folder}'", all.Count, query.Folder);
        return all;
    }

    public async IAsyncEnumerable<MsGraphMessage> StreamMessagesAsync(
        MsGraphEmailQuery query,
        IProgress<string>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = BuildMessagesUrl(query);
        var yielded = 0;

        while (url != null)
        {
            ct.ThrowIfCancellationRequested();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuthHeader(request, token);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(ct);
            var listResponse = JsonSerializer.Deserialize<MsGraphMessageListResponse>(content, _jsonOptions);

            foreach (var message in listResponse?.Value ?? new List<MsGraphMessage>())
            {
                if (query.MaxMessages.HasValue && yielded >= query.MaxMessages.Value)
                {
                    yield break;
                }

                yielded++;
                yield return message;
            }

            progress?.Report($"Fetched {yielded} messages...");

            if (query.MaxMessages.HasValue && yielded >= query.MaxMessages.Value)
            {
                yield break;
            }

            url = listResponse?.ODataNextLink;
        }
    }

    public async Task<MsGraphMessage?> GetMessageAsync(string messageId, string? mailbox = null, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/messages/{messageId}?$select={SelectFields}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        SetAuthHeader(request, token);

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MsGraphMessage>(content, _jsonOptions);
    }

    public async Task<List<MsGraphAttachment>> GetAttachmentsAsync(string messageId, string? mailbox = null, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var url = $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/messages/{messageId}/attachments";
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

    public async Task<List<MsGraphMailFolder>> GetMailFoldersAsync(string? mailbox = null, CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Please sign in first using 'pks graph auth'.");

        var folders = new List<MsGraphMailFolder>();
        // Graph only returns the top level, so walk down into every folder that says it
        // has children. Archive and hand-made folders are where filed correspondence
        // actually lives; stopping at the top level would miss most of it.
        var queue = new Queue<string?>();
        queue.Enqueue(null);

        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            var url = parentId == null
                ? $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/mailFolders?$top=100"
                : $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/mailFolders/{Uri.EscapeDataString(parentId)}/childFolders?$top=100";

            while (url != null)
            {
                ct.ThrowIfCancellationRequested();

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                SetAuthHeader(request, token);

                var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<MsGraphMailFolderListResponse>(content, _jsonOptions);

                foreach (var folder in page?.Value ?? new List<MsGraphMailFolder>())
                {
                    folder.ParentFolderId ??= parentId;
                    folders.Add(folder);

                    if (folder.ChildFolderCount > 0)
                        queue.Enqueue(folder.Id);
                }

                url = page?.ODataNextLink;
            }
        }

        _logger.LogInformation("Retrieved {Count} mail folders", folders.Count);
        return folders;
    }

    /// <summary>
    /// Graph accepts inline base64 attachments up to about 3 MB; past that a message needs an
    /// upload session. Invoice PDFs are tens of kilobytes, so the limit is a guard rail, not a
    /// workflow.
    /// </summary>
    private const int MaxInlineAttachmentBytes = 3 * 1024 * 1024;

    public async Task<MsGraphDraftResult> CreateDraftAsync(MsGraphDraftRequest draft, string? mailbox = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Subject))
            throw new ArgumentException("A draft needs a subject.", nameof(draft));
        if (draft.To.Count == 0)
            throw new ArgumentException("A draft needs at least one recipient.", nameof(draft));

        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Run 'pks ms-graph register' first.");

        var url = $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/messages";

        var (id, webLink) = string.IsNullOrWhiteSpace(draft.InReplyTo)
            ? await CreateBlankDraftAsync(draft, url, token, ct)
            : await CreateReplyDraftAsync(draft, url, token, mailbox, ct);

        foreach (var attachment in draft.Attachments)
        {
            if (attachment.Content.Length > MaxInlineAttachmentBytes)
                throw new InvalidOperationException($"Attachment '{attachment.Name}' is {attachment.Content.Length / 1024} KB; Graph only takes inline attachments up to 3 MB.");

            var attachPayload = new Dictionary<string, object?>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = attachment.Name,
                ["contentType"] = attachment.ContentType,
                ["contentBytes"] = Convert.ToBase64String(attachment.Content)
            };

            var attachRequest = new HttpRequestMessage(HttpMethod.Post, $"{url}/{Uri.EscapeDataString(id)}/attachments")
            {
                Content = new StringContent(JsonSerializer.Serialize(attachPayload, _jsonOptions), System.Text.Encoding.UTF8, "application/json")
            };
            SetAuthHeader(attachRequest, token);

            var attachResponse = await _httpClient.SendAsync(attachRequest, ct);
            await ThrowOnGraphError(attachResponse, $"attach '{attachment.Name}'", ct);
        }

        _logger.LogInformation("Created draft '{Subject}' with {Count} attachment(s)", draft.Subject, draft.Attachments.Count);

        return new MsGraphDraftResult
        {
            Id = id,
            WebLink = webLink,
            AttachmentCount = draft.Attachments.Count
        };
    }

    public async Task<List<MsGraphFolderMessage>> ListFolderMessagesAsync(
        string folder,
        DateTime? after = null,
        string? mailbox = null,
        CancellationToken ct = default)
    {
        var token = await _authService.GetValidAccessTokenAsync()
            ?? throw new InvalidOperationException("Not authenticated. Run 'pks ms-graph register' first.");

        var messages = new List<MsGraphFolderMessage>();
        var url = $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/mailFolders/{Uri.EscapeDataString(folder)}/messages"
            + "?$select=subject,toRecipients,sentDateTime,webLink&$top=100";

        // Drafts have no sentDateTime, so the filter is only meaningful on a folder that has
        // left the building. Applying it to Drafts would silently return nothing.
        if (after.HasValue)
            url += $"&$filter=sentDateTime ge {after.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";

        while (url != null)
        {
            ct.ThrowIfCancellationRequested();

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            SetAuthHeader(request, token);

            var response = await _httpClient.SendAsync(request, ct);
            await ThrowOnGraphError(response, $"list '{folder}'", ct);

            var content = await response.Content.ReadAsStringAsync(ct);
            var page = JsonSerializer.Deserialize<MsGraphMessageListResponse>(content, _jsonOptions);

            foreach (var message in page?.Value ?? new List<MsGraphMessage>())
            {
                messages.Add(new MsGraphFolderMessage
                {
                    Subject = message.Subject.Trim(),
                    To = message.ToRecipients
                        .Select(r => r.EmailAddress?.Address?.Trim() ?? string.Empty)
                        .Where(a => a.Length > 0)
                        .ToList(),
                    SentDateTime = message.SentDateTime,
                    WebLink = message.WebLink
                });
            }

            url = page?.ODataNextLink;
        }

        return messages;
    }

    private static Dictionary<string, object?> Recipient(string address) => new()
    {
        ["emailAddress"] = new Dictionary<string, object?> { ["address"] = address }
    };

    /// <summary>
    /// EnsureSuccessStatusCode throws away the body, and Graph puts the only useful part of a
    /// failure there — "Access is denied" versus "mailbox not found" versus a bad address are
    /// all 403/404 without it.
    /// </summary>
    private static async Task ThrowOnGraphError(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                detail = message.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the raw body is the best we have.
        }

        throw new InvalidOperationException($"Graph refused to {what}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    private string BuildMessagesUrl(MsGraphEmailQuery query)
    {
        // The mailbox-wide collection spans every folder, Deleted Items and Junk
        // included — that is the point of asking for "all".
        var baseUrl = query.IsWholeMailbox
            ? $"{_config.GraphBaseUrl}/{UserSegment(query.Mailbox)}/messages"
            : $"{_config.GraphBaseUrl}/{UserSegment(query.Mailbox)}/mailFolders/{Uri.EscapeDataString(query.Folder)}/messages";
        var select = query.IncludeMessageHeaders ? $"{SelectFields},{HeaderSelectField}" : SelectFields;
        var parameters = new List<string>
        {
            $"$select={select}",
            "$orderby=receivedDateTime desc",
            // A whole-mailbox pull is thousands of messages; 50 per page is a lot of
            // round trips for no reason.
            $"$top={query.Top ?? (query.IsWholeMailbox ? 100 : 50)}"
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
            filters.Add($"from/emailAddress/address eq '{EscapeODataLiteral(query.From)}'");
        }

        if (!string.IsNullOrEmpty(query.Subject))
        {
            filters.Add($"contains(subject, '{EscapeODataLiteral(query.Subject)}')");
        }

        if (!string.IsNullOrEmpty(query.Filter))
        {
            filters.Add(query.Filter);
        }

        return string.Join(" and ", filters);
    }

    /// <summary>
    /// Resolves the Graph path segment for a mailbox: /me for the signed-in user, or
    /// /users/{upn} for a mailbox they hold FullAccess on. The value is a user principal
    /// name supplied on the command line, so it is validated rather than trusted — a
    /// stray slash would otherwise reach into an unrelated part of the Graph URL space.
    /// </summary>
    internal static string UserSegment(string? mailbox)
    {
        if (string.IsNullOrWhiteSpace(mailbox))
            return "me";

        var upn = mailbox.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(upn, @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$"))
            throw new ArgumentException($"'{mailbox}' is not a valid mailbox address.", nameof(mailbox));

        return $"users/{Uri.EscapeDataString(upn)}";
    }

    /// <summary>
    /// Escapes a value for use inside an OData string literal. A single quote in a
    /// caller-supplied filter would otherwise terminate the literal and let the rest
    /// of the value be parsed as query syntax.
    /// </summary>
    internal static string EscapeODataLiteral(string value) => value.Replace("'", "''");

    /// <summary>
    /// Composes a mail that opens its own thread: a plain POST to /messages.
    /// </summary>
    private async Task<(string Id, string? WebLink)> CreateBlankDraftAsync(
        MsGraphDraftRequest draft, string url, string token, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["subject"] = draft.Subject,
            ["body"] = new Dictionary<string, object?>
            {
                ["contentType"] = "HTML",
                ["content"] = draft.HtmlBody
            },
            ["toRecipients"] = draft.To.Select(Recipient).ToList()
        };

        if (draft.Cc.Count > 0)
            payload["ccRecipients"] = draft.Cc.Select(Recipient).ToList();
        if (draft.Bcc.Count > 0)
            payload["bccRecipients"] = draft.Bcc.Select(Recipient).ToList();
        if (!string.IsNullOrWhiteSpace(draft.ReplyTo))
            payload["replyTo"] = new[] { Recipient(draft.ReplyTo!) };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), System.Text.Encoding.UTF8, "application/json")
        };
        SetAuthHeader(request, token);

        var response = await _httpClient.SendAsync(request, ct);
        await ThrowOnGraphError(response, "create draft", ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = document.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph returned a draft without an id.");
        var webLink = document.RootElement.TryGetProperty("webLink", out var link) ? link.GetString() : null;
        return (id, webLink);
    }

    /// <summary>
    /// Composes a mail inside an existing thread. Graph's createReply is the only way to get
    /// the conversation headers right — a client threads on those, not on a subject that
    /// starts with "SV:". createReply also fills in the recipient, the subject and the quoted
    /// history, so this only patches in what the letter actually owns: its own text above the
    /// quote, and the Cc list. The subject deliberately stays Graph's, and the letter's own
    /// body is prepended to the quoted history rather than replacing it — a PATCH that sends
    /// only the letter would wipe the very thread this exists to preserve.
    /// </summary>
    private async Task<(string Id, string? WebLink)> CreateReplyDraftAsync(
        MsGraphDraftRequest draft, string url, string token, string? mailbox, CancellationToken ct)
    {
        var originalId = await ResolveMessageIdAsync(draft.InReplyTo!, mailbox, token, ct);

        var createRequest = new HttpRequestMessage(HttpMethod.Post, $"{url}/{Uri.EscapeDataString(originalId)}/createReply");
        SetAuthHeader(createRequest, token);
        var createResponse = await _httpClient.SendAsync(createRequest, ct);
        await ThrowOnGraphError(createResponse, "create reply draft", ct);

        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(ct));
        var id = created.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph returned a reply draft without an id.");
        var webLink = created.RootElement.TryGetProperty("webLink", out var link) ? link.GetString() : null;

        var quoted = created.RootElement.TryGetProperty("body", out var body)
            && body.TryGetProperty("content", out var quotedContent)
                ? quotedContent.GetString() ?? string.Empty
                : string.Empty;

        var patch = new Dictionary<string, object?>
        {
            ["body"] = new Dictionary<string, object?>
            {
                ["contentType"] = "HTML",
                ["content"] = draft.HtmlBody + quoted
            }
        };

        // createReply addresses the original sender. The letter may still want people on Cc,
        // and Bcc has no counterpart in a reply at all, so both come from the letter.
        if (draft.Cc.Count > 0)
            patch["ccRecipients"] = draft.Cc.Select(Recipient).ToList();
        if (draft.Bcc.Count > 0)
            patch["bccRecipients"] = draft.Bcc.Select(Recipient).ToList();
        if (!string.IsNullOrWhiteSpace(draft.ReplyTo))
            patch["replyTo"] = new[] { Recipient(draft.ReplyTo!) };

        var patchRequest = new HttpRequestMessage(HttpMethod.Patch, $"{url}/{Uri.EscapeDataString(id)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(patch, _jsonOptions), System.Text.Encoding.UTF8, "application/json")
        };
        SetAuthHeader(patchRequest, token);
        var patchResponse = await _httpClient.SendAsync(patchRequest, ct);
        await ThrowOnGraphError(patchResponse, "fill in reply draft", ct);

        _logger.LogInformation("Composed a reply to {InReplyTo} in its own thread", draft.InReplyTo);
        return (id, webLink);
    }

    /// <summary>
    /// Turns an internet message id — the one an export writes down — into the Graph id that
    /// every /messages route wants. They are not interchangeable, and only the internet one
    /// survives outside the mailbox.
    /// </summary>
    private async Task<string> ResolveMessageIdAsync(
        string internetMessageId, string? mailbox, string token, CancellationToken ct)
    {
        var value = internetMessageId.Trim();
        if (!value.StartsWith('<')) value = "<" + value;
        if (!value.EndsWith('>')) value += ">";

        var filter = $"internetMessageId eq '{value.Replace("'", "''")}'";
        var lookup = $"{_config.GraphBaseUrl}/{UserSegment(mailbox)}/messages"
            + $"?$filter={Uri.EscapeDataString(filter)}&$select=id,subject&$top=1";

        var request = new HttpRequestMessage(HttpMethod.Get, lookup);
        SetAuthHeader(request, token);
        var response = await _httpClient.SendAsync(request, ct);
        await ThrowOnGraphError(response, $"look up the message to reply to ({value})", ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var first = document.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"No message with internetMessageId {value} in {mailbox ?? "the signed-in mailbox"}. "
                + "A reply can only be composed in the mailbox that holds the mail it answers.");

        return first.GetProperty("id").GetString()
            ?? throw new InvalidOperationException($"Graph returned the message {value} without an id.");
    }

    private static void SetAuthHeader(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}
