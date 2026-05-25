using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using PKS.Infrastructure.Services.Writing;

namespace PKS.Commands.Writing;

public class WritingProfileImportSettings : WritingSettings
{
    [CommandArgument(0, "<archive>")]
    [Description("Input .tgz path produced by `pks writing profile export`.")]
    public string Archive { get; set; } = "";

    [CommandOption("--force")]
    [Description("Overwrite existing files. Default behaviour skips files that already exist.")]
    public bool Force { get; set; }
}

public class WritingProfileImportCommand : AsyncCommand<WritingProfileImportSettings>
{
    private readonly IWritingPathResolver _paths;

    public WritingProfileImportCommand(IWritingPathResolver paths)
    {
        _paths = paths;
    }

    public override Task<int> ExecuteAsync(CommandContext context, WritingProfileImportSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Archive))
        {
            AnsiConsole.MarkupLine("[red]error:[/] archive path required.");
            return Task.FromResult(1);
        }
        var archive = System.IO.Path.GetFullPath(ExpandHome(settings.Archive));
        if (!File.Exists(archive))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] archive not found at [cyan]{archive}[/]");
            return Task.FromResult(1);
        }

        Directory.CreateDirectory(_paths.GlobalRoot);

        int extracted = 0, skipped = 0;
        var prefix = "writing/";

        using (var inStream = File.OpenRead(archive))
        using (var gz = new GZipInputStream(inStream) { IsStreamOwner = true })
        using (var tar = TarArchive.CreateInputTarArchive(gz, TarBuffer.DefaultBlockFactor, System.Text.Encoding.UTF8))
        {
            tar.ProgressMessageEvent += (_, entry, _) =>
            {
                if (entry.IsDirectory) return;

                var name = entry.Name.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) return;
                var rel = name.Substring(prefix.Length);
                if (string.IsNullOrEmpty(rel)) return;
                if (rel.Contains("..")) return; // safety

                var dest = System.IO.Path.Combine(_paths.GlobalRoot, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (File.Exists(dest) && !settings.Force)
                {
                    skipped++;
                    return;
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                // Reading here would need a re-extract — easier path: do ExtractContents below.
            };

            // Use ExtractContents over the whole archive, then filter on the
            // filesystem in a second pass — this is the most reliable way with
            // SharpZipLib's TarArchive API. Stage into a temp dir first to
            // honour --force / skip semantics.
            var staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "pks-writing-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                tar.ExtractContents(staging);

                var stagedRoot = System.IO.Path.Combine(staging, "writing");
                if (!Directory.Exists(stagedRoot))
                {
                    AnsiConsole.MarkupLine("[red]error:[/] archive has no `writing/` root — was this produced by `pks writing profile export`?");
                    return Task.FromResult(1);
                }

                foreach (var file in Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories))
                {
                    var rel = System.IO.Path.GetRelativePath(stagedRoot, file);
                    var dest = System.IO.Path.Combine(_paths.GlobalRoot, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);

                    if (File.Exists(dest) && !settings.Force)
                    {
                        skipped++;
                        continue;
                    }
                    File.Copy(file, dest, overwrite: true);
                    extracted++;
                }
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Imported [bold]{extracted}[/] file{(extracted == 1 ? "" : "s")} into [cyan]{_paths.GlobalRoot}[/]");
        if (skipped > 0)
            AnsiConsole.MarkupLine($"[grey]  Skipped {skipped} (already exists — re-run with --force to overwrite)[/]");
        return Task.FromResult(0);
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
