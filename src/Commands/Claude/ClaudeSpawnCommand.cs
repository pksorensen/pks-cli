using PKS.Commands.Devcontainer;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Claude;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PKS.Commands.Claude;

[Description("Spawn a devcontainer on a remote SSH target and connect via claude")]
public class ClaudeSpawnCommand : DevcontainerSpawnCommand
{
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureFoundryAuthService _foundryAuthService;
    private readonly AzureFoundryAuthConfig _foundryConfig;

    public ClaudeSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IClaudeMarketplaceConfigurationService claudeMarketplaceConfigService,
        IClaudeManagedSettingsRenderer claudeManagedSettingsRenderer,
        IAzureFoundryAuthService foundryAuthService,
        AzureFoundryAuthConfig foundryConfig,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService,
               vmInitCommand, claudeMarketplaceConfigService, claudeManagedSettingsRenderer, console)
    {
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _foundryAuthService = foundryAuthService;
        _foundryConfig = foundryConfig;
    }

    protected override async Task OnAfterRemoteSpawnAsync(
        SshTarget target, string sshArgs, string projectName, string? containerId,
        string remoteWorkspaceFolder, string volumeName, Settings settings)
    {
        // Resolve container ID from the volume label if devcontainer up didn't return one
        if (string.IsNullOrEmpty(containerId))
        {
            var found = await RunSshCommandAsync(sshArgs, target,
                $"docker ps -q --filter label=vsc.devcontainer.volume.name={volumeName}");
            containerId = found.Output.Trim().Split('\n')[0].Trim();
        }

        if (string.IsNullOrEmpty(containerId))
        {
            Console.MarkupLine("[red]Could not find running devcontainer to attach to.[/]");
            return;
        }

        Console.MarkupLine($"[dim]Attaching claude to container {containerId[..Math.Min(12, containerId.Length)]}...[/]");

        // Interactive SSH: -t allocates a pseudo-TTY, no BatchMode so stdin works
        var interactiveSshArgs = $"-o StrictHostKeyChecking=no -p {target.Port}";
        if (!string.IsNullOrEmpty(target.KeyPath))
            interactiveSshArgs += $" -i \"{target.KeyPath}\"";

        string remoteCmd;
        if (await _foundryAuthService.IsAuthenticatedAsync())
        {
            const string UseFoundry = "Use Foundry models (Azure AI)";
            const string UseDirect = "Use Anthropic direct";
            var launchMode = Console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Azure AI Foundry is configured. Launch mode:[/]")
                    .AddChoices(UseFoundry, UseDirect));

            if (launchMode == UseFoundry)
            {
                var creds = await _foundryAuthService.GetStoredCredentialsAsync();
                var enabledModels = creds!.EnabledModels.Count > 0 ? creds.EnabledModels : new List<string> { creds.DefaultModel };

                var msiPort = 40342;
                var msiSecret = Guid.NewGuid().ToString("N");

                // Kill anything holding the MSI port (by port — covers any script name or stale process).
                await RunSshCommandAsync(sshArgs, target,
                    $"fuser -k {msiPort}/tcp 2>/dev/null || true; sleep 0.5",
                    timeoutSeconds: 5);

                // Copy minimal foundry credentials (TenantId + RefreshToken) to the VM.
                var credsPayload = JsonSerializer.Serialize(new { creds!.TenantId, creds.RefreshToken });
                var credsB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credsPayload));
                await RunSshCommandAsync(sshArgs, target,
                    $"mkdir -p ~/.pks-cli && echo {credsB64} | base64 -d > ~/.pks-cli/foundry-credentials.json && chmod 600 ~/.pks-cli/foundry-credentials.json",
                    timeoutSeconds: 10);

                // Deploy a real MSI token server: validates X-IDENTITY-HEADER, rejects non-cognitiveservices
                // resources, exchanges the stored refresh token for a live access token, and returns
                // the standard Azure MSI JSON response that DefaultAzureCredential expects.
                var pythonScript = $@"#!/usr/bin/env python3
