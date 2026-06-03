using FluentAssertions;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services.Security;

public class ActionPolicyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pks-actions-{Guid.NewGuid():N}.json");
    private readonly IActionCatalog _catalog = new ActionCatalog();

    private ActionPolicyStore NewStore() => new(_catalog, _path);
    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task UnsetAction_UsesCatalogDefault()
    {
        var store = NewStore();
        // vm.start defaults required; vm.stop defaults off.
        (await store.IsRequiredAsync(ActionIds.VmStart)).Should().BeTrue();
        (await store.IsRequiredAsync(ActionIds.VmStop)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task Set_PersistsAcrossReload()
    {
        var map = (await NewStore().GetAllAsync()).ToDictionary(kv => kv.Key, kv => kv.Value);
        map[ActionIds.VmStart] = false;
        map[ActionIds.VmStop] = true;
        await NewStore().SetAsync(map);

        var reloaded = NewStore();
        (await reloaded.IsRequiredAsync(ActionIds.VmStart)).Should().BeFalse();
        (await reloaded.IsRequiredAsync(ActionIds.VmStop)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task GetAll_CoversEveryCatalogAction()
    {
        var all = await NewStore().GetAllAsync();
        all.Keys.Should().BeEquivalentTo(_catalog.All.Select(a => a.Id));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("Speed", "Fast")]
    public async Task PolicyFile_IsOwnerOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        await NewStore().SetAsync(new Dictionary<string, bool> { [ActionIds.VmStart] = true });
        var mode = File.GetUnixFileMode(_path);
        (mode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)).Should().Be(UnixFileMode.None);
    }
}
