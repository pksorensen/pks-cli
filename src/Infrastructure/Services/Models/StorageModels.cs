namespace PKS.Infrastructure.Services.Models;

public enum SyncDirection { Download, Upload, Bidirectional }

public class StorageResource
{
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class StorageSyncRequest
{
    public string ProviderKey { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string ResourceName { get; set; } = string.Empty;
    public string LocalDirectory { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; } = SyncDirection.Download;
    public bool DryRun { get; set; }
    public bool Delete { get; set; }
    public bool VerifyChecksum { get; set; }
    public int MaxParallelism { get; set; } = 4;
    public string[] Include { get; set; } = [];
    public string[] Exclude { get; set; } = [];
}

public class SyncResult
{
    public int FilesDownloaded { get; set; }
    public int FilesUploaded { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesSkipped { get; set; }
    public long BytesTransferred { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool Success => Errors.Count == 0;
}

public record SyncProgressUpdate(int Completed, int Total, string CurrentFile);

public enum StorageItemType { File, Directory }

public class StorageListItem
{
    public string Name { get; set; } = string.Empty;
    public StorageItemType Type { get; set; }
    public long? SizeBytes { get; set; }
    public int? ItemCount { get; set; }
}

public class StorageListRequest
{
    public string Path { get; set; } = "/";
    public int Limit { get; set; } = 100;
    public bool IncludeCount { get; set; }
    public bool DirsOnly { get; set; }
}

public class StorageListResult
{
    public string ShareName { get; set; } = string.Empty;
    public string Path { get; set; } = "/";
    public List<StorageListItem> Items { get; set; } = new();
    public bool Truncated { get; set; }
}
