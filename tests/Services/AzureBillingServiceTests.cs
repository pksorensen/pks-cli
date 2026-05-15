using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class AzureBillingServiceTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request);
    }

    private static AzureBillingService Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var client = new HttpClient(new MockHttpMessageHandler(handler));
        return new AzureBillingService(client, new Mock<ILogger<AzureBillingService>>().Object);
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task ListBillingProfilesAsync_BuildsCorrectUrl_AndParsesProfiles()
    {
        string? capturedUrl = null;
        var svc = Create(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            req.Headers.Authorization!.Scheme.Should().Be("Bearer");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [{
                    "name": "ba-1",
                    "properties": {
                      "displayName": "Sponsor Account",
                      "billingProfiles": {
                        "value": [{
                          "name": "PBFV-1",
                          "properties": { "displayName": "Profile A", "currency": "USD" }
                        }]
                      }
                    }
                  }]
                }
                """)
            });
        });

        var profiles = await svc.ListBillingProfilesAsync("tok");

        capturedUrl.Should().Contain("/providers/Microsoft.Billing/billingAccounts");
        capturedUrl.Should().Contain("$expand=billingProfiles");
        capturedUrl.Should().Contain("api-version=2019-10-01-preview");

        profiles.Should().HaveCount(1);
        profiles[0].BillingAccountId.Should().Be("ba-1");
        profiles[0].BillingProfileId.Should().Be("PBFV-1");
        profiles[0].DisplayName.Should().Be("Profile A");
        profiles[0].Currency.Should().Be("USD");
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task ListBillingProfilesAsync_403_ReturnsEmptyList()
    {
        var svc = Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{}")
        }));

        var profiles = await svc.ListBillingProfilesAsync("tok");

        profiles.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task GetCreditLotsAsync_BuildsCorrectUrl_AndParsesLots()
    {
        string? capturedUrl = null;
        var svc = Create(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "value": [{
                    "properties": {
                      "originalAmount": { "value": 5000 },
                      "closedBalance": { "value": 1234.56 },
                      "creditCurrency": "USD",
                      "expirationDate": "2026-12-31T00:00:00Z",
                      "source": "Azure Sponsorship",
                      "isEstimatedBalance": true
                    }
                  }]
                }
                """)
            });
        });

        var lots = await svc.GetCreditLotsAsync("tok", "ba-1", "PBFV-1");

        capturedUrl.Should().Contain("/billingAccounts/ba-1/billingProfiles/PBFV-1/providers/Microsoft.Consumption/lots");
        capturedUrl.Should().Contain("api-version=2023-03-01");

        lots.Should().HaveCount(1);
        lots[0].OriginalAmount.Should().Be(5000m);
        lots[0].ClosedBalance.Should().Be(1234.56m);
        lots[0].CreditCurrency.Should().Be("USD");
        lots[0].Source.Should().Be("Azure Sponsorship");
        lots[0].IsEstimatedBalance.Should().BeTrue();
        lots[0].ExpirationDate.Should().Be(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task GetCreditLotsAsync_404_ReturnsEmptyList()
    {
        var svc = Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        }));

        var lots = await svc.GetCreditLotsAsync("tok", "ba", "bp");

        lots.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task QueryCostAsync_AtSubscriptionScope_BuildsCorrectUrlAndBody_GroupedByMeter()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        var svc = Create(async req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "properties": {
                    "columns": [
                      { "name": "Cost", "type": "Number" },
                      { "name": "Meter", "type": "String" },
                      { "name": "Currency", "type": "String" }
                    ],
                    "rows": [
                      [12.34, "GPT-4 Tokens", "USD"],
                      [5.00, "Embeddings", "USD"]
                    ]
                  }
                }
                """)
            };
        });

        var result = await svc.QueryCostAsync(
            "tok",
            "/subscriptions/00000000-0000-0000-0000-000000000001",
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc),
            CostGrouping.Meter);

        capturedUrl.Should().Be(
            "https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.CostManagement/query?api-version=2024-08-01");

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("ActualCost");
        doc.RootElement.GetProperty("timeframe").GetString().Should().Be("Custom");
        doc.RootElement.GetProperty("timePeriod").GetProperty("from").GetString().Should().Be("2026-05-01T00:00:00Z");
        doc.RootElement.GetProperty("timePeriod").GetProperty("to").GetString().Should().Be("2026-05-12T23:59:59Z");
        doc.RootElement.GetProperty("dataset").GetProperty("aggregation").GetProperty("totalCost").GetProperty("function").GetString().Should().Be("Sum");
        doc.RootElement.GetProperty("dataset").GetProperty("grouping")[0].GetProperty("name").GetString().Should().Be("Meter");

        result.TotalCost.Should().Be(17.34m);
        result.Currency.Should().Be("USD");
        result.Rows.Should().HaveCount(2);
        result.Rows.Should().Contain(r => r.Group == "GPT-4 Tokens" && r.Cost == 12.34m);
        result.Rows.Should().Contain(r => r.Group == "Embeddings" && r.Cost == 5.00m);
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task QueryCostAsync_AtResourceScope_BuildsCorrectUrl()
    {
        string? capturedUrl = null;
        var svc = Create(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "properties": { "columns": [], "rows": [] } }""")
            });
        });

        await svc.QueryCostAsync(
            "tok",
            "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry-1",
            DateTime.UtcNow.AddDays(-7),
            DateTime.UtcNow,
            CostGrouping.None);

        capturedUrl.Should().Contain("/subscriptions/sub/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/foundry-1/providers/Microsoft.CostManagement/query");
        capturedUrl.Should().Contain("api-version=2024-08-01");
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task QueryDailyCostAsync_BuildsDailyBody_AndParsesNumericUsageDate()
    {
        string? capturedBody = null;
        var svc = Create(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "properties": {
                    "columns": [
                      { "name": "Cost", "type": "Number" },
                      { "name": "UsageDate", "type": "Number" },
                      { "name": "Currency", "type": "String" }
                    ],
                    "rows": [
                      [10.5, 20260511, "USD"],
                      [12.0, 20260512, "USD"],
                      [3.25, 20260513, "USD"]
                    ]
                  }
                }
                """)
            };
        });

        var result = await svc.QueryDailyCostAsync(
            "tok",
            "/subscriptions/sub",
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc));

        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("dataset").GetProperty("granularity").GetString().Should().Be("Daily");
        doc.RootElement.GetProperty("dataset").TryGetProperty("grouping", out _).Should().BeFalse();

        result.Currency.Should().Be("USD");
        result.Points.Should().HaveCount(3);
        result.Points[0].Date.Should().Be(new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc));
        result.Points[0].Cost.Should().Be(10.5m);
        result.Points[2].Date.Should().Be(new DateTime(2026, 5, 13, 0, 0, 0, DateTimeKind.Utc));
        result.Points[2].Cost.Should().Be(3.25m);
    }

    [Fact]
    [Trait("Category", "AzureBilling")]
    public async Task QueryCostAsync_GroupingNone_OmitsGroupingArrayFromBody()
    {
        string? capturedBody = null;
        var svc = Create(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "properties": { "columns": [], "rows": [] } }""")
            };
        });

        await svc.QueryCostAsync("tok", "/subscriptions/sub", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, CostGrouping.None);

        using var doc = JsonDocument.Parse(capturedBody!);
        doc.RootElement.GetProperty("dataset").TryGetProperty("grouping", out _).Should().BeFalse();
    }
}