import http.server, json, urllib.request, urllib.parse, os, datetime
CREDS = os.path.expanduser('~/.pks-cli/foundry-credentials.json')
ALLOWED = 'https://cognitiveservices.azure.com'
CLIENT_ID = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'
PORT = {msiPort}
SECRET = '{msiSecret}'
LOG = '/tmp/pks-msi-server.log'
def log(msg):
    with open(LOG, 'a') as f:
        f.write(datetime.datetime.utcnow().isoformat() + ' ' + msg + '\n')
def get_token(tenant_id, refresh_tok):
    data = urllib.parse.urlencode({{'grant_type': 'refresh_token', 'client_id': CLIENT_ID,
        'refresh_token': refresh_tok, 'scope': 'https://cognitiveservices.azure.com/.default'}}).encode()
    req = urllib.request.Request('https://login.microsoftonline.com/' + tenant_id + '/oauth2/v2.0/token',
        data=data, method='POST')
    req.add_header('Content-Type', 'application/x-www-form-urlencoded')
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.loads(r.read())
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.headers.get('X-IDENTITY-HEADER', '') != SECRET:
            log('REJECT bad header')
            self.send_response(401); self.end_headers(); return
        params = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
        resource = params.get('resource', [None])[0]
        if resource != ALLOWED:
            log('REJECT resource=' + repr(resource))
            self.send_response(403); self.end_headers()
            self.wfile.write(b'{{""error"":""resource_not_allowed""}}'); return
        try:
            with open(CREDS) as f: c = json.load(f)
            tok = get_token(c['TenantId'], c['RefreshToken'])
            if 'refresh_token' in tok:
                c['RefreshToken'] = tok['refresh_token']
                with open(CREDS, 'w') as f: json.dump(c, f)
            body = json.dumps({{'access_token': tok['access_token'],
                'expires_in': str(tok.get('expires_in', 3600)),
                'resource': ALLOWED, 'token_type': 'Bearer'}}).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers(); self.wfile.write(body)
            log('TOKEN issued ok')
        except Exception as e:
            log('ERROR ' + str(e))
            self.send_response(500); self.end_headers()
            self.wfile.write(json.dumps({{'error': str(e)}}).encode())
    def log_message(self, *a): pass
