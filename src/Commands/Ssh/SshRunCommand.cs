using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

/// <summary>
/// Non-interactive command execution on a registered SSH target. Forwards local stdin to the remote
/// command and streams remote stdout/stderr back transparently — so it behaves exactly like `ssh`,
/// including the pipe idiom:  <c>tar czf - dir | pks ssh run hetzner -- "cd ~/dst &amp;&amp; tar xzf -"</c>.
/// Reuses the same pks-held-key materialization + action-guard as `ssh connect`.
/// </summary>
[Description("Run a command on a registered SSH target (non-interactive; forwards stdin)")]
public class SshRunCommand : Command<SshRunCommand.Settings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly ISshKeyStore _keyStore;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public SshRunCommand(
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
        [CommandArgument(0, "<TARGET>")]
        [Description("SSH target label or host")]
        public string Target { get; set; } = "";

        [CommandArgument(1, "[CMD]")]
        [Description("Command to run (or pass it after --). Quote it as one argument.")]
        public string? InlineCommand { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        // Remote command: everything after `--` (preferred), else the single inline CMD argument.
        var remoteCommand = context.Remaining.Raw.Count > 0
            ? string.Join(' ', context.Remaining.Raw)
            : settings.InlineCommand;

        if (string.IsNullOrWhiteSpace(remoteCommand))
        {
            _console.MarkupLine("[red]No command given. Usage:[/] [dim]pks ssh run <target> -- \"<command>\"[/]");
            return 1;
        }

        return ExecuteAsync(settings.Target, remoteCommand).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(string targetSpec, string remoteCommand)
    {
        var target = await _configService.FindTargetAsync(targetSpec);
        if (target == null)
        {
            _console.MarkupLine($"[red]SSH target '{Markup.Escape(targetSpec)}' not found.[/]");
            return 1;
        }

        // Same choke-point as `connect`: an outbound SSH command requires the operator's second factor
        // (once enrolled). Reusing ssh.connect keeps the policy in one place.
        try
        {
            await _guard.RequireAsync(new ActionRequest(ActionIds.SshConnect,
                $"Run a command on {target.Username}@{target.Host}:{target.Port}"));
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

            var psi = new ProcessStartInfo("ssh") { UseShellExecute = false };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("StrictHostKeyChecking=no");
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(target.Port.ToString());
            if (!string.IsNullOrEmpty(keyPath))
            {
                psi.ArgumentList.Add("-o");
                psi.ArgumentList.Add("IdentitiesOnly=yes");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(keyPath);
            }
            psi.ArgumentList.Add($"{target.Username}@{target.Host}");
            // One trailing arg = the remote shell command (ssh runs it via the login shell), so
            // `cd … && tar xzf -` and pipelines work. stdin/stdout/stderr are inherited (not
            // redirected) → local stdin is forwarded and remote output streams straight through.
            psi.ArgumentList.Add(remoteCommand);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _console.MarkupLine("[red]Failed to start ssh process.[/]");
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
}
