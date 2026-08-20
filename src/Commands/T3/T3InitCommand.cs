using System.ComponentModel;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Entra;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.T3;

/// <summary>
/// Stands up a private T3 Code box: a VM, T3 Code running on it, Codex wired to Azure AI Foundry,
/// and Microsoft Entra ID as the front door.
///
///   dnx pks-cli t3 init
///
/// The three pieces this glues together already exist as commands — <c>pks vm init</c>,
/// <c>pks foundry</c>, <c>pks entra app init</c> — and the value here is not any one of them but the
/// order and the six values that have to agree across them: the VM's IP, the DNS name pointing at
/// it, the redirect URI derived from that name, the Entra app that URI is registered on, the client
/// secret that app minted, and the port oauth2-proxy forwards to. Done by hand that is a portal tab,
/// two terminals and a copy-paste of a client secret; the copy-paste is the part worth removing.
///
/// ── What this does NOT do ──────────────────────────────────────────────────────────────────────
/// T3 Code has no OIDC of its own. Its remote story is a one-time pairing token, optionally over
/// Tailscale. "Log in with Entra" therefore cannot be a T3 setting — it is oauth2-proxy in front of
/// a loopback-bound T3, and the consequence is that the hosted client at https://app.t3.codes stops
/// being usable against this box: that client talks to the backend directly with a pairing token,
/// which a cookie-based OIDC gate has no way to satisfy. You get T3's own web UI on your own domain
/// instead. If you would rather keep app.t3.codes, the answer is Tailscale (`t3 serve
/// --tailscale-serve`) and no Entra app at all.
/// </summary>
[Description("Provision a VM running T3 Code, wired to Azure AI Foundry and gated by Entra ID")]
public sealed class T3InitCommand : AsyncCommand<T3InitCommand.Settings>
{
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly ISshCommandRunner _ssh;
    private readonly IEntraApplicationService _entra;
    private readonly IAzureFoundryAuthService _foundry;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly VmInitCommand _vmInit;
    private readonly IAnsiConsole _console;

    public T3InitCommand(
        ISshTargetConfigurationService sshTargets,
        ISshCommandRunner ssh,
        IEntraApplicationService entra,
        IAzureFoundryAuthService foundry,
        IAzureVmMetadataService vmMetadata,
        VmInitCommand vmInit,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _ssh = ssh;
        _entra = entra;
        _foundry = foundry;
        _vmMetadata = vmMetadata;
        _vmInit = vmInit;
        _console = console;
    }

    public sealed class Settings : T3Settings
    {
        [CommandOption("--vm <NAME>")]
        [Description("Use this existing SSH target / VM instead of asking (skips provisioning)")]
        public string? Vm { get; set; }

        [CommandOption("--domain <FQDN>")]
        [Description("Public hostname for the box, e.g. t3.example.com — must already point at its IP")]
        public string? Domain { get; set; }

        [CommandOption("--tenant <TENANT_ID>")]
        [Description("Entra tenant the login app lives in (default: the tenant pks is signed in to)")]
        public string? TenantId { get; set; }

        [CommandOption("--deployment <NAME>")]
        [Description("Foundry deployment Codex runs on (default: the one pks foundry selected)")]
        public string? Deployment { get; set; }

        [CommandOption("--alias <ALIAS>")]
        [Description("Local alias for the Entra app registration (default: t3-<vm>)")]
        public string? Alias { get; set; }

        [CommandOption("--acme-email <EMAIL>")]
        [Description("Contact address for Let's Encrypt (default: admin@<domain>)")]
        public string? AcmeEmail { get; set; }

        [CommandOption("--rotate")]
        [Description("Mint a fresh client secret even if a live one is stored for the alias")]
        public bool Rotate { get; set; }

