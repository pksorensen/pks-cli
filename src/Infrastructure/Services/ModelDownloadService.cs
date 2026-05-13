using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;

namespace PKS.Infrastructure.Services;

public class ModelDownloadService : IModelDownloadService
{
    private readonly HttpClient _http;

    public ModelDownloadService(HttpClient http)
    {
        _http = http;
        if (_http.Timeout < TimeSpan.FromMinutes(30))
        {
            _http.Timeout = TimeSpan.FromMinutes(30);
        }
    }

    public async Task DownloadAsync(string url, string destPath, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            progress?.Report(copied);
        }
    }

    public Task ExtractTarBz2Async(string archivePath, string destDir, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        return Task.Run(() =>
        {
            using var fs = File.OpenRead(archivePath);
            using var bz = new BZip2InputStream(fs);
            using var tar = TarArchive.CreateInputTarArchive(bz, Encoding.UTF8);

            // fs.Position advances as BZip2InputStream pulls compressed chunks from the
            // file. Polling it every 250ms drives the caller's progress bar smoothly —
            // tar.ProgressMessageEvent only fires per-entry (4 entries in the Parakeet
            // archive), so a per-entry counter spends 50 seconds at 0% on the big encoder.
            using var timer = new System.Threading.Timer(_ =>
            {
                try { progress?.Report(fs.Position); }
                catch { /* file may have been disposed mid-tick */ }
            }, null, 250, 250);

            tar.ExtractContents(destDir);
            progress?.Report(fs.Length);
        }, ct);
    }

    public async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(filePath);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
