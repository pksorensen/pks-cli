using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Download and install an AI model")]
public class ModelInitCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IModelDownloadService _download;
    private readonly IAnsiConsole _console;

    public ModelInitCommand(IModelRegistryService registry, IModelDownloadService download, IAnsiConsole console)
    {
        _registry = registry;
        _download = download;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
        // Spectre's branch-data carries the model name. Defensive default for direct test invocation.
        var modelName = context.Data as string;
        if (string.IsNullOrEmpty(modelName))
        {
            _console.MarkupLine("[red]No model name in command context. Did Program.cs register the branch with WithData(name)?[/]");
            return 1;
        }

        var entry = ModelCatalog.Find(modelName);
        if (entry == null)
        {
            _console.MarkupLine($"[red]Unknown model: {Markup.Escape(modelName)}[/]");
            return 1;
        }

        var existing = await _registry.GetAsync(entry.Name);
        if (existing != null && existing.Version == entry.Version)
        {
            _console.MarkupLine($"[green]{Markup.Escape(entry.Name)} {existing.Version} is already installed at {Markup.Escape(existing.InstallPath)}.[/]");
            _console.MarkupLine($"[dim]Use [bold]pks model {Markup.Escape(entry.Name)} update[/] to re-download or [bold]remove[/] first.[/]");
            return 0;
        }

        _console.MarkupLine($"[cyan]Installing {Markup.Escape(entry.DisplayName)} ({entry.Version})[/]");
        _console.MarkupLine($"[dim]  Download size: ~{Math.Round(entry.ExpectedSizeBytes / (1024.0 * 1024), 0)} MiB[/]");
        _console.MarkupLine($"[dim]  Capabilities:  {string.Join(", ", entry.Capabilities)}[/]");
        _console.WriteLine();

        if (!_console.Confirm("[yellow]Proceed with download?[/]", defaultValue: true))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        var installDir = _registry.GetInstallDirectory(entry.Name);
        Directory.CreateDirectory(installDir);

        // Stage downloads in a temp directory next to the install dir so we can atomically
        // commit only after extract succeeds. On failure, leave the install dir empty.
        var stageDir = installDir + ".staging";
        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
        Directory.CreateDirectory(stageDir);

        var archivePath = Path.Combine(stageDir, "archive.tar.bz2");

        try
        {
            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                         new RemainingTimeColumn(), new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var dlTask = ctx.AddTask("[cyan]Downloading[/]", maxValue: entry.ExpectedSizeBytes);
                    var dlProgress = new Progress<long>(b => dlTask.Value = b);
                    await _download.DownloadAsync(entry.DownloadUrl, archivePath, dlProgress);
                    dlTask.Value = dlTask.MaxValue;

                    // Decompressing 640 MiB of bz2 takes ~30–60s on a typical laptop CPU.
                    // Use the .tar.bz2 file size as the bar's max and report bytes consumed
                    // from the input file so the bar moves smoothly during extraction.
                    var archiveSize = new FileInfo(archivePath).Length;
                    var extractTask = ctx.AddTask("[cyan]Extracting[/]", maxValue: archiveSize);
                    var extractProgress = new Progress<long>(b => extractTask.Value = b);
                    await _download.ExtractTarBz2Async(archivePath, stageDir, extractProgress);
                    extractTask.Value = archiveSize;

                    var regTask = ctx.AddTask("[cyan]Registering[/]", maxValue: 1);
                    // Find the extracted top-level dir (e.g. "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8")
                    // and move/rename each of the expected files into installDir/<canonical-name>.
                    var topDirs = Directory.GetDirectories(stageDir);
                    if (topDirs.Length == 0)
                        throw new InvalidOperationException("Archive did not contain a top-level directory.");
                    var extractedRoot = topDirs[0];

                    long totalSize = 0;
                    foreach (var (logical, filename) in entry.Files)
                    {
                        var src = Path.Combine(extractedRoot, filename);
                        if (!File.Exists(src))
                            throw new FileNotFoundException($"Expected file missing in archive: {filename}", src);
                        var dst = Path.Combine(installDir, filename);
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                        totalSize += new FileInfo(dst).Length;
                    }

                    var manifest = new InstalledModel
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
                    await File.WriteAllTextAsync(Path.Combine(installDir, "manifest.json"), manifestJson);
                    await _registry.RegisterAsync(manifest);
                    regTask.Value = 1;
                });

            _console.MarkupLine($"[green]✓ Installed {Markup.Escape(entry.Name)} at {Markup.Escape(installDir)}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Install failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
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