        [CommandOption("--skip-bootstrap")]
        [Description("Do the Entra + planning half and print the remote script instead of running it")]
        public bool SkipBootstrap { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        _console.Write(new Rule("[cyan]T3 Code — private box[/]").LeftJustified());

        // ── 1. The VM ───────────────────────────────────────────────────────────────────────────
        var target = await ResolveTargetAsync(context, settings);
        if (target is null) return 1;

        _console.MarkupLine($"[green]VM:[/] {Markup.Escape(target.Label ?? target.Host)} [dim]({Markup.Escape(target.Host)})[/]");

        // ── 2. The name Entra will redirect back to ─────────────────────────────────────────────
        // Asked for rather than derived: Entra rejects a non-HTTPS web redirect URI outside
        // localhost, HTTPS needs a certificate, and a certificate needs a name that resolves. There
        // is no default that can be guessed from an IP address.
        var domain = settings.Domain;
        if (string.IsNullOrWhiteSpace(domain))
        {
            _console.MarkupLine("[dim]Entra will only accept an HTTPS redirect URI, so the box needs a DNS name.[/]");
            _console.MarkupLine($"[dim]Point an A record at [bold]{Markup.Escape(target.Host)}[/] before continuing — Caddy fetches the certificate during bootstrap.[/]");
            domain = _console.Prompt(new TextPrompt<string>("[cyan]Public hostname for this box:[/]")
                .Validate(d => d.Contains('.') && !d.Contains('/')
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Give a bare FQDN, no scheme and no path[/]")));
        }

        var redirectUri = T3BootstrapScript.RedirectUriFor(domain);

        // ── 3. Foundry ──────────────────────────────────────────────────────────────────────────
        // Only the deployment name is decided here. The VM authenticates to Foundry itself: the
        // stored credential is a user refresh token tied to this machine, and copying it to a box
        // that is by design reachable from the internet would be the wrong trade even if it worked.
        var deployment = settings.Deployment;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            var creds = await _foundry.IsAuthenticatedAsync() ? await _foundry.GetStoredCredentialsAsync() : null;
            var suggested = string.IsNullOrWhiteSpace(creds?.DefaultModel) ? "gpt-5-codex" : creds!.DefaultModel!;
            deployment = _console.Prompt(new TextPrompt<string>("[cyan]Foundry deployment for Codex:[/]").DefaultValue(suggested));
        }

        // ── 4. The Entra app ────────────────────────────────────────────────────────────────────
        if (!await _entra.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not signed in to Microsoft Graph.[/] [dim]Run [bold]pks entra app list[/] once to sign in, then retry.[/]");
            return 1;
        }

        var who = await _entra.WhoAmIAsync();
        var tenantId = settings.TenantId ?? who?.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            _console.MarkupLine("[red]Could not determine the tenant.[/] [dim]Pass [bold]--tenant <id>[/].[/]");
            return 1;
        }

        var alias = settings.Alias ?? $"t3-{Sanitize(target.Label ?? target.Host)}";
        var displayName = $"T3 Code — {domain}";

        _console.MarkupLine($"[dim]Registering [bold]{Markup.Escape(redirectUri)}[/] on [bold]{Markup.Escape(displayName)}[/] as [bold]{Markup.Escape(who?.UserPrincipalName ?? "?")}[/]…[/]");

        EntraAppResult app;
        try
        {
            app = await _entra.InitAsync(new EntraAppRequest
            {
                DisplayName = displayName,
                Alias = alias,
                TenantId = tenantId,
                RedirectUris = { redirectUri },
                Rotate = settings.Rotate,
            });
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Entra app registration failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        _console.MarkupLine($"[green]Entra app:[/] {Markup.Escape(app.App.AppId)} " +
            $"[dim]({(app.CreatedApplication ? "created" : "adopted")}{(app.MintedSecret ? ", secret minted" : ", existing secret")})[/]");

        // ── 5. Bootstrap ────────────────────────────────────────────────────────────────────────
        var remoteHome = target.Username == "root" ? "/root" : $"/home/{target.Username}";
        var script = T3BootstrapScript.Build(new T3BootstrapOptions
        {
            Domain = domain,
            TenantId = tenantId,
            RemoteUser = target.Username,
            RemoteHome = remoteHome,
            FoundryDeployment = deployment!,
            AcmeEmail = settings.AcmeEmail,
        });

        if (settings.SkipBootstrap)
        {
            _console.MarkupLine("[yellow]--skip-bootstrap:[/] [dim]nothing was run on the VM. Script follows.[/]");
            _console.WriteLine();
            Console.WriteLine(script);
            PrintSummary(domain, redirectUri, app, deployment!, target, bootstrapped: false);
            return 0;
        }

        var host = new RemoteHostConfig
        {
            Host = target.Host,
            Username = target.Username,
            Port = target.Port,
            KeyPath = target.KeyPath,
        };

        var bootstrapOk = false;
        await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Bootstrapping the VM (node, t3, codex, caddy, oauth2-proxy)…", async _ =>
            {
                var result = await _ssh.RunAsync(host, script);
                bootstrapOk = result.Success;
                if (!result.Success)
                    _console.MarkupLine($"[red]Bootstrap failed (exit {result.ExitCode}):[/]\n{Markup.Escape(Tail(result.StdErr))}");
            });

        if (!bootstrapOk) return 1;
        _console.MarkupLine("[green]Bootstrap complete.[/]");

        // ── 6. The credentials, down the pipe ───────────────────────────────────────────────────
        // Not in the command line: that becomes an ssh argv, which the remote login shell records.
        // SecretSink does the writing so this file never names Reveal() — the gate test under
        // src/Commands/ would fail the build if it did.
        var cookieSecret = NewCookieSecret();
        var foundryToken = Guid.NewGuid().ToString("N");

        var secretsOk = false;
        await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Delivering credentials…", async _ =>
            {
                var o2p = await _ssh.RunWithStdinAsync(host, T3BootstrapScript.SecretDeliveryScript(), async w =>
                {
                    await w.WriteLineAsync($"OAUTH2_PROXY_CLIENT_ID={app.App.AppId}");
                    SecretSink.WriteEnvLine(w, "OAUTH2_PROXY_CLIENT_SECRET", app.App.ClientSecret);
                    await w.WriteLineAsync($"OAUTH2_PROXY_COOKIE_SECRET={cookieSecret}");
                    await w.FlushAsync();
                });

                if (!o2p.Success)
                {
                    _console.MarkupLine($"[red]Could not install the oauth2-proxy credentials (exit {o2p.ExitCode}):[/]\n{Markup.Escape(Tail(o2p.StdErr))}");
                    return;
                }

                var fnd = await _ssh.RunWithStdinAsync(host, T3BootstrapScript.FoundryTokenDeliveryScript(target.Username), async w =>
                {
                    await w.WriteLineAsync($"PKS_FOUNDRY_PROXY_TOKEN={foundryToken}");
                    await w.WriteLineAsync($"PKS_CODEX_TOKEN={foundryToken}");
                    await w.FlushAsync();
                });

                if (!fnd.Success)
                {
                    _console.MarkupLine($"[red]Could not install the Foundry passthrough token (exit {fnd.ExitCode}):[/]\n{Markup.Escape(Tail(fnd.StdErr))}");
                    return;
                }

                secretsOk = true;
            });

