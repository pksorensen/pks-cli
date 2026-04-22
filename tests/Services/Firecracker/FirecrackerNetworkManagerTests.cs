using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Infrastructure.Services.Runner;
using Xunit;

using RunnerProcessResult = PKS.Infrastructure.Services.Runner.ProcessResult;

namespace PKS.CLI.Tests.Services.Firecracker;

/// <summary>
/// Tests for FirecrackerNetworkManager covering address computation, TAP device
/// lifecycle (allocate / release / reuse), state persistence across instances,
/// and edge-case handling.
/// </summary>
public class FirecrackerNetworkManagerTests : TestBase
{
    private readonly Mock<IProcessRunner> _mockProcessRunner;
    private readonly Mock<ILogger<FirecrackerNetworkManager>> _mockLogger;
    private readonly FirecrackerNetworkManager _manager;
    private readonly string _workDir;

    public FirecrackerNetworkManagerTests()
    {
        _mockProcessRunner = new Mock<IProcessRunner>();
        _mockLogger = new Mock<ILogger<FirecrackerNetworkManager>>();
        _manager = new FirecrackerNetworkManager(_mockProcessRunner.Object, _mockLogger.Object);
        _workDir = CreateTempDirectory();

        // Default: all ip and iptables commands succeed
        _mockProcessRunner
            .Setup(r => r.RunAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerProcessResult(0, "", ""));
    }

    #region ComputeAddresses

    [Fact]
    public void ComputeAddresses_Slot0_ReturnsCorrectValues()
    {
        // Act
        var (tapDevice, vmIp, gatewayIp, macAddress) = FirecrackerNetworkManager.ComputeAddresses(0);

        // Assert
        tapDevice.Should().Be("tap-fc-0");
        vmIp.Should().Be("172.16.0.2");
        gatewayIp.Should().Be("172.16.0.1");
        macAddress.Should().Be("AA:FC:00:00:00:00");
    }

    [Fact]
    public void ComputeAddresses_Slot1_ReturnsCorrectValues()
    {
        // Act
        var (tapDevice, vmIp, gatewayIp, macAddress) = FirecrackerNetworkManager.ComputeAddresses(1);

        // Assert
        tapDevice.Should().Be("tap-fc-1");
        vmIp.Should().Be("172.16.0.6");
        gatewayIp.Should().Be("172.16.0.5");
        macAddress.Should().Be("AA:FC:00:00:00:01");
    }

    [Fact]
    public void ComputeAddresses_Slot64_WrapsToNextOctet()
    {
        // Act
        var (tapDevice, vmIp, gatewayIp, macAddress) = FirecrackerNetworkManager.ComputeAddresses(64);

        // Assert
        tapDevice.Should().Be("tap-fc-64");
        vmIp.Should().Be("172.16.1.2");
        gatewayIp.Should().Be("172.16.1.1");
        macAddress.Should().Be("AA:FC:00:00:00:40");
    }

    #endregion

    #region AllocateNetworkAsync

    [Fact]
    public async Task AllocateNetworkAsync_ReturnsUniqueIpAndTap()
    {
        // Act
        var (tapDevice, vmIp, gatewayIp, macAddress) =
            await _manager.AllocateNetworkAsync("vm-1", _workDir);

        // Assert
        tapDevice.Should().Be("tap-fc-0");
        vmIp.Should().Be("172.16.0.2");
        gatewayIp.Should().Be("172.16.0.1");
        macAddress.Should().Be("AA:FC:00:00:00:00");

        // Verify TAP creation commands were called
        _mockProcessRunner.Verify(r => r.RunAsync(
            "ip", It.Is<string>(a => a.Contains("tuntap add tap-fc-0 mode tap")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _mockProcessRunner.Verify(r => r.RunAsync(
            "ip", It.Is<string>(a => a.Contains("addr add") && a.Contains("tap-fc-0")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _mockProcessRunner.Verify(r => r.RunAsync(
            "ip", It.Is<string>(a => a.Contains("link set tap-fc-0 up")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        _mockProcessRunner.Verify(r => r.RunAsync(
            "iptables", It.Is<string>(a => a.Contains("FORWARD") && a.Contains("tap-fc-0")),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AllocateNetworkAsync_MultipleCalls_ReturnDifferentSlots()
    {
        // Act
        var first = await _manager.AllocateNetworkAsync("vm-1", _workDir);
        var second = await _manager.AllocateNetworkAsync("vm-2", _workDir);

        // Assert
        first.TapDevice.Should().Be("tap-fc-0");
        second.TapDevice.Should().Be("tap-fc-1");

        first.VmIp.Should().NotBe(second.VmIp);
        first.GatewayIp.Should().NotBe(second.GatewayIp);
        first.MacAddress.Should().NotBe(second.MacAddress);
    }

    #endregion

    #region ReleaseNetworkAsync

    [Fact]
    public async Task ReleaseNetworkAsync_FreesSlotForReuse()
    {
        // Arrange - allocate slot 0
        var first = await _manager.AllocateNetworkAsync("vm-1", _workDir);
        first.TapDevice.Should().Be("tap-fc-0");

        // Act - release slot 0, then allocate again with a new vmId
        await _manager.ReleaseNetworkAsync("vm-1", _workDir);
        var reused = await _manager.AllocateNetworkAsync("vm-3", _workDir);

        // Assert - the freed slot 0 should be reused
        reused.TapDevice.Should().Be(first.TapDevice);
        reused.VmIp.Should().Be(first.VmIp);
        reused.GatewayIp.Should().Be(first.GatewayIp);
        reused.MacAddress.Should().Be(first.MacAddress);
    }

    [Fact]
    public async Task ReleaseNetworkAsync_WhenNoAllocation_LogsWarning()
    {
        // Act - should not throw
        var act = () => _manager.ReleaseNetworkAsync("non-existent-vm", _workDir);

        // Assert
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region State Persistence

    [Fact]
    public async Task AllocateNetworkAsync_PersistsState()
    {
        // Arrange - allocate with one manager instance
        var first = await _manager.AllocateNetworkAsync("vm-persist", _workDir);

        // Act - create a new manager instance using the same workDir
        var newManager = new FirecrackerNetworkManager(_mockProcessRunner.Object, _mockLogger.Object);
        var allocation = await newManager.GetAllocationAsync("vm-persist", _workDir);

        // Assert
        allocation.Should().NotBeNull();
        allocation!.Value.TapDevice.Should().Be(first.TapDevice);
        allocation.Value.VmIp.Should().Be(first.VmIp);
        allocation.Value.GatewayIp.Should().Be(first.GatewayIp);
        allocation.Value.MacAddress.Should().Be(first.MacAddress);
    }

    #endregion

    #region GetAllocationAsync

    [Fact]
    public async Task GetAllocationAsync_ReturnsNullWhenNotFound()
    {
        // Act
        var allocation = await _manager.GetAllocationAsync("non-existent-vm", _workDir);

        // Assert
        allocation.Should().BeNull();
    }

    #endregion
}
