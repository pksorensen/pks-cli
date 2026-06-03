using PKS.Commands.Devcontainer;
using PKS.Commands.Vm;
using PKS.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Claude;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PKS.Commands.Claude;

[Description("Spawn a devcontainer on a remote SSH target and connect via claude")]
public class ClaudeSpawnCommand : DevcontainerSpawnCommand
{
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly AzureFoundryAuthConfig _foundryConfig;
    private readonly IAzureDevOpsAuthService _adoAuthService;
    private readonly IConfigurationService _configService;

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
        IAzureDevOpsAuthService adoAuthService,
        IConfigurationService configService,
        IActionGuard actionGuard,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService,
               vmInitCommand, claudeMarketplaceConfigService, claudeManagedSettingsRenderer, foundryAuthService, actionGuard, console)
    {
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _foundryConfig = foundryConfig;
        _adoAuthService = adoAuthService;
        _configService = configService;
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

        var foundryEnvArgs = await BuildFoundryEnvArgsAsync(sshArgs, target);
        string remoteCmd;
        if (!string.IsNullOrEmpty(foundryEnvArgs))
        {
            await PersistFoundryEnvToContainerAsync(sshArgs, target, containerId, foundryEnvArgs);
            remoteCmd = $"docker exec -it {foundryEnvArgs} -w {remoteWorkspaceFolder} {containerId} claude --dangerously-skip-permissions";
        }
        else
            remoteCmd = $"docker exec -it -w {remoteWorkspaceFolder} {containerId} claude --dangerously-skip-permissions";

        // ADO git proxy — deploy if repos are registered and user selects at least one
        await TryDeployAdoGitProxyAsync(sshArgs, target, containerId, remoteWorkspaceFolder);

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
            await PromptPostQuitActionAsync(
                target, interactiveSshArgs, containerId, projectName,
                remoteWorkspaceFolder, volumeName);
        }
    }

    private async Task TryDeployAdoGitProxyAsync(string sshArgs, SshTarget target, string containerId, string workspaceFolder)
    {
        if (!await _adoAuthService.IsAuthenticatedAsync()) return;

        var raw = await _configService.GetAsync("ado.git.repos");
        if (string.IsNullOrWhiteSpace(raw)) return;

        List<AdoGitRepo> repos;
        try { repos = JsonSerializer.Deserialize<List<AdoGitRepo>>(raw) ?? []; }
        catch { return; }

        if (repos.Count == 0) return;

        // Let user pick which repos this container session gets access to
        var selected = Console.Prompt(
            new MultiSelectionPrompt<AdoGitRepo>()
                .Title("[cyan]Select ADO repos to enable in this container:[/]")
                .NotRequired()
                .UseConverter(r => $"{r.Org} / {r.Project} / {r.Repo}")
                .AddChoices(repos));

        if (selected.Count == 0) return;

        var adoCreds = await _adoAuthService.GetStoredCredentialsAsync();
        if (adoCreds == null) return;

        // Copy minimal creds (TenantId + RefreshToken) to the VM
        var payload = JsonSerializer.Serialize(new { adoCreds.TenantId, adoCreds.RefreshToken });
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        await RunSshCommandAsync(sshArgs, target,
            $"mkdir -p ~/.pks-cli && echo {b64} | base64 -d > ~/.pks-cli/ado-credentials.json && chmod 600 ~/.pks-cli/ado-credentials.json",
            timeoutSeconds: 10);

        // Ensure pks is available on the VM (embedded binary or dotnet tool install)
        var pksPath = await EnsurePksOnVmAsync(sshArgs, target);

        // Kill any stale proxy on 7878 and start fresh with the per-container allowlist
        var allowArgs = string.Join(" ", selected.Select(r => $"--allow '{r.AllowKey}'"));
        await RunSshCommandAsync(sshArgs, target,
            $"fuser -k 7878/tcp 2>/dev/null || true; sleep 0.3; nohup {pksPath} ado git-proxy {allowArgs} >/tmp/pks-ado-proxy.log 2>&1 &",
            timeoutSeconds: 10);
        await Task.Delay(500);

        // Configure git URL rewrites inside the container.
        // Cover both forms ADO clone URLs appear in:
        //   https://dev.azure.com/...          (API/script form)
        //   https://OrgName@dev.azure.com/...  (browser copy-paste form)
        await RunSshCommandAsync(sshArgs, target,
            $"docker exec {containerId} git config --global url.'http://172.17.0.1:7878/'.insteadOf 'https://dev.azure.com/'",
            timeoutSeconds: 10);

        var orgs = selected.Select(r => r.Org).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var org in orgs)
        {
            await RunSshCommandAsync(sshArgs, target,
                $"docker exec {containerId} git config --global url.'http://172.17.0.1:7878/'.insteadOf 'https://{org}@dev.azure.com/'",
                timeoutSeconds: 10);
        }

        // Inject a CLAUDE.md hint so Claude knows to omit the username prefix when composing ADO URLs
        var orgList = string.Join(", ", orgs.Select(o => $"`{o}`"));
        var claudeMdNote = $"""


## Azure DevOps Git Access

A local proxy handles ADO authentication — no credentials needed inside this container.

When cloning or referencing ADO repos, **always omit the username prefix**:

- Correct:   `git clone https://dev.azure.com/Org/Project/_git/Repo`
- Incorrect: `git clone https://OrgName@dev.azure.com/Org/Project/_git/Repo`

Orgs available in this session: {orgList}
""";
        var noteB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(claudeMdNote));
        await RunSshCommandAsync(sshArgs, target,
            $"docker exec {containerId} bash -c 'echo {noteB64} | base64 -d >> {workspaceFolder}/CLAUDE.md 2>/dev/null || true'",
            timeoutSeconds: 10);

        Console.MarkupLine($"[dim]ADO git proxy started ({selected.Count} repo(s) enabled). Logs: /tmp/pks-ado-proxy.log[/]");
    }

    /// <summary>
    /// Ensures the pks binary is available on the VM and returns the path to invoke it.
    /// Priority:
    ///   1. Embedded linux-x64 binary (present when built with -p:EmbedPksLinux=true) →
    ///      piped via SSH stdin to ~/.pks-cli/pks, chmod +x
    ///   2. Already installed on the VM (which pks succeeds) → use as-is
    ///   3. Fallback: dotnet tool install -g pks-cli on the VM
    /// </summary>
    private async Task<string> EnsurePksOnVmAsync(string sshArgs, SshTarget target)
    {
        const string embeddedDest = "~/.pks-cli/pks";

        // 1. Try embedded binary
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("pks-linux-x64");
        if (stream != null)
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            Console.MarkupLine($"[dim]Injecting embedded pks ({ms.Length / 1024} KB) to VM...[/]");

            var injectCmd = $"mkdir -p ~/.pks-cli && cat > {embeddedDest} && chmod +x {embeddedDest}";
            var psi = new ProcessStartInfo("ssh")
            {
                Arguments = $"-o StrictHostKeyChecking=no -p {target.Port}" +
                            (!string.IsNullOrEmpty(target.KeyPath) ? $" -i \"{target.KeyPath}\"" : "") +
                            $" {target.Username}@{target.Host} \"{injectCmd}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    ms.Position = 0;
                    await ms.CopyToAsync(proc.StandardInput.BaseStream);
                    proc.StandardInput.Close();
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode == 0)
                        return embeddedDest;
                }
            }
            catch { /* fall through to next option */ }
        }

        // 2. Already installed on the VM?
        var check = await RunSshCommandAsync(sshArgs, target,
            "which pks 2>/dev/null || echo NOT_FOUND", timeoutSeconds: 5);
        if (check.Success && !check.Output.Trim().EndsWith("NOT_FOUND"))
            return "pks";

        // 3. Install from NuGet
        Console.MarkupLine("[dim]Installing pks-cli on VM via dotnet tool install...[/]");
        await RunSshCommandAsync(sshArgs, target,
            "dotnet tool install -g pks-cli 2>/dev/null || dotnet tool update -g pks-cli",
            timeoutSeconds: 120);

        return "pks";
    }

    private async Task PersistFoundryEnvToContainerAsync(
        string sshArgs, SshTarget target, string containerId, string envArgs)
    {
        // Parse "-e VAR=val" pairs from the docker exec env args string
        var exports = new StringBuilder();
        foreach (Match m in Regex.Matches(envArgs, @"-e\s+([A-Z_]+)=(\S+)"))
            exports.AppendLine($"export {m.Groups[1].Value}='{m.Groups[2].Value}'");

        if (exports.Length == 0) return;

        // Write to /home/node/.pks-foundry and source it from .zshrc/.bashrc
        var content = $"# PKS Foundry — written by pks at spawn time\n{exports}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        await RunSshCommandAsync(sshArgs, target,
            $"docker exec {containerId} bash -c 'echo {b64} | base64 -d > /home/node/.pks-foundry'",
            timeoutSeconds: 10);

        // Append source line once to .zshrc and .bashrc
        var sourceCmd = "grep -qxF '. /home/node/.pks-foundry' /home/node/.zshrc 2>/dev/null || echo '. /home/node/.pks-foundry' >> /home/node/.zshrc; " +
                        "grep -qxF '. /home/node/.pks-foundry' /home/node/.bashrc 2>/dev/null || echo '. /home/node/.pks-foundry' >> /home/node/.bashrc";
        await RunSshCommandAsync(sshArgs, target,
            $"docker exec {containerId} bash -c '{sourceCmd}'",
            timeoutSeconds: 10);
    }

    private async Task PromptPostQuitActionAsync(
        SshTarget target, string sshArgs, string containerId, string projectName,
        string remoteWorkspaceFolder, string volumeName)
    {
        Console.WriteLine();

        const string VsCodeOpt = "Open in VS Code (attach to running container)";
        const string KeepOpt = "Keep it running (default — fast reattach)";
        const string StopOpt = "Stop the container (free CPU/RAM, keep state)";
        const string RemoveOpt = "Remove the container (full cleanup — runs destroy flow)";

        var action = Console.Prompt(
            new SelectionPrompt<string>()
                .Title($"[cyan]claude session ended. What should we do with[/] [yellow]{Markup.Escape(containerId[..Math.Min(12, containerId.Length)])}[/][cyan]?[/]")
                .AddChoices(VsCodeOpt, KeepOpt, StopOpt, RemoveOpt));

        bool containerGone = false;

        if (action == VsCodeOpt)
        {
            var (detectedHostPath, detectedVolumeName) = await DetectDevContainerMountAsync(
                sshArgs, target, containerId, projectName);
            await LaunchVsCodeRemoteAsync(
                target, sshArgs, remoteWorkspaceFolder, Console,
                hostPath: detectedHostPath, volumeName: detectedVolumeName, projectFolder: projectName);
            DisplayInfo("Container left running. Reattach with: pks claude");
            return;
        }
        else if (action == KeepOpt)
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

        // Honor the vm.stop policy (off by default) — the Confirm above is agent-answerable; this isn't.
        if (!await TryRequireAsync(new ActionRequest(ActionIds.VmStop, $"Stop (deallocate) VM '{vmRecord.VmName}'")))
            return;

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
