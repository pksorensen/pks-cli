using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Moves this machine's working Codex configuration onto the box, so Codex Desktop's remote
/// connection reaches the same gateway the laptop does.
///
/// <para><b>Why this exists as a command.</b> <c>pks vm codex</c> leaves a box with the CLI, an
/// unprivileged account and an ssh alias — and no credentials, which is correct: pks does not know
/// what a gateway is. But the app then reports the remote account as logged out and offers an OpenAI
/// sign-in, which is the wrong door: completing it bills a ChatGPT account and bypasses the gateway
/// entirely. The configuration that <em>is</em> the authentication already exists on the laptop. It
/// just has to get there, with one change.</para>
///
/// <para><b>The division of labour is intact.</b> Nothing here authors configuration. The ContextOne
/// installer still decides base URLs, customer ids, model names and catalogues; this reads what it
/// produced. The one thing pks adds is the rewrite in <see cref="CodexConfigSync"/>, which is not a
/// policy decision but a property of ssh — and therefore belongs with the thing that knows about
/// ssh.</para>
/// </summary>
[Description("Copy this machine's Codex configuration to the box, rewritten so it works over SSH")]
public sealed class VmCodexSyncConfigCommand : AsyncCommand<VmCodexSyncConfigCommand.Settings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly ISshCommandRunner _ssh;
    private readonly ISshKeyStore _keyStore;
    private readonly IAnsiConsole _console;

    public VmCodexSyncConfigCommand(
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

    public sealed class Settings : VmCodexTargetSettings
    {
        [CommandOption("--from <DIR>")]
        [Description("Local Codex directory to copy from (default: CODEX_HOME, else ~/.codex)")]
        public string? From { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Take every file found and do not ask. Required when there is no terminal to ask at")]
        public bool Yes { get; set; }

        [CommandOption("--dry-run")]
        [Description("Say what would be written, and write nothing")]
        public bool DryRun { get; set; }

        [CommandOption("--alias <ALIAS>")]
        [Description("The ssh alias to name in the check command (default: <target>-<user>, as 'pks vm codex' wrote it)")]
        public string? Alias { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        _console.Write(new Rule("[cyan]Codex config → remote box[/]").LeftJustified());

        var localHome = string.IsNullOrWhiteSpace(settings.From)
            ? CodexConfigSync.DefaultLocalHome()
            : settings.From!;

        var found = CodexConfigSync.Discover(localHome);
        if (found.Count == 0)
        {
            _console.MarkupLine($"[red]Nothing to copy from[/] [cyan]{Markup.Escape(localHome)}[/][red].[/]");
            _console.MarkupLine("[dim]This command moves an existing configuration; it does not author one. Run the[/]");
            _console.MarkupLine("[dim]ContextOne installer on this machine first, or point at the folder with [bold]--from[/].[/]");
            return 1;
        }

        if (!found.Any(i => i.Kind == CodexSyncKind.Config))
        {
            _console.MarkupLine($"[red]No config.toml in[/] [cyan]{Markup.Escape(localHome)}[/][red].[/] [dim]It is the file that names the provider; without it the rest configures nothing.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]From:[/] [cyan]{Markup.Escape(localHome)}[/]");

        using var resolved = await VmCodexTarget.ResolveAsync(settings, _sshTargets, _keyStore, _console);
        if (resolved is null) return 1;

        var host = resolved.Host;
        _console.MarkupLine($"[green]To:[/] {Markup.Escape(resolved.Label)} [dim]({Markup.Escape(host.Username)}@{Markup.Escape(host.Host)}:{host.Port})[/] [green]account[/] [bold]{Markup.Escape(settings.User)}[/]");

        // Where the account's .codex actually is. Asked rather than assumed: useradd honours
        // /etc/default/useradd, and an account somebody made by hand may live anywhere at all.
        var remoteHome = await ReadRemoteHomeAsync(host, settings.User);
        if (remoteHome is null) return 1;
        var remoteCodexDir = $"{remoteHome.TrimEnd('/')}/.codex";

        var selected = Select(found, settings);
        if (selected.Count == 0)
        {
            _console.MarkupLine("[yellow]Nothing selected.[/]");
            return 0;
        }

        // Everything is read and rewritten before anything is sent. A box left with a new
        // config.toml and a catalogue that never arrived is worse than a box left alone.
        var prepared = new List<(CodexSyncItem Item, CodexRewriteResult Result)>();
        var fatal = false;
        foreach (var item in selected)
        {
            string text;
            try
            {
                text = File.ReadAllText(item.LocalPath);
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Could not read {Markup.Escape(item.Name)}: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }

            // A catalogue holds model names and gateway routes — no credential and no local path —
            // so it travels verbatim. Running it through the rewrite would only risk mangling JSON.
            var result = item.Kind == CodexSyncKind.Catalogue
                ? new CodexRewriteResult(text, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>())
                : CodexConfigSync.Rewrite(text, LookupVariable, remoteCodexDir);

            if (!result.Ok) fatal = true;
            prepared.Add((item, result));
        }

        PrintPlan(prepared, remoteCodexDir);

        if (fatal)
        {
            _console.WriteLine();
            _console.MarkupLine("[red]Nothing was written.[/] [dim]Each problem above would produce a box that looks configured and answers 401.[/]");

            var missing = prepared.SelectMany(p => p.Result.MissingVariables).Distinct().ToList();
            if (missing.Count > 0)
            {
                _console.WriteLine();
                _console.MarkupLine("[dim]A variable the config names is not set for this process. The installer writes it[/]");
                _console.MarkupLine("[dim]at User scope, so a terminal opened before it ran cannot see it — open a new one,[/]");
                _console.MarkupLine("[dim]or re-run the installer. Missing:[/]");
                foreach (var name in missing)
                    _console.MarkupLine($"   [grey85]{Markup.Escape(name)}[/]");
            }
            return 1;
        }

        if (settings.DryRun)
        {
            _console.WriteLine();
            _console.MarkupLine("[yellow]--dry-run:[/] [dim]nothing written.[/]");
            return 0;
        }

        if (!Confirm(settings))
        {
            _console.MarkupLine("[yellow]Left alone.[/]");
            return 0;
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (var (item, result) in prepared)
        {
            var destination = $"{remoteCodexDir}/{item.Name}";
            var script = WriteFileScript(settings.User, destination, stamp);

            SshCommandResult? write = null;
            await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync($"Writing {item.Name}…", async _ =>
                    // Down the pipe, never in an argv: a header value is a credential, and an ssh
                    // argv reaches the remote shell's history, ps output and any auditd execve rule.
                    write = await _ssh.RunWithStdinAsync(host, script, async stdin =>
                    {
                        await stdin.WriteAsync(result.Text);
                        await stdin.FlushAsync();
                    }));

            if (write is null || !write.Success)
            {
                _console.MarkupLine($"[red]{Markup.Escape(item.Name)} failed (exit {write?.ExitCode ?? -1}).[/]");
                _console.MarkupLine($"[dim]{Markup.Escape(Tail(write?.StdErr))}[/]");
                return 1;
            }

            _console.MarkupLine($"[green]✓[/] [cyan]{Markup.Escape(destination)}[/] [dim]({item.Bytes} B local)[/]");
        }

        await VerifyAsync(host, settings.User, remoteCodexDir);
        PrintNextSteps(settings, resolved, remoteCodexDir);
        return 0;
    }

    /// <summary>
    /// The variable's value on this machine. Process environment first, then the User scope the
    /// installer writes to: a terminal opened before the installer ran has the variable at User
    /// scope and not in its own environment, and reporting it missing would be wrong.
    /// </summary>
    private static string? LookupVariable(string name)
    {
        var fromProcess = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(fromProcess)) return fromProcess;

        if (!OperatingSystem.IsWindows()) return null;
        try { return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User); }
        catch { return null; }
    }

    /// <summary>
    /// The account's home directory, which also proves the account exists. A sync against a box that
    /// never ran <c>pks vm codex</c> would otherwise write into a directory nothing reads.
    /// </summary>
    private async Task<string?> ReadRemoteHomeAsync(RemoteHostConfig host, string user)
    {
        var probe = await _ssh.RunAsync(host, $"getent passwd '{user}' | cut -d: -f6");
        var home = probe.Success ? probe.StdOut.Trim() : string.Empty;

        if (string.IsNullOrWhiteSpace(home))
        {
            _console.MarkupLine($"[red]No account '{Markup.Escape(user)}' on the box.[/]");
            _console.MarkupLine("[dim]Create it first: [bold]pks vm codex[/] — same target, same [bold]--user[/].[/]");
            return null;
        }

        return home.Split('\n')[0].Trim();
    }

    /// <summary>
    /// The checklist. Everything found is pre-selected, because the ContextOne installer writes these
    /// files as a set: a config naming a catalogue that was not copied is a box with an empty model
    /// picker.
    /// </summary>
    private List<CodexSyncItem> Select(IReadOnlyList<CodexSyncItem> found, Settings settings)
    {
        if (settings.Yes || settings.DryRun) return found.ToList();

        if (!_console.Profile.Capabilities.Interactive)
        {
            _console.MarkupLine("[yellow]No terminal to ask at.[/] [dim]Pass [bold]--yes[/] to take every file found, or [bold]--dry-run[/] to see them.[/]");
            return new List<CodexSyncItem>();
        }

        var prompt = new MultiSelectionPrompt<string>()
            .Title("[cyan]Which files should go to the box?[/]")
            .HighlightStyle(Style.Parse("cyan"))
            .InstructionsText("[dim](space toggles, enter confirms)[/]")
            .NotRequired();

        foreach (var item in found)
            prompt.AddChoice(Describe(item));

        // Pre-ticked rather than merely listed: deselecting is the unusual act here.
        foreach (var item in found)
            prompt.Select(Describe(item));

        var chosen = _console.Prompt(prompt).ToHashSet(StringComparer.Ordinal);
        return found.Where(i => chosen.Contains(Describe(i))).ToList();
    }

    private static string Describe(CodexSyncItem item) => item.Kind switch
    {
        CodexSyncKind.Config => $"{item.Name}  (the provider, the header and the catalogue it points at)",
        CodexSyncKind.Profile => $"{item.Name}  (profile — what 'codex -p' layers)",
        _ => $"{item.Name}  (model catalogue)",
    };

    /// <summary>
    /// What will happen to each file, in a table, before anything is sent. The header value is shown
    /// masked: enough to tell one key from another, never enough to be a disclosure — and the value
    /// reaches neither a log nor the scrollback.
    /// </summary>
    private void PrintPlan(List<(CodexSyncItem Item, CodexRewriteResult Result)> prepared, string remoteCodexDir)
    {
        _console.WriteLine();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("File");
        table.AddColumn("Lands at");
        table.AddColumn("Rewritten");

        foreach (var (item, result) in prepared)
        {
            var changes = new List<string>();
            if (result.ResolvedVariables.Count > 0)
                changes.Add($"env_http_headers → http_headers ({result.ResolvedVariables.Count})");
            if (item.Kind != CodexSyncKind.Catalogue && result.Text.Contains("model_catalog_json", StringComparison.Ordinal))
                changes.Add("model_catalog_json → the box's path");
            if (changes.Count == 0) changes.Add("verbatim");

            table.AddRow(
                Markup.Escape(item.Name),
                $"[dim]{Markup.Escape(remoteCodexDir)}/[/]{Markup.Escape(item.Name)}",
                string.Join('\n', changes.Select(Markup.Escape)));
        }

        _console.Write(table);

        var variables = prepared.SelectMany(p => p.Result.ResolvedVariables).Distinct().ToList();
        if (variables.Count > 0)
        {
            _console.MarkupLine("[green]Keys inlined from this machine's environment:[/]");
            foreach (var name in variables)
                _console.MarkupLine($"   [grey85]{Markup.Escape(name)}[/] [dim]→ {Markup.Escape(Mask(LookupVariable(name)))}[/]");

            _console.WriteLine();
            _console.MarkupLine("[yellow]![/] [dim]The value ends up in the file, not in a variable — that is the point: a variable[/]");
            _console.MarkupLine("   [dim]named in the config is empty for the app server Codex starts over ssh, so the[/]");
            _console.MarkupLine($"   [dim]gateway answers 401. The file is 0600 and owned by the account.[/]");
        }

        foreach (var warning in prepared.SelectMany(p => p.Result.Warnings).Distinct())
            _console.MarkupLine($"[yellow]![/] {Markup.Escape(warning)}");

        foreach (var (item, result) in prepared)
        {
            foreach (var refusal in result.Refusals)
                _console.MarkupLine($"[red]✗[/] {Markup.Escape(item.Name)}: {Markup.Escape(refusal)}");
            foreach (var name in result.MissingVariables)
                _console.MarkupLine($"[red]✗[/] {Markup.Escape(item.Name)}: [bold]{Markup.Escape(name)}[/] is not set on this machine");
        }
    }

    /// <summary>Enough of a key to tell it from another one, and no more.</summary>
    private static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(empty)";
        return value.Length <= 4 ? new string('*', value.Length) : new string('*', 8) + value[^4..];
    }

    private bool Confirm(Settings settings)
    {
        if (settings.Yes) return true;
        if (!_console.Profile.Capabilities.Interactive)
        {
            _console.MarkupLine("[yellow]No terminal to confirm at.[/] [dim]Pass [bold]--yes[/].[/]");
            return false;
        }

        _console.WriteLine();
        return _console.Confirm("[cyan]Write these to the box?[/]", defaultValue: false);
    }

    /// <summary>
    /// One file, written through a temp the connecting account owns and then moved into place by
    /// root.
    ///
    /// Writing straight into the destination is not an option: the account's <c>.codex</c> is 0700
    /// and owned by the account, so a redirect from a non-root login shell cannot create a file
    /// there, and a redirect that could would leave a window in which the file is half-written.
    /// <c>install</c> from a real temp file rather than from stdin, because the destination has to be
    /// created with the right owner and mode in one step.
    /// </summary>
    private static string WriteFileScript(string user, string destination, string stamp) => $$"""
        set -eu

        CODEX_USER='{{user}}'
        DEST='{{destination}}'

        if [ "$(id -u)" -eq 0 ]; then
          pks_run() { "$@"; }
        else
          sudo -n true 2>/dev/null || { echo "pks vm codex sync-config: need root or passwordless sudo" >&2; exit 78; }
          pks_run() { sudo -n "$@"; }
        fi

        pks_run test -d "$(dirname "$DEST")" || pks_run install -d -m 0700 -o "$CODEX_USER" -g "$CODEX_USER" "$(dirname "$DEST")"

        # mktemp already creates at 0600, and the umask covers anything that follows.
        umask 077
        TMP=$(mktemp)
        trap 'rm -f "$TMP"' EXIT
        cat > "$TMP"

        # Keep whatever was there. A config somebody edited on the box by hand is not ours to drop,
        # and the stamp makes the one from this run findable.
        if pks_run test -f "$DEST"; then
          pks_run cp -p "$DEST" "$DEST.bak-{{stamp}}"
        fi

        pks_run install -m 0600 -o "$CODEX_USER" -g "$CODEX_USER" "$TMP" "$DEST"
        """;

    /// <summary>
    /// Checks the things that cost nothing: owner, mode, and that no variable survived into the
    /// file. Deliberately not a model call — proving the gateway answers costs money, and the one
    /// command that does it is printed for the reader to run instead.
    /// </summary>
    private async Task VerifyAsync(RemoteHostConfig host, string user, string remoteCodexDir)
    {
        var config = $"{remoteCodexDir}/{CodexConfigSync.ConfigFileName}";
        var check = await _ssh.RunAsync(host,
            $"stat -c '%U %a' '{config}' 2>/dev/null; grep -c 'env_http_headers' '{config}' 2>/dev/null || true");

        var lines = check.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var ownerAndMode = lines.FirstOrDefault() ?? string.Empty;
        var survivors = lines.Skip(1).FirstOrDefault() ?? "0";

        _console.WriteLine();
        if (ownerAndMode.StartsWith($"{user} 600", StringComparison.Ordinal))
            _console.MarkupLine($"[green]✓[/] [dim]{Markup.Escape(config)} is 0600 and owned by {Markup.Escape(user)}[/]");
        else
            _console.MarkupLine($"[yellow]![/] [dim]expected '{Markup.Escape(user)} 600' for {Markup.Escape(config)}, got '{Markup.Escape(ownerAndMode)}'[/]");

        if (survivors != "0")
            _console.MarkupLine($"[red]✗[/] the config on the box still names {Markup.Escape(survivors)} environment variable(s) — the gateway will answer 401");
    }

    private void PrintNextSteps(Settings settings, VmCodexResolvedTarget resolved, string remoteCodexDir)
    {
        var alias = settings.Alias ?? $"{Sanitize(resolved.Label)}-{settings.User}";

        _console.WriteLine();
        _console.Write(new Rule("[cyan]Check it, then reconnect[/]").LeftJustified());
        _console.MarkupLine("[dim]A non-interactive, non-login shell — the harshest case for the header, and exactly[/]");
        _console.MarkupLine("[dim]what Codex Desktop gets. If this answers, the configuration is proven:[/]");
        _console.WriteLine();
        _console.MarkupLine($"   [grey85]ssh {Markup.Escape(alias)} 'codex exec \"reply ok\"'[/]");
        _console.WriteLine();
        _console.MarkupLine($"[dim]Then in Codex: Settings → Connections, disconnect and reconnect [bold]{Markup.Escape(alias)}[/].[/]");
        _console.MarkupLine($"[dim]An app server already listening on {VmCodexBootstrapScript.AppServerPort} holds the old configuration.[/]");
        _console.WriteLine();
        _console.MarkupLine("[dim]Do [bold]not[/] use the app's \"Sign in to Codex\" button: it signs the account into a[/]");
        _console.MarkupLine("[dim]ChatGPT account and bypasses the gateway entirely. With a provider configured there[/]");
        _console.MarkupLine("[dim]is no sign-in — the configuration is the authentication.[/]");
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-');
        return cleaned.Length == 0 ? "box" : cleaned.ToLowerInvariant();
    }

    private static string Tail(string? text, int lines = 12)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(nothing)";
        var all = text.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }
}
