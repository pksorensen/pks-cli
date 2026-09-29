using Xunit;
using Moq;
using FluentAssertions;
using PKS.Infrastructure.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Services;

[Trait("Category", "AzureVm")]
public class AzureVmDiskServiceTests
{
    private const string Sub = "sub-1";
    private const string Rg = "rg-1";
    private const string Vm = "vm1";
    private const string Token = "tok";
    private const string Compute = "https://management.azure.com/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute";
    private const string OldDiskId = "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/disks/vm1_OsDisk_1_abc";
    private const string SnapshotId = "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Compute/snapshots/vm1-baseline";

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _handler;
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = new();

        public MockHttpMessageHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> handler) => _handler = handler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.ToString(), body));
            return _handler(request, body);
        }
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(node.ToJsonString()) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

    private static JsonObject VmJson() => new()
    {
        ["properties"] = new JsonObject
        {
            ["provisioningState"] = "Succeeded",
            ["storageProfile"] = new JsonObject
            {
                ["osDisk"] = new JsonObject { ["managedDisk"] = new JsonObject { ["id"] = OldDiskId } },
            },
        },
    };

    private static JsonObject DiskJson() => new()
    {
        ["name"] = "vm1_OsDisk_1_abc",
        ["location"] = "swedencentral",
        ["sku"] = new JsonObject { ["name"] = "Premium_LRS" },
        ["tags"] = new JsonObject { ["owner"] = "poul" },
        ["properties"] = new JsonObject { ["provisioningState"] = "Succeeded" },
    };

    private static JsonObject SnapshotJson() => new()
    {
        ["id"] = SnapshotId,
        ["name"] = "vm1-baseline",
        ["properties"] = new JsonObject
        {
            ["provisioningState"] = "Succeeded",
            ["timeCreated"] = "2026-09-28T10:00:00Z",
            ["creationData"] = new JsonObject { ["sourceResourceId"] = OldDiskId },
        },
    };

    private static AzureVmOsDisk Disk(string name = "vm1_OsDisk_1_abc", Dictionary<string, string>? tags = null,
        IReadOnlyList<string>? zones = null, JsonNode? security = null)
        => new(OldDiskId, name, "swedencentral", "Premium_LRS", zones ?? Array.Empty<string>(),
            tags ?? new Dictionary<string, string>(), security);

    // ── request shapes ──────────────────────────────

    [Fact]
    public void BaselineBody_IsIncrementalCopyOfTheOsDisk_WithPksTags()
    {
        var body = AzureVmDiskService.BaselineBody(Disk(), Vm);

        ((string?)body["location"]).Should().Be("swedencentral");
        ((string?)body["sku"]!["name"]).Should().Be("Standard_LRS");
        ((bool?)body["properties"]!["incremental"]).Should().BeTrue();
        ((string?)body["properties"]!["creationData"]!["createOption"]).Should().Be("Copy");
        ((string?)body["properties"]!["creationData"]!["sourceResourceId"]).Should().Be(OldDiskId);
        ((string?)body["tags"]!["pks-managed"]).Should().Be("true");
        ((string?)body["tags"]!["pks-vm-name"]).Should().Be(Vm);
        ((string?)body["tags"]!["pks-role"]).Should().Be("baseline");
    }

    [Fact]
    public void DiskFromSnapshotBody_CopiesSkuTagsAndLocation_OmitsZonesAndSecurityWhenAbsent()
    {
        var body = AzureVmDiskService.DiskFromSnapshotBody(Disk(tags: new() { ["owner"] = "poul" }), SnapshotId, Vm);

        ((string?)body["location"]).Should().Be("swedencentral");
        ((string?)body["sku"]!["name"]).Should().Be("Premium_LRS");
        ((string?)body["properties"]!["creationData"]!["createOption"]).Should().Be("Copy");
        ((string?)body["properties"]!["creationData"]!["sourceResourceId"]).Should().Be(SnapshotId);
        ((string?)body["tags"]!["owner"]).Should().Be("poul");
        ((string?)body["tags"]!["pks-managed"]).Should().Be("true");
        ((string?)body["tags"]!["pks-vm-name"]).Should().Be(Vm);
        body.ContainsKey("zones").Should().BeFalse();
        body["properties"]!.AsObject().ContainsKey("securityProfile").Should().BeFalse();
    }

    [Fact]
    public void DiskFromSnapshotBody_CarriesZonesAndSecurityProfile()
    {
        var security = new JsonObject { ["securityType"] = "TrustedLaunch" };
        var body = AzureVmDiskService.DiskFromSnapshotBody(Disk(zones: new[] { "2" }, security: security), SnapshotId, Vm);

        body["zones"]!.AsArray().Select(z => (string?)z).Should().Equal("2");
        ((string?)body["properties"]!["securityProfile"]!["securityType"]).Should().Be("TrustedLaunch");
    }

    [Fact]
    public void SwapOsDiskBody_PointsOsDiskAtTheNewDisk()
    {
        var body = AzureVmDiskService.SwapOsDiskBody("/x/disks/vm1-osdisk-1", "vm1-osdisk-1");

        var osDisk = body["properties"]!["storageProfile"]!["osDisk"]!;
        ((string?)osDisk["name"]).Should().Be("vm1-osdisk-1");
        ((string?)osDisk["managedDisk"]!["id"]).Should().Be("/x/disks/vm1-osdisk-1");
    }

    [Fact]
    public void Names_FollowTheConvention()
    {
        AzureVmDiskService.BaselineName(Vm).Should().Be("vm1-baseline");
        AzureVmDiskService.NewDiskName(Vm, new DateTime(2026, 9, 28, 13, 4, 5, DateTimeKind.Utc)).Should().Be("vm1-osdisk-20260928130405");
    }

    [Theory]
    [InlineData("vm1_OsDisk_1_abc", null, true)]
    [InlineData("vm1-osdisk-20260928130405", null, true)]
    [InlineData("shared-data-disk", "vm1", true)]
    [InlineData("shared-data-disk", "other-vm", false)]
    [InlineData("vm10_OsDisk_1_abc", null, false)]
    [InlineData("someone-elses-disk", null, false)]
    public void IsDisposableOldDisk_OnlyForThisVmsDisks(string name, string? ownerTag, bool expected)
    {
        var tags = ownerTag == null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["pks-vm-name"] = ownerTag };
        AzureVmDiskService.IsDisposableOldDisk(Disk(name, tags), Vm).Should().Be(expected);
    }

    // ── flows ───────────────────────────────────────

    [Fact]
    public async Task CreateBaselineAsync_PutsIncrementalSnapshotOfTheCurrentOsDisk()
    {
        var created = false;
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/virtualMachines/vm1")) return Json(VmJson());
            if (url.Contains("/disks/vm1_OsDisk_1_abc")) return Json(DiskJson());
            if (url.Contains("/snapshots/vm1-baseline"))
            {
                if (req.Method == HttpMethod.Put) { created = true; return Json(SnapshotJson(), HttpStatusCode.Created); }
                return created ? Json(SnapshotJson()) : NotFound();
            }
            throw new InvalidOperationException($"unexpected {req.Method} {url}");
        });
        var vm = new Mock<IAzureVmService>(MockBehavior.Strict);
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        var baseline = await svc.CreateBaselineAsync(Token, Sub, Rg, Vm);

        baseline.Name.Should().Be("vm1-baseline");
        baseline.SourceDiskId.Should().Be(OldDiskId);
        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        put.Url.Should().Be($"{Compute}/snapshots/vm1-baseline?api-version=2024-03-02");
        var body = JsonNode.Parse(put.Body!)!;
        ((bool?)body["properties"]!["incremental"]).Should().BeTrue();
        ((string?)body["properties"]!["creationData"]!["sourceResourceId"]).Should().Be(OldDiskId);
        handler.Requests.Should().Contain(r => r.Url.StartsWith($"{Compute}/virtualMachines/vm1?api-version=2023-09-01"));
    }

    [Fact]
    public async Task CreateBaselineAsync_DeletesAnExistingBaselineFirst()
    {
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/virtualMachines/vm1")) return Json(VmJson());
            if (url.Contains("/disks/vm1_OsDisk_1_abc")) return Json(DiskJson());
            if (url.Contains("/snapshots/vm1-baseline")) return Json(SnapshotJson());
            throw new InvalidOperationException($"unexpected {req.Method} {url}");
        });
        var vm = new Mock<IAzureVmService>();
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        await svc.CreateBaselineAsync(Token, Sub, Rg, Vm);

        vm.Verify(v => v.DeleteResourceAsync(Token, $"{Compute}/snapshots/vm1-baseline?api-version=2024-03-02", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetToBaselineAsync_WithoutBaseline_PointsAtVmSnapshot_AndTouchesNothing()
    {
        var handler = new MockHttpMessageHandler((req, _) =>
            req.RequestUri!.ToString().Contains("/snapshots/") ? NotFound() : throw new InvalidOperationException("unexpected"));
        var vm = new Mock<IAzureVmService>(MockBehavior.Strict);
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        var act = () => svc.ResetToBaselineAsync(Token, Sub, Rg, Vm);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pks vm snapshot vm1*");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task ResetToBaselineAsync_CreatesDisk_Deallocates_Swaps_Starts_DeletesOldDisk()
    {
        var order = new List<string>();
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Put && url.Contains("/disks/vm1-osdisk-")) { order.Add("create-disk"); return Json(new JsonObject(), HttpStatusCode.Created); }
            if (req.Method == HttpMethod.Patch && url.Contains("/virtualMachines/vm1")) { order.Add("swap"); return Json(new JsonObject()); }
            if (url.Contains("/virtualMachines/vm1")) return Json(VmJson());
            if (url.Contains("/disks/vm1_OsDisk_1_abc")) return Json(DiskJson());
            if (url.Contains("/disks/vm1-osdisk-")) return Json(DiskJson());
            if (url.Contains("/snapshots/vm1-baseline")) return Json(SnapshotJson());
            throw new InvalidOperationException($"unexpected {req.Method} {url}");
        });
        var power = "running";
        var vm = new Mock<IAzureVmService>();
        vm.Setup(v => v.DeallocateVmAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("deallocate"); power = "deallocated"; }).Returns(Task.CompletedTask);
        vm.Setup(v => v.StartVmAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("start"); power = "running"; }).Returns(Task.CompletedTask);
        vm.Setup(v => v.GetVmStatusAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>())).ReturnsAsync(() => power);
        vm.Setup(v => v.DeleteResourceAsync(Token, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, url, _) => order.Add($"delete {url}")).Returns(Task.CompletedTask);
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        var result = await svc.ResetToBaselineAsync(Token, Sub, Rg, Vm);

        order.Should().Equal("create-disk", "deallocate", "swap", "start",
            $"delete https://management.azure.com{OldDiskId}?api-version=2024-03-02");
        result.OldDiskDeleted.Should().BeTrue();
        result.NewDiskName.Should().StartWith("vm1-osdisk-");

        var create = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        ((string?)JsonNode.Parse(create.Body!)!["properties"]!["creationData"]!["sourceResourceId"]).Should().Be(SnapshotId);
        var swap = JsonNode.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Patch).Body!)!;
        ((string?)swap["properties"]!["storageProfile"]!["osDisk"]!["managedDisk"]!["id"])
            .Should().Be($"/subscriptions/{Sub}/resourceGroups/{Rg}/providers/Microsoft.Compute/disks/{result.NewDiskName}");
    }

    [Fact]
    public async Task ResetToBaselineAsync_SwapRefused_DeletesNewDisk_RestartsVm_KeepsOldDisk()
    {
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Put && url.Contains("/disks/vm1-osdisk-")) return Json(new JsonObject(), HttpStatusCode.Created);
            if (req.Method == HttpMethod.Patch) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"error\":{\"code\":\"AuthorizationFailed\"}}") };
            if (url.Contains("/virtualMachines/vm1")) return Json(VmJson());
            if (url.Contains("/disks/")) return Json(DiskJson());
            if (url.Contains("/snapshots/vm1-baseline")) return Json(SnapshotJson());
            throw new InvalidOperationException($"unexpected {req.Method} {url}");
        });
        var vm = new Mock<IAzureVmService>();
        vm.Setup(v => v.GetVmStatusAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>())).ReturnsAsync("deallocated");
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        var act = () => svc.ResetToBaselineAsync(Token, Sub, Rg, Vm);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        vm.Verify(v => v.DeleteResourceAsync(Token, It.Is<string>(u => u.Contains("/disks/vm1-osdisk-")), It.IsAny<CancellationToken>()), Times.Once);
        vm.Verify(v => v.DeleteResourceAsync(Token, It.Is<string>(u => u.Contains("vm1_OsDisk_1_abc")), It.IsAny<CancellationToken>()), Times.Never);
        vm.Verify(v => v.StartVmAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResetToBaselineAsync_ForeignOldDisk_IsKept()
    {
        var foreign = DiskJson();
        foreign["name"] = "hand-made-disk";
        var handler = new MockHttpMessageHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method != HttpMethod.Get) return Json(new JsonObject(), HttpStatusCode.OK);
            if (url.Contains("/virtualMachines/vm1")) return Json(VmJson());
            if (url.Contains("/disks/vm1_OsDisk_1_abc")) return Json(foreign);
            if (url.Contains("/disks/")) return Json(DiskJson());
            if (url.Contains("/snapshots/vm1-baseline")) return Json(SnapshotJson());
            throw new InvalidOperationException($"unexpected {req.Method} {url}");
        });
        var power = "running";
        var vm = new Mock<IAzureVmService>();
        vm.Setup(v => v.DeallocateVmAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>())).Callback(() => power = "deallocated").Returns(Task.CompletedTask);
        vm.Setup(v => v.StartVmAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>())).Callback(() => power = "running").Returns(Task.CompletedTask);
        vm.Setup(v => v.GetVmStatusAsync(Token, Sub, Rg, Vm, It.IsAny<CancellationToken>())).ReturnsAsync(() => power);
        var svc = new AzureVmDiskService(new HttpClient(handler), vm.Object, TimeSpan.Zero);

        var result = await svc.ResetToBaselineAsync(Token, Sub, Rg, Vm);

        result.OldDiskDeleted.Should().BeFalse();
        result.OldDiskName.Should().Be("hand-made-disk");
        result.OldDiskKeptReason.Should().NotBeNullOrEmpty();
        vm.Verify(v => v.DeleteResourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
