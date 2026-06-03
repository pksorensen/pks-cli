using FluentAssertions;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

public class GuardedVmProviderTests
{
    private static AzureVmRecord Record() => new() { Provider = "scaleway", VmName = "h100" };

    private static (GuardedVmProvider guarded, Mock<IVmProvider> inner, Mock<IActionGuard> guard) Build()
    {
        var inner = new Mock<IVmProvider>();
        inner.SetupGet(x => x.ProviderKey).Returns("scaleway");
        inner.SetupGet(x => x.DisplayName).Returns("Scaleway");
        var guard = new Mock<IActionGuard>();
        return (new GuardedVmProvider(inner.Object, guard.Object), inner, guard);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public void ProviderKey_And_DisplayName_DelegateToInner()
    {
        var (guarded, _, _) = Build();
        guarded.ProviderKey.Should().Be("scaleway");
        guarded.DisplayName.Should().Be("Scaleway");
    }

    [Theory]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("destroy")]
    public async Task PowerOp_GatesThenForwards(string op)
    {
        var (guarded, inner, guard) = Build();
        var order = new List<string>();
        guard.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("guard")).Returns(Task.CompletedTask);
        inner.Setup(x => x.StartAsync(It.IsAny<AzureVmRecord>())).Callback(() => order.Add("inner")).Returns(Task.CompletedTask);
        inner.Setup(x => x.StopAsync(It.IsAny<AzureVmRecord>())).Callback(() => order.Add("inner")).Returns(Task.CompletedTask);
        inner.Setup(x => x.DestroyAsync(It.IsAny<AzureVmRecord>(), It.IsAny<Action<string>?>())).Callback(() => order.Add("inner")).Returns(Task.CompletedTask);

        var expected = op switch
        {
            "start" => ActionIds.VmStart,
            "stop" => ActionIds.VmStop,
            _ => ActionIds.VmDestroy,
        };

        switch (op)
        {
            case "start": await guarded.StartAsync(Record()); break;
            case "stop": await guarded.StopAsync(Record()); break;
            default: await guarded.DestroyAsync(Record()); break;
        }

        order.Should().Equal("guard", "inner"); // guard runs before the inner mutation
        guard.Verify(x => x.RequireAsync(It.Is<ActionRequest>(r => r.ActionId == expected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task DeniedStart_DoesNotCallInner()
    {
        var (guarded, inner, guard) = Build();
        guard.Setup(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ActionGuardDeniedException(ActionIds.VmStart, "denied"));

        var act = () => guarded.StartAsync(Record());
        await act.Should().ThrowAsync<ActionGuardDeniedException>();
        inner.Verify(x => x.StartAsync(It.IsAny<AzureVmRecord>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task ReadOps_AreNotGated()
    {
        var (guarded, inner, guard) = Build();
        inner.Setup(x => x.GetStatusAsync(It.IsAny<AzureVmRecord>())).ReturnsAsync(VmPowerState.Running);
        inner.Setup(x => x.GetPublicIpAsync(It.IsAny<AzureVmRecord>())).ReturnsAsync("1.2.3.4");
        inner.Setup(x => x.IsAuthenticatedAsync()).ReturnsAsync(true);
        inner.Setup(x => x.DiscoverAsync()).ReturnsAsync(Array.Empty<AzureVmRecord>());

        await guarded.GetStatusAsync(Record());
        await guarded.GetPublicIpAsync(Record());
        await guarded.IsAuthenticatedAsync();
        await guarded.DiscoverAsync();

        guard.Verify(x => x.RequireAsync(It.IsAny<ActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
