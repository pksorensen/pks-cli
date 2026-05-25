using System.ComponentModel;
using System.IO.Compression;
using Spectre.Console;
using Spectre.Console.Cli;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class WritingProfileExportSettings : WritingSettings
{
    [CommandArgument(0, "<output>")]
    [Description("Output .tgz path (e.g. ~/pks-writing-profile-poul.tgz).")]
    public string Output { get; set; } = "";
}

public class WritingProfileExportCommand : AsyncCommand<WritingProfileExportSettings>
{
    private readonly IWritingPathResolver _paths;

    public WritingProfileExportCommand(IWritingPathResolver paths)
    {
        _paths = paths;
    }

    public override Task<int> ExecuteAsync(CommandContext context, WritingProfileExportSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            AnsiConsole.MarkupLine("[red]error:[/] output path required.");
            return Task.FromResult(1);
        }
        var output = System.IO.Path.GetFullPath(ExpandHome(settings.Output));

        if (!Directory.Exists(_paths.GlobalRoot))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no profile to export at [cyan]{_paths.GlobalRoot}[/]");
            AnsiConsole.MarkupLine("    Run [bold]pks writing init[/] first.");
            return Task.FromResult(1);
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);

        // Exclude binary cache directory (Vale binary) — too big and platform-specific.
        var excludePrefix = NormalizeDirPrefix(_paths.GlobalValeBinDir);
        int fileCount = 0;

        using (var outStream = File.Create(output))
        using (var gzStream = new GZipOutputStream(outStream) { IsStreamOwner = true })
        using (var tar = TarArchive.CreateOutputTarArchive(gzStream, TarBuffer.DefaultBlockFactor, System.Text.Encoding.UTF8))
        {
            tar.RootPath = _paths.GlobalRoot.Replace('\\', '/').TrimEnd('/');

            foreach (var file in Directory.EnumerateFiles(_paths.GlobalRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.StartsWith(excludePrefix, StringComparison.Ordinal))
                    continue;

                var entry = TarEntry.CreateEntryFromFile(file);
                // Strip the absolute prefix so the archive is relocatable.
                var rel = System.IO.Path.GetRelativePath(_paths.GlobalRoot, file).Replace('\\', '/');
                entry.Name = "writing/" + rel;
                tar.WriteEntry(entry, recurse: false);
                fileCount++;
            }
        }

        var size = new FileInfo(output).Length;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Exported [bold]{fileCount}[/] file{(fileCount == 1 ? "" : "s")} ({size / 1024.0:0.0} KB) to [cyan]{output}[/]");
        AnsiConsole.MarkupLine($"    On another machine: [bold]pks writing profile import {output}[/]");
        return Task.FromResult(0);
    }

    private static string NormalizeDirPrefix(string dir)
    {
        var s = dir.Replace('\\', '/').TrimEnd('/');
        return s + "/";
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