with open('/tmp/pks-msi-server.pid', 'w') as f: f.write(str(os.getpid()))
log('MSI token server starting port=' + str(PORT))
http.server.HTTPServer(('0.0.0.0', PORT), H).serve_forever()
";
                var scriptB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pythonScript));
                var deployScript = $"echo {scriptB64} | base64 -d > /tmp/pks-msi-server.py && nohup python3 /tmp/pks-msi-server.py >/tmp/pks-msi-server-out.log 2>&1 &";
                await RunSshCommandAsync(sshArgs, target, deployScript, timeoutSeconds: 10);
                await Task.Delay(800); // give python a moment to bind
                Console.MarkupLine($"[dim]MSI token server started on VM port {msiPort}. Check /tmp/pks-msi-server.log after the session to verify token issuance.[/]");

                // Build CLAUDE_CODE_USE_FOUNDRY env vars — model names map to role-specific vars
                var envVars = new System.Text.StringBuilder();
                envVars.Append($"-e CLAUDE_CODE_USE_FOUNDRY=1 ");
                envVars.Append($"-e ANTHROPIC_FOUNDRY_RESOURCE={creds.SelectedResourceName} ");
                envVars.Append($"-e IDENTITY_ENDPOINT=http://172.17.0.1:{msiPort} ");
                envVars.Append($"-e IDENTITY_HEADER={msiSecret} ");

                if (!string.IsNullOrEmpty(creds.ApiKey))
                    envVars.Append($"-e ANTHROPIC_FOUNDRY_API_KEY={creds.ApiKey} ");

                // Map deployment names to model-role env vars by looking for sonnet/opus/haiku in the name
                foreach (var model in enabledModels)
                {
                    var lower = model.ToLowerInvariant();
                    if (lower.Contains("sonnet")) envVars.Append($"-e ANTHROPIC_DEFAULT_SONNET_MODEL={model} ");
                    else if (lower.Contains("opus")) envVars.Append($"-e ANTHROPIC_DEFAULT_OPUS_MODEL={model} ");
                    else if (lower.Contains("haiku")) envVars.Append($"-e ANTHROPIC_DEFAULT_HAIKU_MODEL={model} ");
                    else envVars.Append($"-e ANTHROPIC_DEFAULT_SONNET_MODEL={model} ");
                }

                remoteCmd = $"docker exec -it {envVars}-w {remoteWorkspaceFolder} {containerId} claude --dangerously-skip-permissions";
            }
            else
            {
                remoteCmd = $"docker exec -it -w {remoteWorkspaceFolder} {containerId} claude --dangerously-skip-permissions";
            }
        }
        else
        {
            remoteCmd = $"docker exec -it -w {remoteWorkspaceFolder} {containerId} claude --dangerously-skip-permissions";
        }

        var psi = new ProcessStartInfo("ssh")
        {
            Arguments = $"{interactiveSshArgs} -t {target.Username}@{target.Host} \"{remoteCmd}\"",
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            Console.MarkupLine("[red]Failed to start SSH process.[/]");
            return;
        }

        await proc.WaitForExitAsync();

        // Post-quit lifecycle prompt — only on clean exit (don't badger the user after a crash)
        if (proc.ExitCode == 0)
        {
            await PromptPostQuitActionAsync(target, interactiveSshArgs, containerId, projectName);
        }
    }

    private async Task PromptPostQuitActionAsync(
        SshTarget target, string sshArgs, string containerId, string projectName)
    {
        Console.WriteLine();

        const string KeepOpt = "Keep it running (default — fast reattach)";
        const string StopOpt = "Stop the container (free CPU/RAM, keep state)";
        const string RemoveOpt = "Remove the container (full cleanup — runs destroy flow)";

        var action = Console.Prompt(
            new SelectionPrompt<string>()
                .Title($"[cyan]claude session ended. What should we do with[/] [yellow]{Markup.Escape(containerId[..Math.Min(12, containerId.Length)])}[/][cyan]?[/]")
                .AddChoices(KeepOpt, StopOpt, RemoveOpt));

        bool containerGone = false;

        if (action == KeepOpt)
        {
            DisplayInfo("Container left running. Reattach with: pks claude");
            return;
        }
        else if (action == StopOpt)
        {
            await WithSpinnerAsync($"Stopping container {containerId[..Math.Min(12, containerId.Length)]}...", async () =>
                await RunSshCommandAsync(sshArgs, target, $"docker stop {containerId}", timeoutSeconds: 60));
            DisplaySuccess($"Container {containerId[..Math.Min(12, containerId.Length)]} stopped");
            containerGone = true;
        }
        else if (action == RemoveOpt)
        {
            var ok = await DestroyRemoteContainerAsync(target, sshArgs, containerId, projectName);
            if (!ok) return;
            containerGone = true;
        }

        if (containerGone)
        {
            await OfferToStopVmAsync(target, sshArgs);
        }
    }

    /// <summary>
    /// Removes the container and any docker volumes that were attached to it
    /// (mirrors what `pks devcontainer destroy` does for a remote container).
    /// </summary>
    private async Task<bool> DestroyRemoteContainerAsync(
        SshTarget target, string sshArgs, string containerId, string projectName)
    {
        // Collect volumes from `docker inspect` Mounts before removing the container.
        var volumes = new List<string>();
        var inspect = await RunSshCommandAsync(sshArgs, target,
            $"docker inspect --format '{{{{json .Mounts}}}}' {containerId}", timeoutSeconds: 15);
        if (inspect.Success)
        {
            try
            {
                var mounts = JsonDocument.Parse(inspect.Output.Trim()).RootElement;
                foreach (var m in mounts.EnumerateArray())
                {
                    if (m.TryGetProperty("Type", out var t) && t.GetString() == "volume" &&
                        m.TryGetProperty("Name", out var n))
                    {
                        var vol = n.GetString();
                        if (!string.IsNullOrEmpty(vol)) volumes.Add(vol);
                    }
                }
            }
            catch { /* best-effort */ }
        }

        Console.WriteLine();
        DisplayWarning($"Will remove container {containerId[..Math.Min(12, containerId.Length)]}" +
            (volumes.Count > 0 ? $" and {volumes.Count} volume(s)" : ""));
        foreach (var v in volumes)
            Console.MarkupLine($"  [dim]• volume {Markup.Escape(v)}[/]");

        if (!Console.Confirm("[red]Confirm destroy?[/]", defaultValue: false))
        {
            DisplayInfo("Destroy cancelled — container left running.");
            return false;
        }

        await WithSpinnerAsync("Removing container...", async () =>
            await RunSshCommandAsync(sshArgs, target, $"docker rm -f {containerId}", timeoutSeconds: 60));
        DisplaySuccess("Container removed");

        foreach (var vol in volumes)
        {
            await WithSpinnerAsync($"Removing volume {vol}...", async () =>
                await RunSshCommandAsync(sshArgs, target, $"docker volume rm {vol}", timeoutSeconds: 30));
            DisplaySuccess($"Volume {vol} removed");
        }

        return true;
    }

    /// <summary>
    /// If the SSH target maps to an Azure VM we can manage, list the OTHER running
    /// containers on the host first, then offer to deallocate the VM.
    /// </summary>
    private async Task OfferToStopVmAsync(SshTarget target, string sshArgs)
    {
        var vms = await _vmMetadata.ListAsync();
        var vmRecord = vms.FirstOrDefault(v =>
            string.Equals(v.VmName, target.Label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.PublicIpAddress, target.Host, StringComparison.OrdinalIgnoreCase));
        if (vmRecord == null) return;
        if (!await _azureAuth.IsAuthenticatedAsync()) return;

        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token)) return;

        // Show what else is running on the VM so the user can make an informed call.
        var ps = await RunSshCommandAsync(sshArgs, target,
            "docker ps --format '{{.ID}}\\t{{.Names}}\\t{{.Status}}\\t{{.Image}}'", timeoutSeconds: 10);

        Console.WriteLine();
        var others = ps.Success
            ? ps.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\t')).Where(p => p.Length >= 3).ToList()
            : new List<string[]>();

        if (others.Count == 0)
        {
            Console.MarkupLine($"[dim]No other containers running on {Markup.Escape(target.Label ?? target.Host)}.[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[yellow]Still running on {Markup.Escape(target.Label ?? target.Host)}[/]")
                .AddColumn("ID").AddColumn("Name").AddColumn("Status").AddColumn("Image");
            foreach (var p in others)
                table.AddRow(p[0].EscapeMarkup(), p[1].EscapeMarkup(), p[2].EscapeMarkup(),
                    p.Length > 3 ? p[3].EscapeMarkup() : "");
            Console.Write(table);
        }
        Console.WriteLine();

        var stopVm = Console.Confirm(
            $"[cyan]Stop (deallocate) the VM[/] [yellow]{Markup.Escape(vmRecord.VmName)}[/][cyan]? Saves Azure compute cost; cold-start adds ~30s next time.[/]",
            defaultValue: false);
        if (!stopVm) return;

        Exception? err = null;
        await WithSpinnerAsync($"Deallocating VM {vmRecord.VmName}...", async () =>
        {
            try { await _vmService.DeallocateVmAsync(token, vmRecord.SubscriptionId, vmRecord.ResourceGroup, vmRecord.VmName); }
            catch (Exception ex) { err = ex; }
        });

        if (err != null)
            DisplayError($"Failed to deallocate VM: {err.Message.EscapeMarkup()}");
        else
            DisplaySuccess($"VM '{vmRecord.VmName}' deallocate command accepted (Azure may take ~30s to fully stop billing).");
    }
}
