using PKS.Commands.Devcontainer;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PKS.Commands.Vibecast;

[Description("Spawn a devcontainer on a remote SSH target and connect via vibecast")]
public class VibecastCommand : DevcontainerSpawnCommand
{
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;

    public VibecastCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        VmInitCommand vmInitCommand,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService, vmInitCommand, console)
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

        Console.MarkupLine($"[dim]Attaching vibecast to container {containerId[..Math.Min(12, containerId.Length)]}...[/]");

        // Interactive SSH: -t allocates a pseudo-TTY, no BatchMode so stdin works
        var interactiveSshArgs = $"-o StrictHostKeyChecking=no -p {target.Port}";
        if (!string.IsNullOrEmpty(target.KeyPath))
            interactiveSshArgs += $" -i \"{target.KeyPath}\"";

        // If a vibecast binary was embedded at build time (-p:EmbedVibecast=true),
        // inject it into the container and use that instead of npx. Lets local builds
        // run against existing remote devcontainers without publishing to npm.
        var embeddedVibecastPath = await TryInjectEmbeddedVibecastViaSshAsync(
            target, interactiveSshArgs, containerId);

        var vibecastInvocation = embeddedVibecastPath ?? "npx -y vibecast";
        var extraArgs = GetExtraVibecastArgs(settings);
        if (!string.IsNullOrEmpty(extraArgs))
            vibecastInvocation += " " + extraArgs;
        // -e LANG/-e LC_ALL: minimal devcontainers default to POSIX locale; without UTF-8
        // some lib in vibecast's dep tree reads $LANG at init() and changes rendering, causing
        // glyphs like ↑↓⏎●◀▶ in reverse-video to render as "__". Setting them here is the only
        // reliable fix because Go's init order can't be controlled from inside the binary.
        var remoteCmd = $"docker exec -e LANG=C.UTF-8 -e LC_ALL=C.UTF-8 -it -w {remoteWorkspaceFolder} {containerId} {vibecastInvocation}";

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
                .Title($"[cyan]vibecast session ended. What should we do with[/] [yellow]{Markup.Escape(containerId[..Math.Min(12, containerId.Length)])}[/][cyan]?[/]")
                .AddChoices(KeepOpt, StopOpt, RemoveOpt));

        bool containerGone = false;

        if (action == KeepOpt)
        {
            DisplayInfo("Container left running. Reattach with: pks vibecast");
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

    /// <summary>
    /// Override in subclasses to append extra flags to the vibecast invocation.
    /// </summary>
    protected virtual string GetExtraVibecastArgs(Settings settings) => "";

    /// <summary>
    /// If pks-cli was built with -p:EmbedVibecast=true, pipe the embedded linux-amd64
    /// vibecast binary through SSH into the remote container at /tmp/vibecast-embedded
    /// and return that path. Returns null when no embedded binary is present or injection
    /// fails — caller should then fall back to npx -y vibecast.
    /// </summary>
    private async Task<string?> TryInjectEmbeddedVibecastViaSshAsync(
        SshTarget target, string sshArgs, string containerId)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("vibecast-linux-amd64");
        if (stream == null)
            return null;

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        Console.MarkupLine($"[cyan]Injecting embedded vibecast ({ms.Length / 1024} KB) into remote container...[/]");

        const string dest = "/tmp/vibecast-embedded";
        // Outer SSH wraps in double quotes; use single quotes for the inner bash -c argument.
        var injectCmd = $"docker exec -i {containerId} bash -c 'cat > {dest} && chmod +x {dest}'";

        var psi = new ProcessStartInfo("ssh")
        {
            Arguments = $"{sshArgs} {target.Username}@{target.Host} \"{injectCmd}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            ms.Position = 0;
            await ms.CopyToAsync(proc.StandardInput.BaseStream);
            await proc.StandardInput.BaseStream.FlushAsync();
            proc.StandardInput.Close();

            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                Console.MarkupLine($"[yellow]Embedded vibecast inject failed (exit {proc.ExitCode}): {stderr.Trim().EscapeMarkup()} — falling back to npx -y vibecast[/]");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.MarkupLine($"[yellow]Embedded vibecast inject error: {ex.Message.EscapeMarkup()} — falling back to npx -y vibecast[/]");
            return null;
        }

        Console.MarkupLine($"[green]✓ Embedded vibecast active at {dest}[/]");
        return dest;
    }
}
