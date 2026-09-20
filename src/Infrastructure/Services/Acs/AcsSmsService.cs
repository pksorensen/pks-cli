using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Acs;

/// <summary>The outcome of one SMS send. <see cref="Error"/> is safe to print; the message text
/// is never part of it.</summary>
public sealed record SmsSendResult(bool Ok, string? MessageId, string? Error, bool Truncated)
{
    public static SmsSendResult Fail(string error) => new(false, null, error, false);
}

/// <summary>What <c>pks acs status</c> and the runner's capability probe need to know without
/// touching a token: the default sender entry, the default recipient, and whether both exist.</summary>
public sealed record AcsSmsDefaults(AzureResourceEntry? From, string? Recipient)
{
    public bool IsConfigured => From is { Enabled: true } && !string.IsNullOrWhiteSpace(Recipient);
}

public interface IAcsSmsService
{
    /// <summary>The configured default sender and recipient (no network).</summary>
    Task<AcsSmsDefaults> GetDefaultsAsync(CancellationToken ct = default);

    /// <summary>True when this host can send an SMS right now without asking anything: an enabled
    /// sender is the default and a recipient is set. This is what the runner advertises as the
    /// <c>sms</c> capability.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    Task SetDefaultsAsync(string? from, string? recipient);

    /// <summary>Sends one SMS. <paramref name="to"/> and <paramref name="from"/> fall back to the
    /// configured defaults. The message is cut to <see cref="AcsSmsService.MaxMessageLength"/>
    /// characters. Never throws for a delivery problem — read <see cref="SmsSendResult.Error"/>.</summary>
    Task<SmsSendResult> SendAsync(string message, string? to = null, string? from = null, CancellationToken ct = default);
}

/// <summary>
/// SMS through Azure Communication Services with the user's own Entra sign-in — no connection
/// string, no access key. The sender is a registry entry of kind
/// <see cref="AzureResourceKind.CommunicationServices"/> (a phone number or an alphanumeric id on
/// one ACS resource); the token comes from the tenant store for that entry's tenant.
///
/// The recipient lives here, on the runner host, and not on the assembly-line station: a phone
/// number on a station's config would be exported with the line repo. Jobs never choose it.
/// </summary>
public sealed class AcsSmsService : IAcsSmsService
{
    public const string FromKey = "acs.sms.from";
    public const string RecipientKey = "acs.sms.recipient";
    public const string InitCommand = "pks acs init";

    /// <summary>Two GSM-7 segments. A one-time code plus a sentence fits; a station's full
    /// notification body does not need to.</summary>
    public const int MaxMessageLength = 306;

    /// <summary>Sends per rolling hour before the service refuses. A runaway station cannot
    /// empty the ACS balance from here.</summary>
    public const int MaxSendsPerHour = 30;

    private readonly IAzureResourceRegistry _registry;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAzureFoundryAuthService _foundry;
    private readonly IConfigurationService _config;
    private readonly HttpClient _http;
    private readonly Queue<DateTime> _recentSends = new();
    private readonly object _sendLock = new();

    public AcsSmsService(
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        IAzureFoundryAuthService foundry,
        IConfigurationService config,
        IHttpClientFactory httpClientFactory)
        : this(registry, tenants, foundry, config, httpClientFactory.CreateClient())
    {
    }

    /// <summary>Test seam: a client with a stubbed handler.</summary>
    public AcsSmsService(
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        IAzureFoundryAuthService foundry,
        IConfigurationService config,
        HttpClient http)
    {
        _registry = registry;
        _tenants = tenants;
        _foundry = foundry;
        _config = config;
        _http = http;
    }

