using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

public interface IAzureVmMetadataService
{
    Task<List<AzureVmRecord>> ListAsync();
    Task SaveAsync(AzureVmRecord record);
    Task<AzureVmRecord?> FindAsync(string vmName);
    Task RemoveAsync(string vmName);
}

public class AzureVmMetadataService : IAzureVmMetadataService
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AzureVmMetadataService(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pks-cli", "vms.json");
    }

    private async Task<AzureVmStore> LoadStoreAsync()
    {
        if (!File.Exists(_storePath))
            return new AzureVmStore();

        try
        {
            var json = await File.ReadAllTextAsync(_storePath);
            return JsonSerializer.Deserialize<AzureVmStore>(json, JsonOptions)
                ?? new AzureVmStore();
        }
        catch (JsonException)
        {
            return new AzureVmStore();
        }
    }

    private async Task SaveStoreAsync(AzureVmStore store)
    {
        var dir = Path.GetDirectoryName(_storePath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(store, JsonOptions);
        await File.WriteAllTextAsync(_storePath, json);
    }

    public async Task<List<AzureVmRecord>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadStoreAsync();
            return store.Vms;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AzureVmRecord record)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadStoreAsync();
            var existing = store.Vms.FindIndex(v =>
                string.Equals(v.VmName, record.VmName, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                store.Vms[existing] = record;
            else
                store.Vms.Add(record);

            await SaveStoreAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AzureVmRecord?> FindAsync(string vmName)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadStoreAsync();
            return store.Vms.FirstOrDefault(v =>
                string.Equals(v.VmName, vmName, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string vmName)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadStoreAsync();
            store.Vms.RemoveAll(v =>
                string.Equals(v.VmName, vmName, StringComparison.OrdinalIgnoreCase));
            await SaveStoreAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }
}
