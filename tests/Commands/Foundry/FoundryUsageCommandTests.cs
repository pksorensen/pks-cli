using FluentAssertions;
using Moq;
using PKS.Commands.Foundry;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Foundry;

public class FoundryUsageCommandTests
{
    [Fact]
    [Trait("Category", "FoundryUsage")]
    public async Task ExecuteAsync_NotAuthenticated_PointsToFoundryInit()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync((FoundryStoredCredentials?)null);

        var billing = new Mock<IAzureBillingService>();
        var console = new TestConsole();

        var cmd = new FoundryUsageCommand(auth.Object, new AzureFoundryAuthConfig(), billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(1);
        console.Output.Should().Contain("pks foundry init");
    }

    [Fact]
    [Trait("Category", "FoundryUsage")]
    public async Task ExecuteAsync_UsesSavedResource_WhenConfirmed_AndNeverListsResources()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(new FoundryStoredCredentials
        {
            TenantId = "tenant",
            RefreshToken = "rt",
            SelectedSubscriptionId = "sub-1",
            SelectedSubscriptionName = "MySub",
            SelectedResourceGroup = "rg-1",
            SelectedResourceName = "foundry-1",
        });
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");

        var billing = new Mock<IAzureBillingService>();
        billing.Setup(x => x.QueryCostAsync("tok",
                "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/foundry-1",
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                CostGrouping.None, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult { Currency = "USD", TotalCost = 7.50m });
        billing.Setup(x => x.QueryCostAsync("tok",
                "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/foundry-1",
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                CostGrouping.Meter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult
            {
                Currency = "USD",
                TotalCost = 7.50m,
                Rows = new List<CostRow> { new("Speech to Text", 5m), new("Embeddings", 2.5m) }
            });

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("y");           // confirm saved resource
        console.Input.PushTextWithEnter("This month");  // time window

        var cmd = new FoundryUsageCommand(auth.Object, new AzureFoundryAuthConfig(), billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(0);
        auth.Verify(x => x.ListFoundryResourcesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        auth.Verify(x => x.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        console.Output.Should().Contain("foundry-1");
        console.Output.Should().Contain("Speech to Text");
        console.Output.Should().Contain("7.50");

        billing.Verify(x => x.QueryCostAsync(
            "tok",
            "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/foundry-1",
            It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CostGrouping>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    [Trait("Category", "FoundryUsage")]
    public async Task ExecuteAsync_NoSavedSelection_RepromptsResource()
    {
        var auth = new Mock<IAzureFoundryAuthService>();
        auth.Setup(x => x.GetStoredCredentialsAsync()).ReturnsAsync(new FoundryStoredCredentials
        {
            TenantId = "tenant",
            RefreshToken = "rt",
            // no SelectedSubscriptionId / SelectedResourceName / SelectedResourceGroup
        });
        auth.Setup(x => x.GetAccessTokenAsync(It.IsAny<string>())).ReturnsAsync("tok");
        auth.Setup(x => x.ListSubscriptionsAsync("tok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AzureSubscription>
            {
                new() { SubscriptionId = "sub-2", DisplayName = "Sub2" }
            });
        auth.Setup(x => x.ListFoundryResourcesAsync("tok", "sub-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CognitiveServicesAccount>
            {
                new()
                {
                    Name = "foo",
                    Id = "/subscriptions/sub-2/resourceGroups/rg-x/providers/Microsoft.CognitiveServices/accounts/foo",
                    Location = "eastus",
                    Kind = "AIServices",
                }
            });

        var billing = new Mock<IAzureBillingService>();
        billing.Setup(x => x.QueryCostAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.IsAny<CostGrouping>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CostQueryResult { Currency = "USD" });

        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("This month"); // time window only — no confirmation prompt since no saved data

        var cmd = new FoundryUsageCommand(auth.Object, new AzureFoundryAuthConfig(), billing.Object, console);
        var result = await cmd.ExecuteAsync();

        result.Should().Be(0);
        auth.Verify(x => x.ListSubscriptionsAsync("tok", It.IsAny<CancellationToken>()), Times.Once);
        auth.Verify(x => x.ListFoundryResourcesAsync("tok", "sub-2", It.IsAny<CancellationToken>()), Times.Once);
        billing.Verify(x => x.QueryCostAsync(
            "tok",
            "/subscriptions/sub-2/resourceGroups/rg-x/providers/Microsoft.CognitiveServices/accounts/foo",
            It.IsAny<DateTime>(), It.IsAny<DateTime>(),
            It.IsAny<CostGrouping>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
