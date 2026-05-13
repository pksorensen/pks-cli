using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Models;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Model;

[Description("Update an installed AI model to the latest catalog version")]
public class ModelUpdateCommand : AsyncCommand<ModelSettings>
{
    private readonly IModelRegistryService _registry;
    private readonly IModelDownloadService _download;
    private readonly IAnsiConsole _console;

    public ModelUpdateCommand(IModelRegistryService registry, IModelDownloadService download, IAnsiConsole console)
    {
        _registry = registry;
        _download = download;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ModelSettings settings)
    {
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

        var installed = await _registry.GetAsync(entry.Name);
        if (installed == null)
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(entry.Name)} is not installed.[/]");
            _console.MarkupLine($"[dim]Run [bold]pks model {Markup.Escape(entry.Name)} init[/] to install it first.[/]");
            return 0;
        }

        if (installed.Version == entry.Version)
        {
            _console.MarkupLine($"[green]{Markup.Escape(entry.Name)} {installed.Version} is already up to date.[/]");
            return 0;
        }

        _console.MarkupLine($"[cyan]Updating {Markup.Escape(entry.DisplayName)}: {Markup.Escape(installed.Version)} → {Markup.Escape(entry.Version)}[/]");
        _console.MarkupLine($"[dim]  Download size: ~{Math.Round(entry.ExpectedSizeBytes / (1024.0 * 1024), 0)} MiB[/]");
        _console.WriteLine();

        if (!_console.Confirm("[yellow]Proceed with update?[/]", defaultValue: true))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        // Unregister and remove the old install dir before re-running install so a
        // half-done update doesn't leave inconsistent state.
        try
        {
            if (Directory.Exists(installed.InstallPath))
                Directory.Delete(installed.InstallPath, recursive: true);
            await _registry.UnregisterAsync(entry.Name);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to remove existing install: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var installDir = _registry.GetInstallDirectory(entry.Name);
        Directory.CreateDirectory(installDir);

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

                    var extractTask = ctx.AddTask("[cyan]Extracting[/]", maxValue: 1);
                    await _download.ExtractTarBz2Async(archivePath, stageDir);
                    extractTask.Value = 1;

                    var regTask = ctx.AddTask("[cyan]Registering[/]", maxValue: 1);
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

            _console.MarkupLine($"[green]✓ Updated {Markup.Escape(entry.Name)} to {Markup.Escape(entry.Version)} at {Markup.Escape(installDir)}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Update failed: {Markup.Escape(ex.Message)}[/]");
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
