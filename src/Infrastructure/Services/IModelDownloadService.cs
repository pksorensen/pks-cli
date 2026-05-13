namespace PKS.Infrastructure.Services;

public interface IModelDownloadService
{
    Task DownloadAsync(string url, string destPath, IProgress<long>? progress = null, CancellationToken ct = default);

    Task ExtractTarBz2Async(string archivePath, string destDir, IProgress<long>? progress = null, CancellationToken ct = default);

    Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default);
}