    public async Task<AcsSmsDefaults> GetDefaultsAsync(CancellationToken ct = default)
    {
        var fromKey = await _config.GetAsync(FromKey);
        var recipient = await _config.GetAsync(RecipientKey);
        AzureResourceEntry? from = null;
        if (!string.IsNullOrWhiteSpace(fromKey))
            from = await _registry.FindAsync(AzureResourceKind.CommunicationServices, fromKey);
        return new AcsSmsDefaults(from, string.IsNullOrWhiteSpace(recipient) ? null : recipient.Trim());
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            return (await GetDefaultsAsync(ct)).IsConfigured;
        }
        catch
        {
            return false;
        }
    }

    public async Task SetDefaultsAsync(string? from, string? recipient)
    {
        if (from != null) await _config.SetAsync(FromKey, from, global: true);
        if (recipient != null) await _config.SetAsync(RecipientKey, recipient, global: true);
    }

    public async Task<SmsSendResult> SendAsync(string message, string? to = null, string? from = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return SmsSendResult.Fail("Empty message.");

        var defaults = await GetDefaultsAsync(ct);
        var recipient = string.IsNullOrWhiteSpace(to) ? defaults.Recipient : to.Trim();
        if (string.IsNullOrWhiteSpace(recipient))
            return SmsSendResult.Fail($"No recipient. Pass one or set a default with `{InitCommand}`.");
        if (!IsE164(recipient))
            return SmsSendResult.Fail($"Recipient '{recipient}' is not an E.164 number (+4512345678).");

        var sender = string.IsNullOrWhiteSpace(from)
            ? defaults.From
            : await _registry.FindAsync(AzureResourceKind.CommunicationServices, from.Trim());
        if (sender == null)
            return SmsSendResult.Fail($"No sender. Run `{InitCommand}` to enable a phone number or alphanumeric id.");
        if (!sender.Enabled)
            return SmsSendResult.Fail($"Sender '{sender.Key}' is registered but disabled. Run `{InitCommand} --enable {sender.Key}`.");
        if (string.IsNullOrWhiteSpace(sender.Endpoint))
            return SmsSendResult.Fail($"Sender '{sender.Key}' has no endpoint recorded. Run `{InitCommand}` to rediscover it.");

        if (!TryReserveSend(out var retryIn))
            return SmsSendResult.Fail($"Rate limit: {MaxSendsPerHour} SMS per hour from this host; try again in {retryIn.TotalMinutes:F0} min.");

        var truncated = false;
        var text = message.Trim();
        if (text.Length > MaxMessageLength)
        {
            text = text[..(MaxMessageLength - 3)] + "...";
            truncated = true;
        }

        string token;
        try
        {
            token = await new AzureQueryTokenProvider(_tenants, _foundry, InitCommand)
                .GetTokenAsync(sender, AzureArmDiscovery.CommunicationScope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return SmsSendResult.Fail(ex.Message);
        }

        var url = $"https://{sender.Endpoint.TrimEnd('/')}/sms?api-version=2021-03-07";
        var body = new
        {
            from = sender.Key,
            smsRecipients = new[] { new { to = recipient } },
            message = text,
            smsSendOptions = new { enableDeliveryReport = false },
        };

        HttpResponseMessage response;
        string content;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            response = await _http.SendAsync(request, ct);
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return SmsSendResult.Fail($"ACS request failed: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var hint = (int)response.StatusCode is 401 or 403
                ? " — the signed-in identity has no role on the resource (Contributor is what Microsoft documents)."
                : "";
            return SmsSendResult.Fail($"ACS returned {(int)response.StatusCode}{hint} {Trim(content)}".TrimEnd());
        }

        AcsSmsSendResponse? parsed;
        try { parsed = JsonSerializer.Deserialize<AcsSmsSendResponse>(content); }
        catch { parsed = null; }
        var item = parsed?.Value.FirstOrDefault();
        if (item == null) return SmsSendResult.Fail("ACS accepted the request but returned no result item.");
        if (!item.Successful) return SmsSendResult.Fail($"ACS rejected the message for {recipient}: {item.HttpStatusCode} {item.ErrorMessage}".TrimEnd());

        return new SmsSendResult(true, item.MessageId, null, truncated);
    }

    private bool TryReserveSend(out TimeSpan retryIn)
    {
        lock (_sendLock)
        {
            var now = DateTime.UtcNow;
            while (_recentSends.Count > 0 && now - _recentSends.Peek() > TimeSpan.FromHours(1))
                _recentSends.Dequeue();
            if (_recentSends.Count >= MaxSendsPerHour)
            {
                retryIn = TimeSpan.FromHours(1) - (now - _recentSends.Peek());
                return false;
            }
            _recentSends.Enqueue(now);
            retryIn = TimeSpan.Zero;
            return true;
        }
    }

    public static bool IsE164(string value)
        => value.Length is >= 8 and <= 16 && value[0] == '+' && value.Skip(1).All(char.IsDigit);

    public static bool IsValidAlphanumericSender(string value)
        => value.Length is >= 1 and <= 11 && value.All(char.IsAsciiLetterOrDigit) && value.Any(char.IsAsciiLetter);

    private static string Trim(string content)
        => content.Length <= 200 ? content : content[..200] + "…";
}
