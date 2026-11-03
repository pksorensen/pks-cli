using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Makes a box answer Codex Desktop's "Remote SSH" connection: installs the Codex CLI and creates a
/// dedicated unprivileged login account for it, then prints the two things that cannot be done from
/// here — the ssh config stanza for the laptop and the one-off sign-in.
///
/// Scoped at a host rather than at <c>pks vm init</c> on purpose. The boxes that need this are
/// mostly boxes that already exist and were never provisioned by pks, so a flag on the provisioning
/// command would solve the rarer half of the problem. <c>vm init --codex</c> routes back into this
/// same script, so a freshly provisioned VM gets it without a second command.
/// </summary>
[Description("Install the Codex CLI on a box and create a dedicated non-root account for Codex Desktop")]
public sealed class VmCodexCommand : AsyncCommand<VmCodexCommand.Settings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly ISshCommandRunner _ssh;
    private readonly ISshKeyStore _keyStore;
    private readonly IAnsiConsole _console;

    public VmCodexCommand(
        ISshTargetConfigurationService sshTargets,
        ISshCommandRunner ssh,
        ISshKeyStore keyStore,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _ssh = ssh;
        _keyStore = keyStore;
        _console = console;
    }

    public sealed class Settings : VmSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [Description("Registered SSH target, by label or host (interactive picker if omitted). See 'pks ssh list'")]
        public string? Target { get; set; }

        // Spectre rejects '@' in an option value name, so the placeholder cannot spell the shape.
        [CommandOption("--host <USER_AT_HOST>")]
        [Description("Bootstrap a box that is not a registered target, e.g. root@example.com")]
        public string? Host { get; set; }

        [CommandOption("--port <PORT>")]
        [Description("SSH port for --host (default: 22)")]
        public int Port { get; set; } = 22;

        [CommandOption("--key <PATH>")]
        [Description("Private key for --host (default: whatever ssh would pick)")]
        public string? KeyPath { get; set; }

        [CommandOption("--user <NAME>")]
        [Description($"Account to create and run Codex as (default: {VmCodexBootstrapScript.DefaultUser})")]
        public string User { get; set; } = VmCodexBootstrapScript.DefaultUser;

        [CommandOption("--authorized-key <KEY_OR_PATH>")]
        [Description("Public key to authorise for the account; repeatable. Default: inherit the keys the account we connect as already trusts")]
        public string[] AuthorizedKeys { get; set; } = Array.Empty<string>();

        [CommandOption("--node <MAJOR>")]
        [Description($"Node major to install when the box's is older (default: {VmCodexBootstrapScript.DefaultNodeMajorText})")]
        public int NodeMajor { get; set; } = VmCodexBootstrapScript.DefaultNodeMajor;

        [CommandOption("--codex-version <VERSION>")]
        [Description("Pin the Codex CLI version instead of installing @latest")]
        public string? CodexVersion { get; set; }

        [CommandOption("--docker")]
        [Description("Add the account to the docker group. Root-equivalent on the host — opt in knowingly")]
        public bool Docker { get; set; }

        [CommandOption("--sudo")]
        [Description("Grant the account passwordless sudo. Off by default, and dropping the flag revokes it again")]
        public bool Sudo { get; set; }

        [CommandOption("--alias <ALIAS>")]
        [Description("Host alias for the printed ssh config stanza (default: <target>-codex)")]
        public string? Alias { get; set; }

        [CommandOption("--no-ssh-config")]
        [Description("Do not touch ~/.ssh/config; print the stanza to paste instead")]
        public bool NoSshConfig { get; set; }

        [CommandOption("--print")]
        [Description("Print the script instead of running it")]
        public bool Print { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        _console.Write(new Rule("[cyan]Codex on a remote box[/]").LeftJustified());

        if (!IsValidAccountName(settings.User))
        {
            _console.MarkupLine($"[red]'{Markup.Escape(settings.User)}' is not a usable account name.[/] [dim]Lowercase letters, digits, '_' and '-'.[/]");
            return 1;
        }

        if (string.Equals(settings.User, "root", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine("[red]--user root defeats the point of the command.[/] [dim]Codex would run as uid 0 with write access to the whole box.[/]");
            return 1;
        }

        var keys = ReadAuthorizedKeys(settings.AuthorizedKeys, out var keyError);
        if (keyError is not null)
        {
            _console.MarkupLine($"[red]{Markup.Escape(keyError)}[/]");
            return 1;
        }

        var script = VmCodexBootstrapScript.Build(new VmCodexBootstrapOptions
        {
            User = settings.User,
            AuthorizedKeys = keys,
            NodeMajor = settings.NodeMajor,
            Docker = settings.Docker,
            Sudo = settings.Sudo,
            CodexVersion = settings.CodexVersion,
        });

        if (settings.Print)
        {
            // Deliberately before target resolution: --print is also how you read the script without
            // having a box to point it at yet.
            Console.WriteLine(script);
            return 0;
        }

        var resolved = await ResolveAsync(settings);
        if (resolved is null) return 1;
        var (host, label, localKeyPath, materialized) = resolved.Value;

        try
        {
            _console.MarkupLine($"[green]Box:[/] {Markup.Escape(label)} [dim]({Markup.Escape(host.Username)}@{Markup.Escape(host.Host)}:{host.Port})[/]");
            _console.MarkupLine($"[green]Account:[/] {Markup.Escape(settings.User)}"
                + (settings.Sudo ? " [yellow](passwordless sudo)[/]" : string.Empty)
                + (settings.Docker ? " [yellow](docker group)[/]" : string.Empty));

            SshCommandResult? result = null;
            await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync($"Installing node, the Codex CLI and {settings.User}…",
                    async _ => result = await _ssh.RunAsync(host, script));

            if (result is null || !result.Success)
            {
                // stderr alone is not enough: apt writes its progress there, so on a failure the
                // tail is debconf chatter and the line that died is above it. The script's ERR trap
                // names the step; stdout carries the "==>" markers.
                _console.MarkupLine($"[red]Bootstrap failed (exit {result?.ExitCode ?? -1}).[/]");
                _console.MarkupLine($"[dim]--- last output ---[/]\n{Markup.Escape(Tail(result?.StdOut))}");
                _console.MarkupLine($"[dim]--- errors ---[/]\n{Markup.Escape(Tail(result?.StdErr))}");
                return 1;
            }

            var signedIn = result.StdOut.Contains("is already signed in to Codex", StringComparison.Ordinal);

            // A box whose config.toml names a model_provider authenticates through that provider's
            // header, so there is no login step to point at. Reporting one would send the reader
            // after something that cannot help.
            var provider = ProviderFrom(result.StdOut);
            _console.MarkupLine("[green]Codex CLI installed and the account is ready.[/]");
            foreach (var warning in Warnings(result))
                _console.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

            PrintNextSteps(settings, host, label, localKeyPath, signedIn, provider);
            return 0;
        }
        finally
        {
            materialized?.Dispose();
        }
    }

    /// <summary>
    /// Finishes the job on this machine, then states only what is genuinely left.
    ///
    /// The ssh config entry is written rather than printed because this command runs on the laptop —
    /// the same machine the Codex app runs on, and the one that holds the key <c>pks vm init</c>
    /// generated. Printing a stanza to paste would leave the easiest step of the whole flow as
    /// homework, and the app offers no host that is not in that file.
    ///
    /// The sign-in is not written, because it cannot be: it needs a human and a browser or a device
    /// code. That asymmetry is the reason this method reports two different kinds of thing.
    /// </summary>
    private void PrintNextSteps(Settings settings, RemoteHostConfig host, string label, string? localKeyPath, bool signedIn, string? provider)
    {
        var alias = settings.Alias ?? $"{Sanitize(label)}-{settings.User}";
        var identity = Absolutise(settings.KeyPath ?? localKeyPath);
        var stanza = VmCodexBootstrapScript.SshConfigStanza(alias, host.Host, settings.User, host.Port, identity);


        _console.WriteLine();
        _console.Write(new Rule("[cyan]Using it from the Codex app[/]").LeftJustified());

        var configPath = LocalSshConfig.DefaultPath();
        var outcome = settings.NoSshConfig ? (SshConfigOutcome?)null : TryWriteSshConfig(configPath, alias, stanza);

        if (outcome is SshConfigOutcome.Added or SshConfigOutcome.Updated or SshConfigOutcome.Unchanged)
        {
            var verb = outcome switch
            {
                SshConfigOutcome.Added => "Added",
                SshConfigOutcome.Updated => "Updated",
                _ => "Already in",
            };
            _console.MarkupLine($"[green]{verb}[/] [cyan]{Markup.Escape(configPath)}[/][green]:[/] host [bold]{Markup.Escape(alias)}[/]");
            foreach (var line in stanza.Split('\n'))
                _console.MarkupLine($"   [dim]{Markup.Escape(line)}[/]");

            if (identity is null)
                _console.MarkupLine($"   [yellow]![/] [dim]Fill in IdentityFile with the key you use for {Markup.Escape(host.Username)}@{Markup.Escape(host.Host)} — pks could not name a file on this machine.[/]");
        }
        else
        {
            // Either the user opted out, or the alias is already spoken for and overwriting someone
            // else's stanza is not ours to do. Either way the stanza still has to reach them.
            if (outcome == SshConfigOutcome.ConflictingAlias)
            {
                _console.MarkupLine($"[yellow]'{Markup.Escape(alias)}' is already defined in {Markup.Escape(configPath)}[/] [dim]— left untouched. ssh uses the first matching Host, so add this by hand or pass --alias.[/]");
            }
            else
            {
                _console.MarkupLine($"[cyan]Add this to {Markup.Escape(configPath)}[/] [dim]— Codex only offers hosts it finds there, reads concrete aliases, and ignores pattern-only ones.[/]");
            }
            _console.WriteLine();
            foreach (var line in stanza.Split('\n'))
                _console.MarkupLine($"   [grey85]{Markup.Escape(line)}[/]");
        }

        _console.WriteLine();

        if (provider is not null)
        {
            _console.MarkupLine($"[green]Provider[/] [bold]{Markup.Escape(provider)}[/] [green]is configured on the box — no codex login step.[/]");
            _console.MarkupLine($"[dim]In Codex: Settings → Connections, enable [bold]{Markup.Escape(alias)}[/], pick a project folder.[/]");
            _console.WriteLine();
            _console.MarkupLine($"[dim]The config lives at[/] [cyan]{Markup.Escape($"~{settings.User}/.codex/config.toml")}[/][dim]; this command does not author it.[/]");
            return;
        }

        if (signedIn)
        {
            _console.MarkupLine($"[green]{Markup.Escape(settings.User)} is already signed in to Codex.[/] [dim]Open Settings → Connections, enable [bold]{Markup.Escape(alias)}[/], pick a folder.[/]");
            return;
        }

        // Prefer the alias we just wrote: it is shorter, and using it is a live check that the
        // stanza resolves. Fall back to the explicit user@host form when the file was not written.
        var wrote = outcome is SshConfigOutcome.Added or SshConfigOutcome.Updated or SshConfigOutcome.Unchanged;
        var login = wrote
            ? $"ssh {alias} -t codex login --device-auth"
            : VmCodexBootstrapScript.LoginHint(settings.User, host.Host, host.Port);
        var forward = wrote
            ? $"ssh -L 1455:localhost:1455 {alias} -t codex login"
            : $"ssh -L 1455:localhost:1455 {settings.User}@{host.Host} -t codex login";

        _console.MarkupLine("[bold]One step left — sign the account in once. Only a human can do this:[/]");
        _console.WriteLine();
        _console.MarkupLine($"   [grey85]{Markup.Escape(login)}[/]");
        _console.WriteLine();
        _console.MarkupLine("   [dim]Device auth, not the default browser flow: that one opens its callback on the box's[/]");
        _console.MarkupLine("   [dim]own loopback, which a browser on this machine cannot reach. It needs device code[/]");
        _console.MarkupLine("   [dim]login enabled on the account; otherwise forward the port instead:[/]");
        _console.MarkupLine($"   [dim]{Markup.Escape(forward)}[/]");
        _console.WriteLine();
        _console.MarkupLine($"[dim]Then in Codex: Settings → Connections, enable [bold]{Markup.Escape(alias)}[/], pick a project folder.[/]");
    }

    /// <summary>
    /// The provider name from the script's own report line, or null when the box has no provider
    /// configured. Parsed from the report rather than probed separately so there is one source of
    /// truth for the box's state, and so a box reached through sudo needs no second round trip.
    /// </summary>
    private static string? ProviderFrom(string stdout)
    {
        const string marker = "==> provider configured: ";
        var at = stdout.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var from = at + marker.Length;
        var to = stdout.IndexOfAny(new[] { '\r', '\n' }, from);
        var line = (to < 0 ? stdout[from..] : stdout[from..to]).Trim();

        // The script appends " (no codex login needed)" for the reader; the name is what precedes it.
        var paren = line.IndexOf(" (", StringComparison.Ordinal);
        if (paren >= 0) line = line[..paren].Trim();

        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// ssh resolves a relative <c>IdentityFile</c> against the process working directory, and Codex
    /// spawns ssh from wherever it pleases — so a relative <c>--key</c> that works on the command
    /// line becomes a key ssh cannot find. A leading <c>~</c> is left alone: ssh expands that
    /// itself, and expanding it here would hard-code this machine's home directory.
    /// </summary>
    private static string? Absolutise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('~')) return path;
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>
    /// A failure to write the config is reported, never fatal: the box is already set up, and the
    /// stanza is printed in the fallback branch so the user can finish by hand.
    /// </summary>
    private SshConfigOutcome? TryWriteSshConfig(string path, string alias, string stanza)
    {
        try
        {
            return LocalSshConfig.Write(path, alias, stanza);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Could not write {Markup.Escape(path)} ({Markup.Escape(ex.Message)}).[/]");
            return null;
        }
    }

    /// <summary>Warnings the script raised on stderr, surfaced individually because each one means
    /// the app will fail in a way that does not mention the cause.</summary>
    private static IEnumerable<string> Warnings(SshCommandResult result)
        => result.StdErr.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("pks vm codex: ", StringComparison.Ordinal))
            .Select(l => l["pks vm codex: ".Length..]);

    private async Task<(RemoteHostConfig Host, string Label, string? LocalKeyPath, MaterializedKey? Key)?> ResolveAsync(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            var raw = settings.Host!.Trim();
            var at = raw.LastIndexOf('@');
            if (at <= 0 || at == raw.Length - 1)
            {
                _console.MarkupLine($"[red]--host wants user@host[/] [dim](got '{Markup.Escape(raw)}')[/]");
                return null;
            }

            var cfg = new RemoteHostConfig
            {
                Username = raw[..at],
                Host = raw[(at + 1)..],
                Port = settings.Port,
                KeyPath = settings.KeyPath,
            };
            return (cfg, cfg.Host, settings.KeyPath, null);
        }

        var targets = await _sshTargets.ListTargetsAsync();
        SshTarget? target;

        if (!string.IsNullOrWhiteSpace(settings.Target))
        {
            target = await _sshTargets.FindTargetAsync(settings.Target!);
            if (target is null)
            {
                _console.MarkupLine($"[red]No SSH target '{Markup.Escape(settings.Target!)}'.[/] [dim]See [bold]pks ssh list[/], or pass [bold]--host user@host[/].[/]");
                return null;
            }
        }
        else if (targets.Count == 0)
        {
            _console.MarkupLine("[yellow]No registered SSH targets.[/] [dim]Pass [bold]--host user@host[/], or register one with [bold]pks ssh register[/].[/]");
            return null;
        }
        else if (targets.Count == 1)
        {
            target = targets[0];
        }
        else
        {
            var choice = _console.Prompt(new SelectionPrompt<string>()
                .Title("[cyan]Which box should Codex run on?[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(targets.Select(Describe)));
            target = targets.First(t => Describe(t) == choice);
        }

        // A target may hold its key in the pks key store rather than at a path, in which case this
        // decrypts it to a short-lived 0600 temp that the caller must dispose.
        var (config, materialized) = await target.ToRemoteHostConfigAsync(_keyStore);

        // The stanza is for the laptop, so it may only name a key that exists as a file there. A
        // target whose key pks holds in its own store (ManagedKeyId) has no such path -- the
        // materialized copy is a short-lived temp that is shredded when this command returns.
        var localKey = string.IsNullOrWhiteSpace(target.ManagedKeyId) && !string.IsNullOrWhiteSpace(target.KeyPath)
            ? target.KeyPath
            : null;
        return (config, target.Label ?? target.Host, localKey, materialized);
    }

    private static string Describe(SshTarget t)
        => string.IsNullOrWhiteSpace(t.Label) ? $"{t.Username}@{t.Host}" : $"{t.Label} ({t.Username}@{t.Host})";

    /// <summary>
    /// Accepts a literal public key or a path to one. A path is the form people reach for first, and
    /// silently treating <c>~/.ssh/id_ed25519.pub</c> as the key itself would write that string into
    /// authorized_keys and produce an account nobody can log into.
    /// </summary>
    private static List<string> ReadAuthorizedKeys(IEnumerable<string> inputs, out string? error)
    {
        error = null;
        var keys = new List<string>();

        foreach (var raw in inputs)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value)) continue;

            if (LooksLikePublicKey(value))
            {
                keys.Add(value);
                continue;
            }

            var path = value.StartsWith('~')
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.TrimStart('~', '/', '\\'))
                : value;

            if (!File.Exists(path))
            {
                error = $"--authorized-key '{value}' is neither a public key nor a file that exists.";
                return keys;
            }

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                if (!LooksLikePublicKey(trimmed))
                {
                    // The overwhelmingly likely cause, and worth naming: a private key pasted or
                    // pointed at by mistake must never be written to an authorized_keys file.
                    error = $"'{path}' does not look like a public key file. Point at the .pub, not the private key.";
                    return keys;
                }
                keys.Add(trimmed);
            }
        }

        return keys;
    }

    private static bool LooksLikePublicKey(string value)
        => value.StartsWith("ssh-", StringComparison.Ordinal)
        || value.StartsWith("ecdsa-", StringComparison.Ordinal)
        || value.StartsWith("sk-ssh-", StringComparison.Ordinal)
        || value.StartsWith("sk-ecdsa-", StringComparison.Ordinal);

    /// <summary>Linux account names: lowercase start, then letters, digits, underscore or dash.</summary>
    private static bool IsValidAccountName(string name)
        => name.Length is > 0 and <= 32
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-');
        return cleaned.Length == 0 ? "box" : cleaned.ToLowerInvariant();
    }

    private static string Tail(string? text, int lines = 20)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(nothing)";
        var all = text.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
