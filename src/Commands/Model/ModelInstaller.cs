using System.Text.Json;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;

namespace PKS.Commands.Model;

/// <summary>
/// The one download → verify → (extract) → move → register routine behind
/// <c>model &lt;name&gt; init</c> and <c>update</c>. Stages into <c>&lt;installDir&gt;.staging</c>
/// and commits into <paramref name="installDir"/> only when everything checked out, so a
/// failed install leaves the install dir empty.
/// </summary>
public static class ModelInstaller
{
    public static async Task<InstalledModel> InstallAsync(
        CatalogEntry entry,
        string installDir,
        IModelDownloadService download,
        IModelRegistryService registry,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(installDir);

        var stageDir = installDir + ".staging";
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
        Directory.CreateDirectory(stageDir);

        var isSingleFile = entry.Kind == CatalogEntryKind.SingleFile;
        var downloadPath = isSingleFile
            ? Path.Combine(stageDir, entry.Files.Values.Single())
            : Path.Combine(stageDir, "archive.tar.bz2");

        try
        {
            InstalledModel? manifest = null;
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                         new RemainingTimeColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var dlTask = ctx.AddTask("[cyan]Downloading[/]", maxValue: entry.ExpectedSizeBytes);
                    var dlProgress = new Progress<long>(b => dlTask.Value = b);
                    await download.DownloadAsync(entry.DownloadUrl, downloadPath, dlProgress, ct);
                    dlTask.Value = dlTask.MaxValue;

                    // Verify before anything is extracted or moved: a pinned checksum is the only
                    // thing that makes a GitHub release asset trustworthy.
                    if (!string.IsNullOrEmpty(entry.Sha256))
                    {
                        var verifyTask = ctx.AddTask("[cyan]Verifying checksum[/]", maxValue: 1);
                        var actual = await download.ComputeSha256Async(downloadPath, ct);
                        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"checksum mismatch for {Path.GetFileName(downloadPath)}: expected {entry.Sha256}, got {actual}");
                        verifyTask.Value = 1;
                    }

                    string extractedRoot;
                    if (isSingleFile)
                    {
                        // The download is the model file itself; nothing to extract.
                        extractedRoot = stageDir;
                    }
                    else
                    {
                        // Decompressing 640 MiB of bz2 takes ~30–60s on a typical laptop CPU.
                        // Use the .tar.bz2 file size as the bar's max and report bytes consumed
                        // from the input file so the bar moves smoothly during extraction.
                        var archiveSize = new FileInfo(downloadPath).Length;
                        var extractTask = ctx.AddTask("[cyan]Extracting[/]", maxValue: archiveSize);
                        var extractProgress = new Progress<long>(b => extractTask.Value = b);
                        await download.ExtractTarBz2Async(downloadPath, stageDir, extractProgress, ct);
                        extractTask.Value = archiveSize;

                        // Find the extracted top-level dir (e.g. "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8")
                        var topDirs = Directory.GetDirectories(stageDir);
                        if (topDirs.Length == 0)
                            throw new InvalidOperationException("Archive did not contain a top-level directory.");
                        extractedRoot = topDirs[0];
                    }

                    var regTask = ctx.AddTask("[cyan]Registering[/]", maxValue: 1);
                    // Move each of the expected files into installDir/<canonical-name>.
                    long totalSize = 0;
                    foreach (var (_, filename) in entry.Files)
                    {
                        var src = Path.Combine(extractedRoot, filename);
                        if (!File.Exists(src))
                            throw new FileNotFoundException($"Expected file missing in download: {filename}", src);
                        var dst = Path.Combine(installDir, filename);
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                        totalSize += new FileInfo(dst).Length;
                    }

                    manifest = new InstalledModel
                    {
                        Name = entry.Name,
                        DisplayName = entry.DisplayName,
                        Version = entry.Version,
                        Capabilities = entry.Capabilities.ToList(),
                        Languages = entry.Languages.ToList(),
                        InstallPath = installDir,
                        InstalledAt = DateTime.UtcNow,
                        SizeBytes = totalSize,
                        SherpaModelType = entry.SherpaModelType,
                        Files = entry.Files.ToDictionary(kv => kv.Key, kv => kv.Value),
                        Source = new InstalledModelSource { DownloadUrl = entry.DownloadUrl, Sha256 = entry.Sha256 },
                    };
                    var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(Path.Combine(installDir, "manifest.json"), manifestJson, ct);
                    await registry.RegisterAsync(manifest, ct);
                    regTask.Value = 1;
                });
            return manifest!;
        }
        finally
        {
            if (Directory.Exists(stageDir))
            {
                try { Directory.Delete(stageDir, recursive: true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
