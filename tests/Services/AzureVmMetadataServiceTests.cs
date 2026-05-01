using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class AzureVmMetadataServiceTests : IDisposable
{
    private readonly string _tempFile;
    private readonly AzureVmMetadataService _sut;

    public AzureVmMetadataServiceTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"vms-test-{Guid.NewGuid():N}.json");
        _sut = new AzureVmMetadataService(_tempFile);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    [Trait("Category", "AzureVmMetadata")]
    public async Task ListAsync_WhenEmpty_ReturnsEmptyList()
    {
        var result = await _sut.ListAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "AzureVmMetadata")]
    public async Task SaveAndFind_RoundTrip_Works()
    {
        var record = new AzureVmRecord
        {
            VmName = "test-vm-1",
            SubscriptionId = "sub-abc",
            SubscriptionName = "My Sub",
            ResourceGroup = "my-rg",
            Location = "eastus",
            PublicIpAddress = "1.2.3.4",
            SshKeyPath = "/home/user/.pks-cli/keys/test-vm-1",
            IdleShutdownMinutes = 60,
            ScheduledShutdownUtc = "22:00",
            CreatedAt = DateTime.UtcNow
        };

        await _sut.SaveAsync(record);
        var found = await _sut.FindAsync("test-vm-1");

        found.Should().NotBeNull();
        found!.VmName.Should().Be("test-vm-1");
        found.SubscriptionId.Should().Be("sub-abc");
        found.ResourceGroup.Should().Be("my-rg");
        found.PublicIpAddress.Should().Be("1.2.3.4");
        found.IdleShutdownMinutes.Should().Be(60);
        found.ScheduledShutdownUtc.Should().Be("22:00");
    }

    [Fact]
    [Trait("Category", "AzureVmMetadata")]
    public async Task FindAsync_WhenNotExists_ReturnsNull()
    {
        var found = await _sut.FindAsync("nonexistent-vm");
        found.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureVmMetadata")]
    public async Task RemoveAsync_RemovesRecord()
    {
        var record = new AzureVmRecord { VmName = "vm-to-remove", SubscriptionId = "sub-1" };
        await _sut.SaveAsync(record);

        // Verify it exists
        var before = await _sut.FindAsync("vm-to-remove");
        before.Should().NotBeNull();

        // Remove it
        await _sut.RemoveAsync("vm-to-remove");
        var after = await _sut.FindAsync("vm-to-remove");
        after.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "AzureVmMetadata")]
    public async Task SaveAsync_WhenRecordExists_UpdatesInPlace()
    {
        var record = new AzureVmRecord { VmName = "updatable-vm", IdleShutdownMinutes = 60 };
        await _sut.SaveAsync(record);

        var updated = new AzureVmRecord { VmName = "updatable-vm", IdleShutdownMinutes = 30 };
        await _sut.SaveAsync(updated);

        var list = await _sut.ListAsync();
        list.Should().HaveCount(1);
        list[0].IdleShutdownMinutes.Should().Be(30);
    }
}
