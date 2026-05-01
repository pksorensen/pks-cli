using PKS.Commands.Devcontainer;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace PKS.Commands.Vibecast;

[Description("Spawn a devcontainer on a remote SSH target and connect via vibecast")]
public class VibecastCommand : DevcontainerSpawnCommand
{
    public VibecastCommand(
        IDevcontainerSpawnerService spawnerService,
        ISshTargetConfigurationService sshTargetService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAnsiConsole console)
        : base(spawnerService, sshTargetService, nugetTemplateService, vmMetadata, azureAuth, vmService, console)
    {
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

        var remoteCmd = $"docker exec -it -w {remoteWorkspaceFolder} {containerId} npx -y vibecast";

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
    }
}
