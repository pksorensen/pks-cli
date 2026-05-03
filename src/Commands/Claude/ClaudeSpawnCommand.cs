using PKS.Commands.Devcontainer;
using PKS.Infrastructure.Services;
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

    public ClaudeSpawnCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService, console)
    {
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
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

        var remoteCmd = $"docker exec -it -w {remoteWorkspaceFolder} {containerId} claude";

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
