using System.ComponentModel;
using System.Runtime.InteropServices;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ado;

/// <summary>
/// GIT_ASKPASS handler for Azure DevOps. Outputs credentials to stdout with zero Spectre markup.
/// Usage: GIT_ASKPASS="pks git askpass" git clone https://dev.azure.com/org/repo
/// </summary>
[Description("Git credential helper for Azure DevOps (used as GIT_ASKPASS)")]
public class GitAskPassCommand : Command<GitAskPassCommand.Settings>
{
    private readonly IAzureDevOpsAuthService _authService;

    public GitAskPassCommand(IAzureDevOpsAuthService authService)
    {
        _authService = authService;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[prompt]")]
        [Description("The credential prompt from Git")]
        public string? Prompt { get; set; }

        [CommandOption("--install")]
        [Description("Install the GIT_ASKPASS wrapper script and configure your shell")]
        public bool Install { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        if (settings.Install)
            return InstallAskPassWrapper();

        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private static int InstallAskPassWrapper()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binDir = Path.Combine(home, ".local", "bin");
        Directory.CreateDirectory(binDir);

        var scriptPath = Path.Combine(binDir, "pks-git-askpass");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            scriptPath += ".cmd";
            File.WriteAllText(scriptPath, "@echo off\r\npks git askpass %*\r\n");
        }
        else
        {
            File.WriteAllText(scriptPath, "#!/bin/sh\nexec pks git askpass \"$@\"\n");
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        // Try to add to shell profile
        var exportLine = $"export GIT_ASKPASS=\"$HOME/.local/bin/{Path.GetFileName(scriptPath)}\"";
        var shellConfigured = false;

        foreach (var rcFile in new[] { ".zshrc", ".bashrc", ".profile" })
        {
            var rcPath = Path.Combine(home, rcFile);
            if (!File.Exists(rcPath)) continue;

            var content = File.ReadAllText(rcPath);
            if (content.Contains("GIT_ASKPASS")) { shellConfigured = true; break; }

            File.AppendAllText(rcPath, $"\n# PKS CLI git credential helper\n{exportLine}\n");
            shellConfigured = true;
            AnsiConsole.MarkupLine($"[green]Updated[/] [dim]{rcPath}[/]");
            break;
        }

        AnsiConsole.MarkupLine($"[green]Installed[/] [dim]{scriptPath}[/]");

        if (!shellConfigured)
        {
            AnsiConsole.MarkupLine($"\n[yellow]Add this to your shell profile:[/]");
            AnsiConsole.MarkupLine($"[cyan]{exportLine}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"\n[dim]Restart your shell or run:[/]");
            AnsiConsole.MarkupLine($"[cyan]{exportLine}[/]");
        }

        return 0;
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        try
        {
            var prompt = settings.Prompt ?? string.Empty;

            if (prompt.Contains("Username", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("pks");
                return 0;
            }

            if (prompt.Contains("Password", StringComparison.OrdinalIgnoreCase))
            {
                var token = await _authService.RefreshAccessTokenAsync();
                if (string.IsNullOrEmpty(token))
                    return 1;

                Console.Write(token);
                return 0;
            }

            // Unknown prompt — fail silently
            return 1;
        }
        catch
        {
            // Silent failure — Git expects no stderr output
            return 1;
        }
    }
}
