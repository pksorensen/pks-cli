using FluentAssertions;
using Moq;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Azure;

public class AzureUsageCommandTests
{
    [Fact]
    [Trait("Category", "AzureUsage")]
    public async Task ExecuteAsync_NotAuthenticated_PointsToAzureInit()
    {
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(false);

        var billing = new Mock<IAzureBillingService>();
        var console = new TestConsole();

        var cmd = new AzureUsageCommand(auth.Object, billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(1);
        console.Output.Should().Contain("pks azure init");
    }

    [Fact]
    [Trait("Category", "AzureUsage")]
    public async Task ExecuteAsync_NoSubscriptions_ReturnsError()
    {
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        auth.Setup(x => x.ListSubscriptionsAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>());

        var billing = new Mock<IAzureBillingService>();
        var console = new TestConsole();

        var cmd = new AzureUsageCommand(auth.Object, billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(1);
        console.Output.Should().Contain("No Azure subscriptions");
    }

    [Fact]
    [Trait("Category", "AzureUsage")]
    public async Task ExecuteAsync_HappyPath_RendersCreditAndCostTables()
    {
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        auth.Setup(x => x.ListSubscriptionsAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-1", DisplayName = "Sponsorship-1" }
            });

        var billing = new Mock<IAzureBillingService>();
        billing.Setup(x => x.ListBillingProfilesAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BillingProfileRef>
            {
                new() { BillingAccountId = "ba-1", BillingProfileId = "bp-1", DisplayName = "Sponsor Profile" }
            });
        billing.Setup(x => x.GetCreditLotsAsync("tok", "ba-1", "bp-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CreditLot>
            {
                new()
                {
                    OriginalAmount = 5000m,
                    ClosedBalance = 1234.56m,
                    CreditCurrency = "USD",
                    Source = "Azure Sponsorship",
                    ExpirationDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                }
            });
        billing.Setup(x => x.QueryCostAsync("tok", "/subscriptions/sub-1",
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                CostGrouping.None, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult { Currency = "USD", TotalCost = 42.50m });
        billing.Setup(x => x.QueryCostAsync("tok", "/subscriptions/sub-1",
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                CostGrouping.Meter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult
            {
                Currency = "USD",
                TotalCost = 42.50m,
                Rows = new List<CostRow> { new("GPT-4 Tokens", 30m), new("Embeddings", 12.5m) }
            });

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("This month");

        var cmd = new AzureUsageCommand(auth.Object, billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(0);
        console.Output.Should().Contain("Sponsorship credits");
        console.Output.Should().Contain("1,234.56");
        console.Output.Should().Contain("Cost summary");
        console.Output.Should().Contain("42.50");
        console.Output.Should().Contain("Cost by meter");
        console.Output.Should().Contain("GPT-4 Tokens");
    }

    [Fact]
    [Trait("Category", "AzureUsage")]
    public async Task ExecuteAsync_NoBillingProfiles_SkipsCreditsButShowsCost()
    {
        var auth = new Mock<IAzureAuthService>();
        auth.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        auth.Setup(x => x.ListSubscriptionsAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-x", DisplayName = "Pay-As-You-Go" }
            });

        var billing = new Mock<IAzureBillingService>();
        billing.Setup(x => x.ListBillingProfilesAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<BillingProfileRef>());
        billing.Setup(x => x.QueryCostAsync("tok", It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CostGrouping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult { Currency = "USD", TotalCost = 5m });

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("This month");

        var cmd = new AzureUsageCommand(auth.Object, billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(0);
        console.Output.Should().NotContain("Sponsorship credits");
        console.Output.Should().Contain("No billing profiles");
        console.Output.Should().Contain("Cost summary");
        billing.Verify(x => x.GetCreditLotsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