        if (!secretsOk) return 1;
        _console.MarkupLine("[green]Credentials installed.[/]");

        PrintSummary(domain, redirectUri, app, deployment!, target, bootstrapped: true);
        return 0;
    }

    /// <summary>
    /// Either an SSH target that already exists, or a fresh VM via the existing <c>pks vm init</c>.
    ///
    /// The chain mirrors what <c>vm init</c> itself does with <c>azure init</c>: run the other
    /// command, then read what it left behind. <c>VmInitCommand</c> returns an exit code and not a
    /// target, so the join is the newest SSH target registered after it ran — good enough while
    /// nothing else registers targets concurrently, and the reason a draft should not rely on it
    /// forever.
    /// </summary>
    private async Task<SshTarget?> ResolveTargetAsync(CommandContext context, Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Vm))
        {
            var found = await _sshTargets.FindTargetAsync(settings.Vm!);
            if (found is null)
                _console.MarkupLine($"[red]No SSH target '{Markup.Escape(settings.Vm!)}'.[/] [dim]See [bold]pks ssh list[/].[/]");
            return found;
        }

        var existing = await _sshTargets.ListTargetsAsync();
        const string ProvisionOption = "+ Provision a new VM (pks vm init)";
        var choices = new List<string> { ProvisionOption };
        choices.AddRange(existing.Select(t => t.Label ?? t.Host));

        var choice = _console.Prompt(new SelectionPrompt<string>()
            .Title("[cyan]Which machine should run T3 Code?[/]")
            .HighlightStyle(Style.Parse("cyan"))
            .AddChoices(choices));

        if (choice != ProvisionOption)
            return existing.First(t => (t.Label ?? t.Host) == choice);

        var before = existing.Select(t => t.Id).ToHashSet();
        var rc = _vmInit.Execute(context, new VmInitCommand.Settings());
        if (rc != 0)
        {
            _console.MarkupLine("[red]VM provisioning did not complete.[/]");
            return null;
        }

        var after = await _sshTargets.ListTargetsAsync();
        var fresh = after.Where(t => !before.Contains(t.Id)).OrderByDescending(t => t.RegisteredAt).FirstOrDefault();
        if (fresh is null)
            _console.MarkupLine("[red]The VM was created but no SSH target was registered for it.[/]");
        return fresh;
    }

    private void PrintSummary(string domain, string redirectUri, EntraAppResult app, string deployment, SshTarget target, bool bootstrapped)
    {
        _console.Write(new Panel($"""
            [green]T3 Code is set up on[/] [bold]https://{Markup.Escape(domain)}[/]

            [cyan1]Sign-in:[/]        Microsoft Entra ID (oauth2-proxy)
            [cyan1]Redirect URI:[/]   {Markup.Escape(redirectUri)}
                              [dim]already registered on app {Markup.Escape(app.App.AppId)} — nothing to paste[/]
            [cyan1]Agent:[/]          codex → Azure AI Foundry, deployment [bold]{Markup.Escape(deployment)}[/]
            [cyan1]SSH:[/]            pks ssh connect {Markup.Escape(target.Label ?? target.Host)}

            [yellow]Two things still need you:[/]

            [bold]1.[/] DNS. [bold]{Markup.Escape(domain)}[/] must resolve to [bold]{Markup.Escape(target.Host)}[/],
               and ports 80 and 443 must be open, or Caddy cannot get a certificate.

            [bold]2.[/] Foundry sign-in on the box. The passthrough needs its own credential:

                 [dim]ssh -L 8400:localhost:8400 {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)}[/]
                 [dim]pks foundry init          # open the printed URL in your local browser[/]
                 [dim]sudo systemctl enable --now pks-foundry-proxy[/]

            [dim]https://app.t3.codes will not work against this box — that client reaches the
            backend directly with a pairing token, which the Entra gate cannot satisfy. Use the UI
            served at the domain above.[/]
            """)
            .Border(BoxBorder.Rounded)
            .BorderStyle(bootstrapped ? "green" : "yellow")
            .Header(bootstrapped ? " [bold green]Ready[/] " : " [bold yellow]Planned[/] "));
    }

    /// <summary>32 random bytes, base64url — what oauth2-proxy wants for its cookie signing key.</summary>
    private static string NewCookieSecret()
    {
        // oauth2-proxy's own documented generator is `openssl rand -base64 32 | tr -- '+/' '-_'`,
        // which keeps the padding. Whether an unpadded value decodes cleanly varies by release, so
        // match the documented form rather than find out.
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' ? char.ToLowerInvariant(c) : '-').ToArray());

    private static string Tail(string s, int lines = 20)
    {
        var all = s.Split('\n');
        return all.Length <= lines ? s : string.Join('\n', all[^lines..]);
    }
}
