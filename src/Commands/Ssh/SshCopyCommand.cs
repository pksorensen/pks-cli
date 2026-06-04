using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

/// <summary>
/// Copy files to/from a registered SSH target via scp, reusing the pks-held key + action-guard.
/// A remote path is written as <c>&lt;target&gt;:&lt;path&gt;</c>. Examples:
///   <c>pks ssh copy ./build.tar hetzner:~/build.tar</c>  (upload)
///   <c>pks ssh copy hetzner:~/out.json ./out.json</c>     (download)
/// </summary>
[Description("Copy files to/from a registered SSH target (scp)")]
public class SshCopyCommand : Command<SshCopyCommand.Settings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly ISshKeyStore _keyStore;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public SshCopyCommand(
        ISshTargetConfigurationService configService,
        ISshKeyStore keyStore,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _configService = configService;
        _keyStore = keyStore;
        _guard = guard;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandArgument(0, "<SOURCE>")]
        [Description("Source path (local, or <target>:<remote>)")]
        public string Source { get; set; } = "";

        [CommandArgument(1, "<DEST>")]
        [Description("Destination path (local, or <target>:<remote>)")]
        public string Dest { get; set; } = "";

        [CommandOption("-r|--recursive")]
        [Description("Recurse into directories")]
        public bool Recursive { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        // Exactly one side must carry a "<target>:<path>" spec that resolves to a registered target.
        var (srcTarget, srcRemote) = await TrySplitRemoteAsync(settings.Source);
        var (dstTarget, dstRemote) = await TrySplitRemoteAsync(settings.Dest);

        if ((srcTarget == null) == (dstTarget == null))
        {
            _console.MarkupLine("[red]Exactly one of SOURCE/DEST must be a remote '<target>:<path>'.[/]");
            return 1;
        }

        var target = srcTarget ?? dstTarget!;
        try
        {
            await _guard.RequireAsync(new ActionRequest(ActionIds.SshConnect,
                $"Copy files {(srcTarget != null ? "from" : "to")} {target.Username}@{target.Host}:{target.Port}"));
        }
        catch (ActionGuardDeniedException ex)
        {
            _console.MarkupLine($"[red]Connection denied:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        MaterializedKey? materialized = null;
        try
        {
            var keyPath = target.KeyPath;
            if (!string.IsNullOrEmpty(target.ManagedKeyId))
            {
                try
                {
                    materialized = await _keyStore.MaterializeAsync(target.ManagedKeyId);
                    keyPath = materialized.Path;
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Could not access pks-held key:[/] {Markup.Escape(ex.Message)}");
                    return 1;
                }
            }

            var remotePrefix = $"{target.Username}@{target.Host}:";
            var srcArg = srcTarget != null ? remotePrefix + srcRemote : settings.Source;
            var dstArg = dstTarget != null ? remotePrefix + dstRemote : settings.Dest;

            var psi = new ProcessStartInfo("scp") { UseShellExecute = false };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("StrictHostKeyChecking=no");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-P"); // scp uses uppercase -P for port
            psi.ArgumentList.Add(target.Port.ToString());
            if (settings.Recursive) psi.ArgumentList.Add("-r");
            if (!string.IsNullOrEmpty(keyPath))
            {
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("IdentitiesOnly=yes");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(keyPath);
            }
            psi.ArgumentList.Add(srcArg);
            psi.ArgumentList.Add(dstArg);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _console.MarkupLine("[red]Failed to start scp process.[/]");
                return 1;
            }
            proc.WaitForExit();
            return proc.ExitCode;
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    /// <summary>If <paramref name="spec"/> is "<label>:<path>" and <label> resolves to a registered
    /// target, return (target, path); otherwise (null, spec) — a local path.</summary>
    private async Task<(SshTarget?, string)> TrySplitRemoteAsync(string spec)
    {
        var idx = spec.IndexOf(':');
        // require at least 2 chars before ':' so a Windows drive ("C:") isn't mistaken for a target
        if (idx <= 1) return (null, spec);
        var label = spec[..idx];
        var path = spec[(idx + 1)..];
        var target = await _configService.FindTargetAsync(label);
        return target != null ? (target, path) : (null, spec);
    }
}
