using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// How both Codex commands name the box and the account on it.
///
/// Shared rather than declared twice because the two commands have to agree about it exactly:
/// <c>pks vm codex</c> creates the account, and <c>pks vm codex sync-config</c> writes into that
/// same account's <c>.codex</c>. A <c>--user</c> that drifted between them would put the
/// configuration in a home directory nothing reads, and the only symptom would be the app going on
/// reporting "logged out".
/// </summary>
public class VmCodexTargetSettings : VmSettings
{
    [CommandArgument(0, "[TARGET]")]
    [Description("Registered SSH target, by label or host (interactive picker if omitted). See 'pks ssh list'")]
    public string? Target { get; set; }

    // Spectre rejects '@' in an option value name, so the placeholder cannot spell the shape.
    [CommandOption("--host <USER_AT_HOST>")]
    [Description("Point at a box that is not a registered target, e.g. root@example.com")]
    public string? Host { get; set; }

    [CommandOption("--port <PORT>")]
    [Description("SSH port for --host (default: 22)")]
    public int Port { get; set; } = 22;

    [CommandOption("--key <PATH>")]
    [Description("Private key for --host (default: whatever ssh would pick)")]
    public string? KeyPath { get; set; }

    [CommandOption("--user <NAME>")]
    [Description($"The account Codex runs as on the box (default: {VmCodexBootstrapScript.DefaultUser})")]
    public string User { get; set; } = VmCodexBootstrapScript.DefaultUser;
}

/// <summary>A resolved box: how to reach it, what to call it, and the key file on this machine when
/// there is one.</summary>
/// <param name="Host">Connection details for <see cref="ISshCommandRunner"/>.</param>
/// <param name="Label">What to call the box in output, and the base for a default alias.</param>
/// <param name="LocalKeyPath">A key that exists as a file on <em>this</em> machine, or null when
/// pks holds it in its own store — in which case no ssh config stanza may name it.</param>
/// <param name="Key">A decrypted short-lived copy of a managed key. The caller must dispose it.</param>
public sealed record VmCodexResolvedTarget(
    RemoteHostConfig Host,
    string Label,
    string? LocalKeyPath,
    MaterializedKey? Key) : IDisposable
{
    public void Dispose() => Key?.Dispose();
}

/// <summary>
/// Turns <see cref="VmCodexTargetSettings"/> into a box to connect to, with the same precedence for
/// both Codex commands: an explicit <c>--host</c> wins, then a named target, then the only
/// registered target, then a picker.
/// </summary>
public static class VmCodexTarget
{
    public static async Task<VmCodexResolvedTarget?> ResolveAsync(
        VmCodexTargetSettings settings,
        ISshTargetConfigurationService sshTargets,
        ISshKeyStore keyStore,
        IAnsiConsole console)
    {
        if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            var raw = settings.Host!.Trim();
            var at = raw.LastIndexOf('@');
            if (at <= 0 || at == raw.Length - 1)
            {
                console.MarkupLine($"[red]--host wants user@host[/] [dim](got '{Markup.Escape(raw)}')[/]");
                return null;
            }

            var cfg = new RemoteHostConfig
            {
                Username = raw[..at],
                Host = raw[(at + 1)..],
                Port = settings.Port,
                KeyPath = settings.KeyPath,
            };
            return new VmCodexResolvedTarget(cfg, cfg.Host, settings.KeyPath, null);
        }

        var targets = await sshTargets.ListTargetsAsync();
        SshTarget? target;

        if (!string.IsNullOrWhiteSpace(settings.Target))
        {
            target = await sshTargets.FindTargetAsync(settings.Target!);
            if (target is null)
            {
                console.MarkupLine($"[red]No SSH target '{Markup.Escape(settings.Target!)}'.[/] [dim]See [bold]pks ssh list[/], or pass [bold]--host user@host[/].[/]");
                return null;
            }
        }
        else if (targets.Count == 0)
        {
            console.MarkupLine("[yellow]No registered SSH targets.[/] [dim]Pass [bold]--host user@host[/], or register one with [bold]pks ssh register[/].[/]");
            return null;
        }
        else if (targets.Count == 1)
        {
            target = targets[0];
        }
        else
        {
            var choice = console.Prompt(new SelectionPrompt<string>()
                .Title("[cyan]Which box?[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(targets.Select(Describe)));
            target = targets.First(t => Describe(t) == choice);
        }

        // A target may hold its key in the pks key store rather than at a path, in which case this
        // decrypts it to a short-lived 0600 temp that the caller must dispose.
        var (config, materialized) = await target.ToRemoteHostConfigAsync(keyStore);

        // A stanza for the laptop may only name a key that exists as a file there. A target whose
        // key pks holds in its own store (ManagedKeyId) has no such path — the materialized copy is
        // a temp that is shredded when the command returns.
        var localKey = string.IsNullOrWhiteSpace(target.ManagedKeyId) && !string.IsNullOrWhiteSpace(target.KeyPath)
            ? target.KeyPath
            : null;

        return new VmCodexResolvedTarget(config, target.Label ?? target.Host, localKey, materialized);
    }

    public static string Describe(SshTarget t)
        => string.IsNullOrWhiteSpace(t.Label) ? $"{t.Username}@{t.Host}" : $"{t.Label} ({t.Username}@{t.Host})";
}
