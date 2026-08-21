using System.ComponentModel;
using System.Reflection;
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
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _azureVm;
    private readonly VmInitCommand _vmInit;
    private readonly IAnsiConsole _console;

    public T3InitCommand(
        ISshTargetConfigurationService sshTargets,
        ISshCommandRunner ssh,
        IEntraApplicationService entra,
        IAzureFoundryAuthService foundry,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService azureVm,
        VmInitCommand vmInit,
        IAnsiConsole console)
    {
        _sshTargets = sshTargets;
        _ssh = ssh;
        _entra = entra;
        _foundry = foundry;
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _azureVm = azureVm;
        _vmInit = vmInit;
        _console = console;
    }

    public sealed class Settings : T3Settings
    {
        [CommandOption("--vm <NAME>")]
        [Description("Use this existing SSH target / VM instead of asking (skips provisioning)")]
        public string? Vm { get; set; }

        [CommandOption("--domain <FQDN>")]
        [Description("Use your own hostname instead of the Azure-assigned one (must already point at the box)")]
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
        // Entra rejects a non-HTTPS web redirect URI outside localhost, HTTPS needs a certificate,
        // and a certificate needs a name that resolves. On Azure that name is free — a DNS label on
        // the VM's public IP yields <label>.<region>.cloudapp.azure.com — so the command claims one
        // and opens 80/443 rather than asking the operator to go and build the prerequisite by hand.
        var domain = settings.Domain;
        if (string.IsNullOrWhiteSpace(domain))
            domain = await TryClaimAzureHostnameAsync(target);

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
        // Only the deployment name is decided here; the credential itself is delivered in step 7,
        // after the box exists and has somewhere safe to put it.
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
            PrintSummary(domain, redirectUri, app, deployment!, target, bootstrapped: false, foundryReady: false);
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

        // ── 7. Foundry, without a second terminal ───────────────────────────────────────────────
        // The draft ended here and told the operator to ssh in, port-forward, and sign in to Azure
        // on the box. That put the same refresh token on the same machine — it just made a person
        // do it. What is worth controlling is who on the box can read it, and the delivery target
        // is a service account no agent runs as.
        var foundryReady = false;
        if (await _foundry.IsAuthenticatedAsync())
        {
            if (await TryInstallPksOnBoxAsync(host))
            {
                await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                    .StartAsync("Handing the box its Foundry credential…", async _ =>
                    {
                        var wrote = false;
                        var result = await _ssh.RunWithStdinAsync(host, T3BootstrapScript.FoundryCredentialDeliveryScript(),
                            async w => wrote = await _foundry.WriteRemoteSettingsAsync(w));

                        foundryReady = wrote && result.Success;
                        if (!foundryReady)
                            _console.MarkupLine($"[yellow]The Foundry passthrough is not running yet (exit {result.ExitCode}):[/] {Markup.Escape(Tail(result.StdErr, 5))}");
                    });
            }
        }
        else
        {
            _console.MarkupLine("[yellow]No Foundry credential stored locally[/] [dim]— run [bold]pks foundry init[/] here, then re-run this command.[/]");
        }

        if (foundryReady) _console.MarkupLine("[green]Foundry passthrough is live.[/]");

        PrintSummary(domain, redirectUri, app, deployment!, target, bootstrapped: true, foundryReady: foundryReady);
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

    /// <summary>
    /// Claims <c>&lt;label&gt;.&lt;region&gt;.cloudapp.azure.com</c> for the box and opens 80/443, or
    /// returns null and leaves the caller to ask.
    ///
    /// This is the step that turns the command into one command. Without it the operator is told to
    /// go and create an A record and open two ports before anything else can work, which is most of
    /// the manual labour the command exists to remove — and on Azure the name is already there for
    /// the asking. Null means "this is not an Azure VM we provisioned", not "it failed": a Scaleway
    /// box, a hand-registered SSH target, or an unreadable subscription all fall back to the prompt.
    /// </summary>
    private async Task<string?> TryClaimAzureHostnameAsync(SshTarget target)
    {
        var vmName = target.Label ?? target.Host;
        var record = await _vmMetadata.FindAsync(vmName);
        if (record is null || !string.Equals(record.Provider, "azure", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(record.SubscriptionId) || string.IsNullOrWhiteSpace(record.ResourceGroup))
            return null;

        try
        {
            var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
            if (string.IsNullOrWhiteSpace(token))
            {
                _console.MarkupLine("[yellow]Not signed in to Azure[/] [dim]— falling back to asking for a hostname.[/]");
                return null;
            }

            string? fqdn = null;
            await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync("Claiming a public hostname and opening 80/443…", async _ =>
                {
                    fqdn = await _azureVm.EnsurePublicIpDnsLabelAsync(
                        token!, record.SubscriptionId, record.ResourceGroup, record.VmName, record.VmName);

                    // Both are needed and neither is enough: Caddy's ACME challenge arrives on 80,
                    // and the browser then wants 443. `pks vm init` opens only 22.
                    await _azureVm.EnsureInboundPortsAsync(
                        token!, record.SubscriptionId, record.ResourceGroup, record.VmName,
                        "AllowWeb", new[] { "80", "443" });
                });

            if (string.IsNullOrWhiteSpace(fqdn)) return null;

            _console.MarkupLine($"[green]Hostname:[/] {Markup.Escape(fqdn!)} [dim](Azure-assigned; ports 80/443 open)[/]");
            return fqdn;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Could not set up the public hostname automatically:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// Pushes the embedded linux-x64 pks to <c>/usr/local/bin/pks</c>, or explains why it cannot.
    ///
    /// Only builds made with <c>-p:EmbedPksLinux=true</c> carry one. When it is absent the Foundry
    /// half is skipped rather than half-configured, because the alternatives are both wrong: the npm
    /// package of that name is a different project entirely, and the NuGet one needs a .NET SDK on
    /// a box that otherwise needs no runtime at all.
    /// </summary>
    private async Task<bool> TryInstallPksOnBoxAsync(RemoteHostConfig host)
    {
        await using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream("pks-linux-x64");
        if (embedded is null)
        {
            _console.MarkupLine("[yellow]This build carries no linux pks binary[/] [dim]— skipping the Foundry passthrough.[/]");
            return false;
        }

        using var buffer = new MemoryStream();
        await embedded.CopyToAsync(buffer);

        var ok = false;
        await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync($"Installing pks on the box ({buffer.Length / (1024 * 1024)} MB)…", async _ =>
            {
                var result = await _ssh.RunWithStdinAsync(host, T3BootstrapScript.PksBinaryDeliveryScript(), async w =>
                {
                    await w.FlushAsync();
                    buffer.Position = 0;
                    // Straight at the byte stream: this is an executable, and the TextWriter would
                    // re-encode it.
                    await buffer.CopyToAsync(w.BaseStream);
                    await w.BaseStream.FlushAsync();
                });

                ok = result.Success;
                if (!ok)
                    _console.MarkupLine($"[yellow]Could not install pks on the box (exit {result.ExitCode}):[/] {Markup.Escape(Tail(result.StdErr, 5))}");
            });

        return ok;
    }

    private void PrintSummary(string domain, string redirectUri, EntraAppResult app, string deployment,
        SshTarget target, bool bootstrapped, bool foundryReady)
    {
        var url = $"https://{domain}";

        var body = $"""
            [green]Open this and sign in with your Microsoft account:[/]

                [bold]{Markup.Escape(url)}[/]

            [cyan1]Sign-in:[/]        Microsoft Entra ID (oauth2-proxy), app {Markup.Escape(app.App.AppId)}
            [cyan1]Redirect URI:[/]   {Markup.Escape(redirectUri)} [dim]— already registered[/]
            [cyan1]Agent:[/]          codex → Azure AI Foundry, deployment [bold]{Markup.Escape(deployment)}[/]
            [cyan1]SSH:[/]            pks ssh connect {Markup.Escape(target.Label ?? target.Host)}
            """;

        if (!foundryReady)
            body += $"""


                [yellow]The Foundry passthrough is not running.[/] T3 will start and you can sign in,
                but codex has no model until this box has a credential:

                  [dim]pks foundry init[/]   [dim]# here, on this machine[/]
                  [dim]pks t3 init --vm {Markup.Escape(target.Label ?? target.Host)}[/]   [dim]# then re-run; everything else is idempotent[/]
                """;

        body += """


            [dim]The certificate is fetched on the first request, so give the first load a few
            seconds. https://app.t3.codes will not work against this box — that client reaches the
            backend directly with a pairing token, which the Entra gate cannot satisfy; use the UI
            served at the URL above.[/]
            """;

        _console.Write(new Panel(body)
            .Border(BoxBorder.Rounded)
            .BorderStyle(bootstrapped ? (foundryReady ? "green" : "yellow") : "yellow")
            .Header(bootstrapped ? (foundryReady ? " [bold green]Ready[/] " : " [bold yellow]Almost[/] ") : " [bold yellow]Planned[/] "));
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
