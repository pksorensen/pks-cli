using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Reads sponsorship credit balance and Cost Management consumption data via Azure ARM REST.
/// </summary>
public interface IAzureBillingService
{
    Task<List<BillingProfileRef>> ListBillingProfilesAsync(string accessToken, CancellationToken ct = default);

    Task<List<CreditLot>> GetCreditLotsAsync(string accessToken, string billingAccountId, string billingProfileId, CancellationToken ct = default);

    /// <summary>
    /// Query aggregated cost at any ARM scope (subscription, resource group, or single resource).
    /// </summary>
    /// <param name="scope">ARM scope path, e.g. "/subscriptions/{id}" or "/subscriptions/{id}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{name}".</param>
    Task<CostQueryResult> QueryCostAsync(string accessToken, string scope, DateTime startUtc, DateTime endUtc, CostGrouping grouping, CancellationToken ct = default);

    /// <summary>
    /// Query daily-granularity cost at the given ARM scope. One point per day.
    /// </summary>
    Task<DailyCostResult> QueryDailyCostAsync(string accessToken, string scope, DateTime startUtc, DateTime endUtc, CancellationToken ct = default);
}

public class AzureBillingService : IAzureBillingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureBillingService> _logger;

    private const string BillingApiVersion = "2019-10-01-preview";
    private const string ConsumptionApiVersion = "2023-03-01";
    private const string CostManagementApiVersion = "2024-08-01";

    public AzureBillingService(HttpClient httpClient, ILogger<AzureBillingService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<BillingProfileRef>> ListBillingProfilesAsync(string accessToken, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/providers/Microsoft.Billing/billingAccounts?$expand=billingProfiles&api-version={BillingApiVersion}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var resp = await _httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // 403/404 — caller's identity has no billing-profile reader role. Treat as "no profiles visible".
        if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.NotFound)
            return new List<BillingProfileRef>();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} listing billing accounts: {body}", null, resp.StatusCode);

        var results = new List<BillingProfileRef>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var account in accounts.EnumerateArray())
        {
            var accountId = account.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var accountDisplay = string.Empty;
            if (!account.TryGetProperty("properties", out var accProps)) continue;
            if (accProps.TryGetProperty("displayName", out var d)) accountDisplay = d.GetString() ?? string.Empty;
            if (!accProps.TryGetProperty("billingProfiles", out var billing)) continue;
            if (!billing.TryGetProperty("value", out var profiles) || profiles.ValueKind != JsonValueKind.Array) continue;

            foreach (var profile in profiles.EnumerateArray())
            {
                var profileId = profile.TryGetProperty("name", out var pn) ? pn.GetString() ?? string.Empty : string.Empty;
                var profileDisplay = string.Empty;
                var currency = string.Empty;
                if (profile.TryGetProperty("properties", out var pp))
                {
                    if (pp.TryGetProperty("displayName", out var pd)) profileDisplay = pd.GetString() ?? string.Empty;
                    if (pp.TryGetProperty("currency", out var pc)) currency = pc.GetString() ?? string.Empty;
                }
                results.Add(new BillingProfileRef
                {
                    BillingAccountId = accountId,
                    BillingAccountDisplayName = accountDisplay,
                    BillingProfileId = profileId,
                    DisplayName = profileDisplay,
                    Currency = currency,
                });
            }
        }
        return results;
    }

    public async Task<List<CreditLot>> GetCreditLotsAsync(string accessToken, string billingAccountId, string billingProfileId, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{billingProfileId}/providers/Microsoft.Consumption/lots?api-version={ConsumptionApiVersion}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var resp = await _httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden)
            return new List<CreditLot>();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} listing credit lots: {body}", null, resp.StatusCode);

        var results = new List<CreditLot>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var lots) || lots.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var lot in lots.EnumerateArray())
        {
            if (!lot.TryGetProperty("properties", out var props)) continue;
            var item = new CreditLot();
            if (props.TryGetProperty("originalAmount", out var oa) && oa.TryGetProperty("value", out var oav) && oav.ValueKind == JsonValueKind.Number)
                item.OriginalAmount = oav.GetDecimal();
            if (props.TryGetProperty("closedBalance", out var cb) && cb.TryGetProperty("value", out var cbv) && cbv.ValueKind == JsonValueKind.Number)
                item.ClosedBalance = cbv.GetDecimal();
            if (props.TryGetProperty("creditCurrency", out var cc) && cc.ValueKind == JsonValueKind.String)
                item.CreditCurrency = cc.GetString() ?? string.Empty;
            if (props.TryGetProperty("startDate", out var sd) && sd.ValueKind == JsonValueKind.String)
                item.StartDate = sd.GetDateTime();
            if (props.TryGetProperty("expirationDate", out var ed) && ed.ValueKind == JsonValueKind.String)
                item.ExpirationDate = ed.GetDateTime();
            if (props.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.String)
                item.Source = so.GetString() ?? string.Empty;
            if (props.TryGetProperty("isEstimatedBalance", out var ie) &&
                (ie.ValueKind == JsonValueKind.True || ie.ValueKind == JsonValueKind.False))
                item.IsEstimatedBalance = ie.GetBoolean();
            results.Add(item);
        }
        return results;
    }

    public async Task<CostQueryResult> QueryCostAsync(string accessToken, string scope, DateTime startUtc, DateTime endUtc, CostGrouping grouping, CancellationToken ct = default)
    {
        var scopePath = scope.StartsWith("/") ? scope : "/" + scope;
        var url = $"https://management.azure.com{scopePath}/providers/Microsoft.CostManagement/query?api-version={CostManagementApiVersion}";

        var from = startUtc.ToString("yyyy-MM-ddT00:00:00Z");
        var to = endUtc.ToString("yyyy-MM-ddT23:59:59Z");

        object body;
        if (grouping == CostGrouping.None)
        {
            body = new
            {
                type = "ActualCost",
                timeframe = "Custom",
                timePeriod = new { from, to },
                dataset = new
                {
                    granularity = "None",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    }
                }
            };
        }
        else
        {
            var dim = grouping switch
            {
                CostGrouping.Meter => "Meter",
                CostGrouping.ServiceName => "ServiceName",
                _ => "Meter"
            };
            body = new
            {
                type = "ActualCost",
                timeframe = "Custom",
                timePeriod = new { from, to },
                dataset = new
                {
                    granularity = "None",
                    aggregation = new
                    {
                        totalCost = new { name = "Cost", function = "Sum" }
                    },
                    grouping = new[] { new { type = "Dimension", name = dim } }
                }
            };
        }

        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} querying cost: {content}", null, resp.StatusCode);

        return ParseCostQueryResult(content);
    }

    public async Task<DailyCostResult> QueryDailyCostAsync(string accessToken, string scope, DateTime startUtc, DateTime endUtc, CancellationToken ct = default)
    {
        var scopePath = scope.StartsWith("/") ? scope : "/" + scope;
        var url = $"https://management.azure.com{scopePath}/providers/Microsoft.CostManagement/query?api-version={CostManagementApiVersion}";

        var from = startUtc.ToString("yyyy-MM-ddT00:00:00Z");
        var to = endUtc.ToString("yyyy-MM-ddT23:59:59Z");

        var body = new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new { from, to },
            dataset = new
            {
                granularity = "Daily",
                aggregation = new
                {
                    totalCost = new { name = "Cost", function = "Sum" }
                }
            }
        };

        var json = JsonSerializer.Serialize(body);
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await _httpClient.SendAsync(req, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} querying daily cost: {content}", null, resp.StatusCode);

        return ParseDailyCostResult(content);
    }

    private static DailyCostResult ParseDailyCostResult(string json)
    {
        var result = new DailyCostResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var props))
            return result;

        int costIdx = -1, dateIdx = -1, currencyIdx = -1;
        if (props.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var col in columns.EnumerateArray())
            {
                var name = col.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, "Cost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "PreTaxCost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "CostUSD", StringComparison.OrdinalIgnoreCase))
                {
                    if (costIdx < 0) costIdx = i;
                }
                else if (string.Equals(name, "UsageDate", StringComparison.OrdinalIgnoreCase))
                {
                    dateIdx = i;
                }
                else if (string.Equals(name, "Currency", StringComparison.OrdinalIgnoreCase))
                {
                    currencyIdx = i;
                }
                i++;
            }
        }

        if (!props.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || costIdx < 0 || dateIdx < 0)
            return result;

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var arr = row.EnumerateArray().ToArray();
            if (costIdx >= arr.Length || dateIdx >= arr.Length) continue;

            decimal cost = arr[costIdx].ValueKind == JsonValueKind.Number ? arr[costIdx].GetDecimal() : 0m;
            DateTime? date = ParseUsageDate(arr[dateIdx]);
            if (date is null) continue;

            if (currencyIdx >= 0 && currencyIdx < arr.Length &&
                string.IsNullOrEmpty(result.Currency) &&
                arr[currencyIdx].ValueKind == JsonValueKind.String)
            {
                result.Currency = arr[currencyIdx].GetString() ?? string.Empty;
            }

            result.Points.Add(new DailyCostPoint(date.Value, cost));
        }

        result.Points = result.Points.OrderBy(p => p.Date).ToList();
        return result;
    }

    private static DateTime? ParseUsageDate(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            // Cost Management returns UsageDate as yyyyMMdd integer (e.g. 20260513)
            var n = el.GetInt32();
            var y = n / 10000;
            var m = n / 100 % 100;
            var d = n % 100;
            if (y >= 2000 && m is >= 1 and <= 12 && d is >= 1 and <= 31)
                return new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc);
            return null;
        }
        if (el.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(el.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
        }
        return null;
    }

    private static CostQueryResult ParseCostQueryResult(string json)
    {
        var result = new CostQueryResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("properties", out var props))
            return result;

        int costIdx = -1, currencyIdx = -1, groupIdx = -1;
        if (props.TryGetProperty("columns", out var columns) && columns.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var col in columns.EnumerateArray())
            {
                var name = col.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.Equals(name, "Cost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "PreTaxCost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "CostUSD", StringComparison.OrdinalIgnoreCase))
                {
                    if (costIdx < 0) costIdx = i;
                }
                else if (string.Equals(name, "Currency", StringComparison.OrdinalIgnoreCase))
                {
                    currencyIdx = i;
                }
                else if (string.Equals(name, "Meter", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(name, "ServiceName", StringComparison.OrdinalIgnoreCase))
                {
                    groupIdx = i;
                }
                i++;
            }
        }

        if (!props.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return result;

        decimal total = 0m;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var arr = row.EnumerateArray().ToArray();
            if (costIdx < 0 || costIdx >= arr.Length) continue;

            decimal cost = arr[costIdx].ValueKind == JsonValueKind.Number ? arr[costIdx].GetDecimal() : 0m;
            total += cost;

            if (currencyIdx >= 0 && currencyIdx < arr.Length &&
                string.IsNullOrEmpty(result.Currency) &&
                arr[currencyIdx].ValueKind == JsonValueKind.String)
            {
                result.Currency = arr[currencyIdx].GetString() ?? string.Empty;
            }

            var group = groupIdx >= 0 && groupIdx < arr.Length
                ? (arr[groupIdx].ValueKind == JsonValueKind.String ? arr[groupIdx].GetString() ?? "(unknown)" : "(unknown)")
                : "Total";
            result.Rows.Add(new CostRow(group, cost));
        }
        result.TotalCost = total;
        return result;
    }
}
